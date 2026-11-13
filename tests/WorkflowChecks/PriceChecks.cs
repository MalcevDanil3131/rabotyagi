using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using rabotyagi.DataAccess;
using rabotyagi.DataAccess.Entities;
using rabotyagi.DataAccess.Enums;

internal static class PriceChecks
{
    public static async Task Agree(HttpClient worker, HttpClient client, Guid id, Action<bool, string> check)
    {
        var order = await worker.GetFromJsonAsync<JsonElement>($"order/{id}");
        using var proposal = await worker.PostAsJsonAsync($"order/{id}/price", new {
            amount = 1500.50m, scope = "Работа и материалы", expectedUpdated = order.GetProperty("updated").GetString() });
        check(proposal.StatusCode == HttpStatusCode.OK, "assigned executor proposes price");
        order = await client.GetFromJsonAsync<JsonElement>($"order/{id}");
        using var acceptance = await client.PostAsJsonAsync($"order/{id}/price/decision", new {
            accepted = true, expectedUpdated = order.GetProperty("updated").GetString() });
        check(acceptance.StatusCode == HttpStatusCode.OK, "owner accepts the exact current proposal");
    }

    public static async Task Run(string connection, Guid sourceId, Guid workerId, HttpClient worker,
        HttpClient client, HttpClient admin, HttpClient dispatcher, HttpClient otherWorker, HttpClient outsider,
        HttpClient anonymous, Action<bool, string> check)
    {
        if (!new NpgsqlConnectionStringBuilder(connection).Database!.StartsWith("rabotyagi_checks_"))
            throw new InvalidOperationException("Price checks require an isolated database.");
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        var source = await db.Orders.AsNoTracking().SingleAsync(x => x.Id == sourceId);
        async Task<Order> Fresh(EOrderStatus status = EOrderStatus.WorkerOnTheWay)
        {
            var order = new Order { InitiatorId = source.InitiatorId, AddressId = source.AddressId,
                ExecutorId = workerId, Status = status, Comment = "Price checks", OrderType = source.OrderType };
            db.Orders.Add(order); await db.SaveChangesAsync();
            return await Read(order.Id);
        }
        Task<Order> Read(Guid id) => db.Orders.AsNoTracking().SingleAsync(x => x.Id == id);
        Task<HttpResponseMessage> Propose(HttpClient actor, Order order, decimal amount = 2500.75m, string scope = "Полная стоимость с материалами") =>
            actor.PostAsJsonAsync($"order/{order.Id}/price", new { amount, scope, expectedUpdated = order.Updated });
        Task<HttpResponseMessage> Decide(HttpClient actor, Order order, bool accepted = true, string? reason = null) =>
            actor.PostAsJsonAsync($"order/{order.Id}/price/decision", new { accepted, reason, expectedUpdated = order.Updated });
        async Task Expect(Task<HttpResponseMessage> request, HttpStatusCode code, string label)
        { using var response = await request; check(response.StatusCode == code, label + $" ({(int)response.StatusCode})"); }
        Task<HttpResponseMessage> Start(Order order) => worker.PatchAsJsonAsync("order/status", new { orderId = order.Id, status = "inProgress" });

        var order = await Fresh();
        await Expect(Start(order), HttpStatusCode.Conflict, "server blocks start without a price");
        await Expect(Propose(anonymous, order), HttpStatusCode.Unauthorized, "anonymous cannot propose");
        foreach (var actor in new[] { client, admin, dispatcher })
            await Expect(Propose(actor, order), HttpStatusCode.Forbidden, "staff/client cannot propose on behalf of worker");
        await Expect(Propose(otherWorker, order), HttpStatusCode.NotFound, "unassigned worker cannot propose");
        foreach (var amount in new[] { 0m, -1m, 1.001m, 10000000.01m })
            await Expect(Propose(worker, order, amount), HttpStatusCode.BadRequest, "invalid money is rejected without rounding");
        await Expect(Propose(worker, order, scope: "  "), HttpStatusCode.BadRequest, "scope is required");
        await Expect(Propose(worker, order), HttpStatusCode.OK, "proposal succeeds");
        await Expect(Propose(worker, order), HttpStatusCode.Conflict, "duplicate proposal is stale");
        order = await Read(order.Id);
        await Expect(Start(order), HttpStatusCode.Conflict, "pending price cannot start work");
        foreach (var actor in new[] { worker, admin, dispatcher })
            await Expect(Decide(actor, order), HttpStatusCode.Forbidden, "only owner can decide");
        await Expect(Decide(outsider, order), HttpStatusCode.NotFound, "outsider cannot decide or discover proposal");
        await Expect(Decide(anonymous, order), HttpStatusCode.Unauthorized, "anonymous cannot decide");
        await Expect(Decide(client, order, false), HttpStatusCode.BadRequest, "rejection requires reason");
        await Expect(Decide(client, order, false, "Дорого"), HttpStatusCode.OK, "owner rejects price");
        await Expect(Start(order), HttpStatusCode.Conflict, "rejected price cannot start work");
        order = await Read(order.Id);
        await Expect(Propose(worker, order, 2000), HttpStatusCode.OK, "worker revises rejected proposal");
        var firstProposal = await Read(order.Id);
        await Expect(Propose(worker, firstProposal, 1800), HttpStatusCode.OK, "pending proposal can be replaced");
        await Expect(Decide(client, firstProposal), HttpStatusCode.Conflict, "old browser cannot accept a superseded amount");
        order = await Read(order.Id);
        var decisions = await Task.WhenAll(Decide(client, order), Decide(client, order));
        check(decisions.Count(x => x.StatusCode == HttpStatusCode.OK) == 1 && decisions.Count(x => x.StatusCode == HttpStatusCode.Conflict) == 1,
            "concurrent acceptance records exactly one decision");
        foreach (var response in decisions) response.Dispose();
        var accepted = await Read(order.Id);
        check(accepted.PriceAmount == 1800 && accepted.PriceStatus == EOrderPriceStatus.Accepted
            && accepted.PriceAcceptedAt?.Offset == TimeSpan.Zero && accepted.PriceExecutorId == workerId, "exact agreed amount and UTC time persist");
        await Expect(Propose(worker, accepted, 3000), HttpStatusCode.Conflict, "accepted price cannot be silently changed");
        await Expect(Decide(client, accepted, false, "Передумал"), HttpStatusCode.Conflict, "completed decision cannot be overwritten");
        await Expect(Start(accepted), HttpStatusCode.OK, "agreed work can start");
        await Expect(Propose(worker, await Read(order.Id)), HttpStatusCode.Conflict, "price is locked after start");
        var events = await db.OrderHistoryEvents.AsNoTracking().Where(x => x.OrderId == order.Id).OrderBy(x => x.OccurredAt).ToListAsync();
        check(events.Count(x => x.EventType == EOrderHistoryEventType.PriceAccepted) == 1
            && events.Single(x => x.EventType == EOrderHistoryEventType.PriceAccepted).PriceAmount == 1800
            && events.Single(x => x.EventType == EOrderHistoryEventType.PriceRejected).Comment == "Дорого"
            && events.Where(x => x.EventType == EOrderHistoryEventType.PriceProposed).All(x => x.ActorUserId == workerId),
            "history retains prices, actors, scopes and rejection reason");

        foreach (var status in Enum.GetValues<EOrderStatus>().Where(x => x != EOrderStatus.WorkerOnTheWay))
            await Expect(Propose(worker, await Fresh(status)), HttpStatusCode.Conflict, "proposal only before work on assigned order");
        var race = await Fresh();
        var proposals = await Task.WhenAll(Propose(worker, race, 1000), Propose(worker, race, 2000));
        check(proposals.Count(x => x.StatusCode == HttpStatusCode.OK) == 1 && proposals.Count(x => x.StatusCode == HttpStatusCode.Conflict) == 1,
            "concurrent proposals have one winner");
        foreach (var response in proposals) response.Dispose();
        race = await Read(race.Id);
        var cancelled = await Task.WhenAll(Decide(client, race), client.PatchAsJsonAsync("order/status", new { orderId = race.Id, status = "cancelled" }));
        foreach (var response in cancelled) response.Dispose();
        var afterRace = await Read(race.Id);
        check(afterRace.Status == EOrderStatus.Cancelled || afterRace.PriceStatus == EOrderPriceStatus.Accepted,
            "accept/cancel race leaves a valid result");
        if (afterRace.Status == EOrderStatus.Cancelled)
            await Expect(Start(afterRace), HttpStatusCode.Conflict, "cancelled order cannot start even if price accepted");

        var failed = await Fresh();
        var decisionFailure = await Fresh();
        await Expect(Propose(worker, decisionFailure), HttpStatusCode.OK, "rollback fixture proposed");
        decisionFailure = await Read(decisionFailure.Id);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_price_history() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'Injected price history failure'; END $$;
            CREATE TRIGGER reject_price_history BEFORE INSERT ON "OrderHistoryEvents"
            FOR EACH ROW EXECUTE FUNCTION reject_price_history();
            """);
        try
        {
            await Expect(Propose(worker, failed), HttpStatusCode.InternalServerError, "proposal history failure reported");
            var unchanged = await Read(failed.Id);
            check(unchanged.PriceAmount == null && unchanged.Updated == failed.Updated, "proposal and history roll back together");
            await Expect(Decide(client, decisionFailure), HttpStatusCode.InternalServerError, "decision history failure reported");
            unchanged = await Read(decisionFailure.Id);
            check(unchanged.PriceStatus == EOrderPriceStatus.Proposed && unchanged.PriceAcceptedAt == null
                && unchanged.Updated == decisionFailure.Updated, "decision and history roll back together");
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_price_history ON \"OrderHistoryEvents\"; DROP FUNCTION reject_price_history();"); }
    }
}
