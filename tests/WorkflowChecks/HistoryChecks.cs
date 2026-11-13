using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using rabotyagi.DataAccess;
using rabotyagi.DataAccess.Entities;
using rabotyagi.DataAccess.Enums;

internal static class HistoryChecks
{
    public static async Task Run(string connectionString, Guid orderId, Guid workerId,
        HttpClient client, HttpClient admin, HttpClient dispatcher, HttpClient winner,
        HttpClient loser, HttpClient outsider, HttpClient anonymous, Action<bool, string> check)
    {
        // Never run SQL fault injection against a configured application database.
        if (!new NpgsqlConnectionStringBuilder(connectionString).Database!.StartsWith("rabotyagi_checks_"))
            throw new InvalidOperationException("History checks require an isolated test database.");
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);
        check(!db.Database.HasPendingModelChanges(), "migration snapshot matches the runtime model");
        async Task<JsonElement> History(HttpClient actor, Guid id)
        {
            using var response = await actor.GetAsync($"order/{id}/history");
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        }
        var fullHistory = await History(client, orderId);
        check(fullHistory.EnumerateArray().Count(x => x.GetProperty("eventType").GetString() is "priceProposed" or "priceAccepted") == 2,
            "workflow records proposal and client acceptance");
        var history = JsonSerializer.SerializeToElement(fullHistory.EnumerateArray()
            .Where(x => x.GetProperty("eventType").GetString() is not ("priceProposed" or "priceAccepted")).ToArray());
        string[] types = ["created", "statusChanged", "executorAssigned", "statusChanged", "reportSubmitted",
            "workReturnedByClient", "reportSubmitted", "workAcceptedByClient"];
        check(history.EnumerateArray().Select(x => x.GetProperty("eventType").GetString()).SequenceEqual(types),
            "workflow history contains exactly the successful actions, including one concurrent assignment");
        string?[] previous = [null, "created", "published", "workerOnTheWay", "inProgress", "workFinished", "rework", "workFinished"];
        string[] next = ["created", "published", "workerOnTheWay", "inProgress", "workFinished", "rework", "workFinished", "completed"];
        DateTimeOffset? last = null;
        for (var i = 0; i < history.GetArrayLength(); i++)
        {
            var item = history[i];
            var time = item.GetProperty("occurredAt").GetDateTimeOffset();
            check(time.Offset == TimeSpan.Zero && time <= DateTimeOffset.UtcNow && (!last.HasValue || time >= last), "history is chronological UTC");
            check(item.GetProperty("actorUserId").GetGuid() != Guid.Empty && !string.IsNullOrWhiteSpace(item.GetProperty("actorNameSnapshot").GetString()), "history has actor identity snapshot");
            check(item.GetProperty("previousStatus").GetString() == previous[i] && item.GetProperty("newStatus").GetString() == next[i], "history records actual status transition");
            last = time;
        }
        check(history[2].GetProperty("executorId").GetGuid() == workerId, "assignment records the winning executor");
        check(history[5].GetProperty("comment").GetString() == "Нужен другой ракурс", "old feedback survives report resubmission and approval");
        foreach (var actor in new[] { admin, dispatcher, winner })
            check((await History(actor, orderId)).GetArrayLength() == fullHistory.GetArrayLength(), "authorized role can read history");
        foreach (var actor in new[] { loser, outsider })
        {
            using var response = await actor.GetAsync($"order/{orderId}/history");
            check(response.StatusCode == HttpStatusCode.NotFound, "unrelated user cannot read history");
        }
        using (var response = await anonymous.GetAsync($"order/{orderId}/history"))
            check(response.StatusCode == HttpStatusCode.Unauthorized, "anonymous history is unauthorized");

        var source = await db.Orders.AsNoTracking().SingleAsync(x => x.Id == orderId);
        var legacy = new Order { InitiatorId = source.InitiatorId, AddressId = source.AddressId,
            Comment = "Existing order without history", OrderType = source.OrderType };
        db.Orders.Add(legacy);
        await db.SaveChangesAsync();
        check((await History(client, legacy.Id)).GetArrayLength() == 0, "legacy orders have no fabricated events");

        // Force only the new INSERT to fail; the existing order mutation must roll back too.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_history() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'Injected history insert failure'; END $$;
            CREATE TRIGGER reject_history BEFORE INSERT ON "OrderHistoryEvents"
            FOR EACH ROW EXECUTE FUNCTION reject_history();
            """);
        try
        {
            var count = await db.Orders.CountAsync();
            using var create = await client.PostAsJsonAsync("order", new { title = "Failed create", comment = "Failure test", orderType = "sanitary", addressText = "Test address" });
            check(create.StatusCode == HttpStatusCode.InternalServerError && await db.Orders.CountAsync() == count, "creation rolls back if history fails");
            using var status = await admin.PatchAsJsonAsync("order/status", new { orderId = legacy.Id, status = "published" });
            check(status.StatusCode == HttpStatusCode.InternalServerError && await db.Orders.AsNoTracking().Where(x => x.Id == legacy.Id).Select(x => x.Status).SingleAsync() == EOrderStatus.Created,
                "status change rolls back if history fails");
            await db.Orders.Where(x => x.Id == legacy.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, EOrderStatus.Published));
            using var assignment = await winner.PatchAsJsonAsync("order/executor", new { orderId = legacy.Id, executorId = workerId });
            var unchanged = await db.Orders.AsNoTracking().SingleAsync(x => x.Id == legacy.Id);
            check(assignment.StatusCode == HttpStatusCode.InternalServerError && unchanged.ExecutorId == null && unchanged.Status == EOrderStatus.Published,
                "conditional executor update rolls back if history fails");
            await db.Orders.Where(x => x.Id == legacy.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, EOrderStatus.InProgress).SetProperty(x => x.ExecutorId, workerId));
            var photoCount = await db.OrderPhotos.CountAsync();
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("Report rollback test"), "Summary");
            foreach (var field in new[] { "WorkDone", "WorkplaceClean", "ClientInformed" })
                form.Add(new StringContent("true"), field);
            form.Add(new ByteArrayContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jX1sAAAAASUVORK5CYII=")), "Photos", "test.png");
            using var report = await winner.PostAsync($"order/{legacy.Id}/report", form);
            unchanged = await db.Orders.AsNoTracking().SingleAsync(x => x.Id == legacy.Id);
            check(report.StatusCode == HttpStatusCode.InternalServerError && unchanged.Status == EOrderStatus.InProgress
                && unchanged.ReportSubmittedAt == null && await db.OrderPhotos.CountAsync() == photoCount,
                "report and photo records roll back if history fails");
            await db.Orders.Where(x => x.Id == legacy.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, EOrderStatus.WorkFinished).SetProperty(x => x.ReportSubmittedAt, DateTimeOffset.UtcNow));
            using var review = await client.PostAsJsonAsync($"order/{legacy.Id}/review", new { approved = true,
                expectedUpdated = (await db.Orders.AsNoTracking().SingleAsync(x => x.Id == legacy.Id)).Updated });
            unchanged = await db.Orders.AsNoTracking().SingleAsync(x => x.Id == legacy.Id);
            check(review.StatusCode == HttpStatusCode.InternalServerError && unchanged.Status == EOrderStatus.WorkFinished
                && unchanged.ReviewedAt == null, "review rolls back if history fails");
            check((await History(client, legacy.Id)).GetArrayLength() == 0, "failed mutations leave no events");
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_history ON \"OrderHistoryEvents\"; DROP FUNCTION reject_history();");
        }
        await db.Orders.Where(x => x.Id == legacy.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, EOrderStatus.Published));
        check((await History(loser, legacy.Id)).GetArrayLength() == 0, "workers can read history of published orders");
        // Dedicated historical author: keep the workflow accounts usable in --serve mode.
        var actorId = Guid.NewGuid();
        db.Users.Add(new User { Id = actorId, Name = "Historical", Surname = "Author" });
        db.OrderHistoryEvents.Add(new OrderHistoryEvent { OrderId = legacy.Id,
            ActorUserId = actorId, ActorNameSnapshot = "Historical Author",
            EventType = EOrderHistoryEventType.StatusChanged, PreviousStatus = EOrderStatus.Created,
            NewStatus = EOrderStatus.Published });
        await db.SaveChangesAsync();
        await db.Users.Where(x => x.Id == actorId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "Renamed").SetProperty(x => x.LockoutEnd, DateTimeOffset.UtcNow.AddDays(1)));
        check((await History(client, legacy.Id))[0].GetProperty("actorNameSnapshot").GetString() == "Historical Author", "rename and block do not rewrite history");
        await db.Users.Where(x => x.Id == actorId).ExecuteDeleteAsync();
        var afterDelete = await History(client, legacy.Id);
        check(afterDelete[0].GetProperty("actorUserId").GetGuid() == actorId && afterDelete[0].GetProperty("actorNameSnapshot").GetString() == "Historical Author", "deleted author's identity survives");
    }
}
