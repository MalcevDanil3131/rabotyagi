using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using rabotyagi.DataAccess;
using rabotyagi.DataAccess.Entities;
using rabotyagi.DataAccess.Enums;

internal static class ReassignmentChecks
{
    public static async Task Run(string connectionString, Guid sourceId, Guid oldId, Guid newId,
        HttpClient client, HttpClient admin, HttpClient dispatcher, HttpClient oldWorker, HttpClient newWorker,
        HttpClient anonymous, Action<bool, string> check)
    {
        if (!new NpgsqlConnectionStringBuilder(connectionString).Database!.StartsWith("rabotyagi_checks_"))
            throw new InvalidOperationException("Reassignment checks require an isolated database.");
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);
        var source = await db.Orders.AsNoTracking().Include(x => x.Photos).SingleAsync(x => x.Id == sourceId);
        async Task<Order> Fixture(EOrderStatus status)
        {
            var order = new Order { InitiatorId = source.InitiatorId, AddressId = source.AddressId,
                ExecutorId = oldId, Status = status, Comment = "Reassignment test", OrderType = source.OrderType,
                ReportSummary = "Previous report", ReportSubmittedAt = DateTimeOffset.UtcNow.AddHours(-1),
                ReviewFeedback = "Previous feedback", ReviewedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
                PriceAmount = 1500, PriceScope = "Test work", PriceStatus = EOrderPriceStatus.Accepted,
                PriceExecutorId = oldId, PriceAcceptedAt = DateTimeOffset.UtcNow.AddHours(-1) };
            if (source.Photos.FirstOrDefault() is { } photo)
                order.Photos.Add(new OrderPhoto { FileName = photo.FileName, ContentType = photo.ContentType, OriginalName = photo.OriginalName });
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            // Round trip timestamps at PostgreSQL precision, just as API clients do.
            return await db.Orders.AsNoTracking().SingleAsync(x => x.Id == order.Id);
        }
        Task<HttpResponseMessage> Replace(HttpClient actor, Order order, Guid? target = null, string reason = "Исполнитель недоступен") =>
            actor.PatchAsJsonAsync($"order/{order.Id}/executor", new
            {
                executorId = target ?? newId, expectedExecutorId = oldId, expectedUpdated = order.Updated, reason
            });
        async Task Expect(HttpClient actor, Order order, HttpStatusCode code, Guid? target = null, string reason = "Исполнитель недоступен")
        {
            using var response = await Replace(actor, order, target, reason);
            check(response.StatusCode == code, $"replacement returns {(int)code}");
        }
        async Task Verify(Order before)
        {
            var after = await db.Orders.AsNoTracking().Include(x => x.Photos).SingleAsync(x => x.Id == before.Id);
            var history = await db.OrderHistoryEvents.AsNoTracking().SingleAsync(x => x.OrderId == before.Id);
            check(after.ExecutorId == newId && after.Status == EOrderStatus.WorkerOnTheWay, "replacement resets order to on-the-way");
            check(after.PriceAmount == null && after.PriceStatus == EOrderPriceStatus.None && after.PriceAcceptedAt == null
                && after.PriceExecutorId == null, "replacement requires a new price agreement");
            check(history.EventType == EOrderHistoryEventType.ExecutorChanged && history.PreviousExecutorId == oldId
                && history.ExecutorId == newId && !string.IsNullOrWhiteSpace(history.PreviousExecutorNameSnapshot)
                && !string.IsNullOrWhiteSpace(history.ExecutorNameSnapshot) && history.PreviousStatus == before.Status
                && history.NewStatus == EOrderStatus.WorkerOnTheWay && history.Comment == "Исполнитель недоступен",
                "replacement history records both identities, reason and status transition");
            check(after.ReportSummary == before.ReportSummary && after.ReviewFeedback == before.ReviewFeedback
                && after.ReportSubmittedAt == before.ReportSubmittedAt && after.Photos.Count == source.Photos.Count,
                "replacement preserves previous report and photos");
            using var oldAccess = await oldWorker.GetAsync($"order/{before.Id}/history");
            using var newAccess = await newWorker.GetAsync($"order/{before.Id}/history");
            check(oldAccess.StatusCode == HttpStatusCode.NotFound && newAccess.StatusCode == HttpStatusCode.OK,
                "history access follows the current executor");
            using var staleStart = await oldWorker.PatchAsJsonAsync("order/status", new { orderId = before.Id, status = "inProgress" });
            check(staleStart.StatusCode == HttpStatusCode.Forbidden, "former executor cannot start work");
            using var prematureReview = await client.PostAsJsonAsync($"order/{before.Id}/review", new { approved = true, expectedUpdated = after.Updated });
            check(prematureReview.StatusCode == HttpStatusCode.Conflict, "old report cannot be approved after replacement");
        }
        foreach (var status in new[] { EOrderStatus.WorkerOnTheWay, EOrderStatus.InProgress, EOrderStatus.Rework })
        {
            var order = await Fixture(status);
            await Expect(anonymous, order, HttpStatusCode.Unauthorized);
            await Expect(client, order, HttpStatusCode.Forbidden);
            await Expect(oldWorker, order, HttpStatusCode.Forbidden);
            await Expect(dispatcher, order, HttpStatusCode.BadRequest, reason: "   ");
            await Expect(dispatcher, order, HttpStatusCode.Conflict, target: oldId);
            await Expect(status == EOrderStatus.Rework ? admin : dispatcher, order, HttpStatusCode.OK);
            await Expect(dispatcher, order, HttpStatusCode.Conflict); // Repeated/stale submission.
            await Verify(order);
            using var blocked = await newWorker.PatchAsJsonAsync("order/status", new { orderId = order.Id, status = "inProgress" });
            check(blocked.StatusCode == HttpStatusCode.Conflict, "replacement cannot reuse the former executor's agreement");
            await PriceChecks.Agree(newWorker, client, order.Id, check);
            using var start = await newWorker.PatchAsJsonAsync("order/status", new { orderId = order.Id, status = "inProgress" });
            check(start.StatusCode == HttpStatusCode.OK, "new executor can start work");
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("New executor confirms the completed work"), "Summary");
            foreach (var field in new[] { "WorkDone", "WorkplaceClean", "ClientInformed" }) form.Add(new StringContent("true"), field);
            foreach (var photoId in await db.OrderPhotos.Where(x => x.OrderId == order.Id).Select(x => x.Id).ToListAsync())
                form.Add(new StringContent(photoId.ToString()), "KeepPhotoIds");
            using var report = await newWorker.PostAsync($"order/{order.Id}/report", form);
            check(report.StatusCode == HttpStatusCode.OK, "new executor can submit a fresh report");
        }
        foreach (var status in new[] { EOrderStatus.Created, EOrderStatus.Published, EOrderStatus.WorkFinished, EOrderStatus.Approved, EOrderStatus.Completed, EOrderStatus.Cancelled })
        {
            var order = await Fixture(status);
            await Expect(dispatcher, order, HttpStatusCode.Conflict);
            check(!await db.OrderHistoryEvents.AnyAsync(x => x.OrderId == order.Id), "forbidden transition writes no history");
        }
        var unavailable = await Fixture(EOrderStatus.InProgress);
        await Expect(dispatcher, unavailable, HttpStatusCode.Conflict, target: source.InitiatorId);
        await Expect(dispatcher, unavailable, HttpStatusCode.Conflict, target: Guid.NewGuid());
        var stale = await Fixture(EOrderStatus.WorkerOnTheWay);
        using (var start = await oldWorker.PatchAsJsonAsync("order/status", new { orderId = stale.Id, status = "inProgress" }))
            check(start.StatusCode == HttpStatusCode.OK, "old executor starts before replacement request");
        await Expect(dispatcher, stale, HttpStatusCode.Conflict);
        check(!await db.OrderHistoryEvents.AnyAsync(x => x.OrderId == stale.Id && x.EventType == EOrderHistoryEventType.ExecutorChanged),
            "stale version cannot overwrite a newer work transition");
        var availability = await db.Users.Where(x => x.Id == newId).Select(x => x.AvailableUntil).SingleAsync();
        await db.Users.Where(x => x.Id == newId).ExecuteUpdateAsync(s => s.SetProperty(x => x.AvailableUntil, (DateTimeOffset?)null));
        await Expect(dispatcher, unavailable, HttpStatusCode.Conflict);
        await db.Users.Where(x => x.Id == newId).ExecuteUpdateAsync(s => s.SetProperty(x => x.AvailableUntil, availability)
            .SetProperty(x => x.LockoutEnd, DateTimeOffset.UtcNow.AddDays(1)));
        await Expect(dispatcher, unavailable, HttpStatusCode.Conflict);
        await db.Users.Where(x => x.Id == newId).ExecuteUpdateAsync(s => s.SetProperty(x => x.LockoutEnd, (DateTimeOffset?)null));
        var raceOrder = await Fixture(EOrderStatus.InProgress);
        var race = await Task.WhenAll(Replace(dispatcher, raceOrder), Replace(admin, raceOrder));
        check(race.Count(x => x.StatusCode == HttpStatusCode.OK) == 1 && race.Count(x => x.StatusCode == HttpStatusCode.Conflict) == 1,
            "simultaneous replacements have exactly one winner");
        foreach (var response in race) response.Dispose();
        await Verify(raceOrder);

        var failed = await Fixture(EOrderStatus.Rework);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_reassignment_history() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'Injected replacement history failure'; END $$;
            CREATE TRIGGER reject_reassignment_history BEFORE INSERT ON "OrderHistoryEvents"
            FOR EACH ROW EXECUTE FUNCTION reject_reassignment_history();
            """);
        try
        {
            await Expect(dispatcher, failed, HttpStatusCode.InternalServerError);
            var after = await db.Orders.AsNoTracking().SingleAsync(x => x.Id == failed.Id);
            check(after.ExecutorId == oldId && after.Status == failed.Status && after.Updated == failed.Updated
                && !await db.OrderHistoryEvents.AnyAsync(x => x.OrderId == failed.Id), "history failure rolls back replacement entirely");
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_reassignment_history ON \"OrderHistoryEvents\"; DROP FUNCTION reject_reassignment_history();");
        }
    }
}
