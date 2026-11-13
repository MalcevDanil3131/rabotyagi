using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using rabotyagi.API.Features.Demo;

internal static class DemoFixtureChecks
{
    public static async Task CheckScenarios(HttpClient browser, Action<bool, string> check)
    {
        var orders = (await browser.GetFromJsonAsync<JsonElement>("order?pageSize=100")).EnumerateArray().ToArray();
        check(orders.Length == 12, "fresh demo contains twelve current scenarios");
        check(orders.Where(x => x.GetProperty("executorId").ValueKind != JsonValueKind.Null)
            .Select(x => x.GetProperty("executorId").GetGuid()).Distinct().Count() == 2, "both demo workers have assigned scenarios");
        foreach (var order in orders)
        {
            var priceStatus = order.GetProperty("priceStatus").GetString();
            var status = order.GetProperty("status").GetString();
            var history = (await browser.GetFromJsonAsync<JsonElement>($"order/{order.GetProperty("id").GetGuid()}/history"))
                .EnumerateArray().ToArray();
            check(history.Any(x => x.GetProperty("eventType").GetString() == "created"), "demo scenario has creation history");
            if (status is "inProgress" or "workFinished" or "rework" or "approved" or "completed")
            {
                check(priceStatus == "accepted" && order.GetProperty("priceAmount").GetDecimal() > 0
                    && order.GetProperty("executorId").GetGuid() == order.GetProperty("priceExecutorId").GetGuid(),
                    "started demo work has an agreed price for its current executor");
                var accepted = history.Single(x => x.GetProperty("eventType").GetString() == "priceAccepted");
                var started = history.Single(x => x.GetProperty("eventType").GetString() == "statusChanged"
                    && x.GetProperty("newStatus").GetString() == "inProgress");
                check(accepted.GetProperty("occurredAt").GetDateTimeOffset() < started.GetProperty("occurredAt").GetDateTimeOffset()
                    && accepted.GetProperty("priceAmount").GetDecimal() == order.GetProperty("priceAmount").GetDecimal(),
                    "demo history agrees with the amount and records acceptance before work starts");
            }
            if (status is "created" or "published" or "cancelled")
                check(priceStatus == "none" && order.GetProperty("priceAmount").ValueKind == JsonValueKind.Null,
                    "unassigned demo orders do not invent prices");
        }
        var pending = orders.Single(x => x.GetProperty("priceStatus").GetString() == "proposed");
        var rejected = orders.Single(x => x.GetProperty("priceStatus").GetString() == "rejected");
        check(rejected.GetProperty("status").GetString() == "workerOnTheWay", "rejected demo proposal is available for revision");
        var worker = DemoAccounts.All.Single(x => x.Id == pending.GetProperty("executorId").GetGuid());
        using (var login = await browser.PostAsync("demo/login/" + worker.Key, null)) login.EnsureSuccessStatusCode();
        var id = pending.GetProperty("id").GetGuid();
        using (var response = await browser.PatchAsJsonAsync("order/status", new { orderId = id, status = "inProgress" }))
            check(response.StatusCode == HttpStatusCode.Conflict, "demo sample cannot start before client accepts");
        using (var login = await browser.PostAsync("demo/login/client", null)) login.EnsureSuccessStatusCode();
        using (var response = await browser.PostAsJsonAsync($"order/{id}/price/decision", new {
            accepted = true, expectedUpdated = pending.GetProperty("updated").GetString() }))
            check(response.IsSuccessStatusCode, "client can accept a seeded demo proposal through the real API");
        var report = orders.First(x => x.GetProperty("status").GetString() == "workFinished");
        var reportId = report.GetProperty("id").GetGuid();
        using (var response = await browser.PostAsJsonAsync($"order/{reportId}/review", new {
            approved = true, expectedUpdated = report.GetProperty("updated").GetString() }))
            check(response.IsSuccessStatusCode, "client directly accepts a current seeded demo report");
        var finished = await browser.GetFromJsonAsync<JsonElement>($"order/{reportId}");
        var acceptedHistory = await browser.GetFromJsonAsync<JsonElement>($"order/{reportId}/history");
        check(finished.GetProperty("status").GetString() == "completed" && acceptedHistory.EnumerateArray().Any(x =>
            x.GetProperty("eventType").GetString() == "workAcceptedByClient"), "demo acceptance completes order and records client decision");
        using (var login = await browser.PostAsync("demo/login/superadmin", null)) login.EnsureSuccessStatusCode();
    }

    public static async Task CheckUpgrade(string connection, Func<Task> restart, Action<bool, string> check)
    {
        if (!new NpgsqlConnectionStringBuilder(connection).Database!.StartsWith("rabotyagi_demo_checks_"))
            throw new InvalidOperationException("Fixture upgrade checks require an isolated test database.");
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        async Task<HashSet<string>> Snapshot(string table)
        {
            var rows = new HashSet<string>();
            await using var command = new NpgsqlCommand($"SELECT row_to_json(t)::text FROM \"{table}\" t", db);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
            return rows;
        }
        var beforeOrders = await Snapshot("Orders");
        var beforeHistory = await Snapshot("OrderHistoryEvents");
        var beforeUsers = await Snapshot("AspNetUsers");
        var orderIds = beforeOrders.Select(x => JsonDocument.Parse(x).RootElement.GetProperty("Id").GetGuid()).ToHashSet();
        // Simulate an existing demo created before versioned fixtures were introduced.
        await using (var removeMarker = new NpgsqlCommand("DELETE FROM \"AspNetUserClaims\" WHERE \"ClaimType\" = @type AND \"ClaimValue\" = @version", db))
        {
            removeMarker.Parameters.AddWithValue("type", DemoScenarios.MarkerType);
            removeMarker.Parameters.AddWithValue("version", DemoScenarios.Version);
            await removeMarker.ExecuteNonQueryAsync();
        }
        await restart();
        var afterOrders = await Snapshot("Orders");
        var afterHistory = await Snapshot("OrderHistoryEvents");
        check(afterOrders.Count == beforeOrders.Count + 12, "unversioned demo receives the new scenario set");
        check(beforeOrders.IsSubsetOf(afterOrders), "upgrade preserves every field of existing demo orders including user decisions");
        check(beforeHistory.IsSubsetOf(afterHistory) && afterHistory.Count(x => orderIds.Contains(
            JsonDocument.Parse(x).RootElement.GetProperty("OrderId").GetGuid())) == beforeHistory.Count,
            "upgrade neither changes nor invents history for existing orders");
        check(beforeUsers.SetEquals(await Snapshot("AspNetUsers")), "upgrade preserves existing accounts including roles and availability");
        await restart();
        check(afterOrders.SetEquals(await Snapshot("Orders")) && afterHistory.SetEquals(await Snapshot("OrderHistoryEvents")),
            "version marker prevents duplicate scenarios and history on another restart");
    }
}
