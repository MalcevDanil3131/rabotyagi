using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using rabotyagi.DataAccess;
using rabotyagi.DataAccess.Entities;
using rabotyagi.DataAccess.Enums;

internal static class ClientAcceptanceChecks
{
    public static async Task Run(string connection, Guid sourceId, HttpClient client, HttpClient admin,
        HttpClient dispatcher, HttpClient worker, HttpClient outsider, HttpClient anonymous, Action<bool, string> check)
    {
        if (!new NpgsqlConnectionStringBuilder(connection).Database!.StartsWith("rabotyagi_checks_"))
            throw new InvalidOperationException("Acceptance checks require an isolated test database.");
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        var source = await db.Orders.AsNoTracking().SingleAsync(x => x.Id == sourceId);
        async Task<Order> Fresh(EOrderStatus status = EOrderStatus.WorkFinished, bool report = true)
        {
            var row = new Order { InitiatorId = source.InitiatorId, ExecutorId = source.ExecutorId, AddressId = source.AddressId,
                Comment = "Client acceptance check", Status = status, OrderType = source.OrderType,
                ReportSubmittedAt = report ? DateTimeOffset.UtcNow : null, ReportSummary = "Report",
                PriceAmount = source.PriceAmount, PriceStatus = source.PriceStatus, PriceExecutorId = source.ExecutorId };
            db.Orders.Add(row); await db.SaveChangesAsync(); return await Read(row.Id);
        }
        Task<Order> Read(Guid id) => db.Orders.AsNoTracking().SingleAsync(x => x.Id == id);
        Task<HttpResponseMessage> Decide(HttpClient actor, Order row, bool accepted = true, string? reason = null) =>
            actor.PostAsJsonAsync($"order/{row.Id}/review", new { approved = accepted, feedback = reason, expectedUpdated = row.Updated });
        async Task Expect(Task<HttpResponseMessage> task, HttpStatusCode expected, string text)
        { using var response = await task; check(response.StatusCode == expected, text + $" ({(int)response.StatusCode})"); }
        var order = await Fresh();
        foreach (var actor in new[] { admin, dispatcher, worker })
            await Expect(Decide(actor, order), HttpStatusCode.Forbidden, "staff and executor cannot accept on behalf of owner");
        await Expect(Decide(outsider, order), HttpStatusCode.NotFound, "outsider cannot discover or review report");
        await Expect(Decide(anonymous, order), HttpStatusCode.Unauthorized, "anonymous cannot review report");
        await Expect(client.PostAsJsonAsync($"order/{order.Id}/review", new { approved = true }), HttpStatusCode.BadRequest, "version required");
        await Expect(client.PostAsJsonAsync($"order/{order.Id}/review", new { expectedUpdated = order.Updated }), HttpStatusCode.BadRequest, "explicit decision required");
        await Expect(Decide(client, order, false, "  "), HttpStatusCode.BadRequest, "return requires meaningful feedback");
        await Expect(Decide(client, order, false, new string('x', 2001)), HttpStatusCode.BadRequest, "feedback length limited");
        await Expect(Decide(client, order, false, "  Исправьте крепление  "), HttpStatusCode.OK, "client returns report directly");
        var returned = await Read(order.Id);
        var entry = await db.OrderHistoryEvents.AsNoTracking().SingleAsync(x => x.OrderId == order.Id);
        check(returned.Status == EOrderStatus.Rework && returned.ReviewFeedback == "Исправьте крепление"
            && returned.ReviewerId == source.InitiatorId && entry.ActorUserId == source.InitiatorId
            && entry.EventType == EOrderHistoryEventType.WorkReturnedByClient && entry.PriceAmount == source.PriceAmount,
            "return persists owner, reason, amount and status together");
        await Expect(Decide(client, order), HttpStatusCode.Conflict, "old report version cannot be accepted after return");
        // Simulate a subsequent report version in this disposable fixture; full HTTP resubmission is checked by Program.
        await db.Orders.Where(x => x.Id == order.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, EOrderStatus.WorkFinished)
            .SetProperty(x => x.Updated, DateTimeOffset.UtcNow).SetProperty(x => x.ReportSummary, "Corrected report"));
        await Expect(Decide(client, order), HttpStatusCode.Conflict, "stale browser cannot accept a newer report");
        order = await Read(order.Id);
        await Expect(Decide(client, order), HttpStatusCode.OK, "client accepts current report directly");
        var completed = await Read(order.Id);
        check(completed.Status == EOrderStatus.Completed && completed.ReviewerId == source.InitiatorId
            && completed.ReviewedAt?.Offset == TimeSpan.Zero && completed.PriceAmount == source.PriceAmount,
            "acceptance immediately completes order and preserves agreed amount");
        await Expect(Decide(client, order), HttpStatusCode.Conflict, "duplicate acceptance is rejected");
        foreach (var status in Enum.GetValues<EOrderStatus>().Where(x => x is not (EOrderStatus.WorkFinished or EOrderStatus.Approved)))
            await Expect(Decide(client, await Fresh(status)), HttpStatusCode.Conflict, "review forbidden outside awaiting-client states");
        await Expect(Decide(client, await Fresh(report: false)), HttpStatusCode.Conflict, "cannot accept without submitted report");
        var legacy = await Fresh(EOrderStatus.Approved);
        await Expect(admin.PatchAsJsonAsync("order/status", new { orderId = legacy.Id, status = "completed" }), HttpStatusCode.Forbidden,
            "legacy admin completion cannot bypass owner decision");
        await Expect(Decide(client, legacy), HttpStatusCode.OK, "owner can complete legacy staff-approved order");
        var race = await Fresh();
        var responses = await Task.WhenAll(Decide(client, race), Decide(client, race, false, "Доработать"));
        check(responses.Count(x => x.IsSuccessStatusCode) == 1 && responses.Count(x => x.StatusCode == HttpStatusCode.Conflict) == 1
            && await db.OrderHistoryEvents.CountAsync(x => x.OrderId == race.Id) == 1,
            "concurrent accept and return record exactly one decision");
        foreach (var response in responses) response.Dispose();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_client_acceptance() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'Injected acceptance history failure'; END $$;
            CREATE TRIGGER reject_client_acceptance BEFORE INSERT ON "OrderHistoryEvents"
            FOR EACH ROW EXECUTE FUNCTION reject_client_acceptance();
            """);
        try
        {
            foreach (var accepted in new[] { true, false })
            {
                var failed = await Fresh();
                await Expect(Decide(client, failed, accepted, "Исправить"), HttpStatusCode.InternalServerError, "history failure reported");
                var unchanged = await Read(failed.Id);
                check(unchanged.Status == failed.Status && unchanged.Updated == failed.Updated && unchanged.ReviewerId == null
                    && unchanged.ReviewedAt == null && unchanged.ReviewFeedback == null
                    && !await db.OrderHistoryEvents.AnyAsync(x => x.OrderId == failed.Id), "failed decision rolls back order and history");
            }
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_client_acceptance ON \"OrderHistoryEvents\"; DROP FUNCTION reject_client_acceptance();"); }
    }
}
