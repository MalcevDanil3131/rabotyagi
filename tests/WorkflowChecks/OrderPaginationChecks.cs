using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using rabotyagi.API.Features.Demo;
using rabotyagi.DataAccess;
using rabotyagi.DataAccess.Entities;
using rabotyagi.DataAccess.Enums;

internal static class OrderPaginationChecks
{
    public static async Task Run(string connection, HttpClient browser, Action<bool, string> check)
    {
        if (!new NpgsqlConnectionStringBuilder(connection).Database!.StartsWith("rabotyagi_demo_checks_"))
            throw new InvalidOperationException("Pagination checks require an isolated demo test database.");
        async Task<JsonElement> Get(string query)
        {
            using var response = await browser.GetAsync("order/page?" + query); response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        }
        var baseline = await Get("pageSize=20");
        var total = baseline.GetProperty("totalCount").GetInt32();
        check(total > 100 && baseline.GetProperty("items").GetArrayLength() == 20, "paged API counts beyond all loaded rows");
        var ids = new List<Guid>();
        for (var page = 1; page <= (total + 19) / 20; page++)
        {
            var data = await Get($"page={page}&pageSize=20");
            ids.AddRange(data.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()));
        }
        check(ids.Count == total && ids.Distinct().Count() == total, "every order appears exactly once across pages with equal creation times");
        var repeated = await Get("page=2&pageSize=20");
        check(repeated.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).SequenceEqual(ids.Skip(20).Take(20)),
            "refresh of second page keeps the same deterministic ordering");
        var created = await Get("page=2&pageSize=100&status=created");
        check(created.GetProperty("totalCount").GetInt32() == 106 && created.GetProperty("items").GetArrayLength() == 6,
            "server status filtering precedes pagination and returns full count");
        var overflow = await Get("page=100000&pageSize=20&status=created");
        check(overflow.GetProperty("page").GetInt32() == 6 && overflow.GetProperty("items").GetArrayLength() == 6,
            "removed or out-of-range last page falls back to current last page");
        var empty = await Get("search=no_such_order_987654&page=25");
        check(empty.GetProperty("page").GetInt32() == 1 && empty.GetProperty("totalCount").GetInt32() == 0,
            "empty filtered list has valid first page and zero total");
        foreach (var invalid in new[] { "page=0", "pageSize=101", "scope=invalid", "status=999", "search=" + new string('x', 201) })
        {
            using var response = await browser.GetAsync("order/page?" + invalid);
            check(response.StatusCode == HttpStatusCode.BadRequest, "invalid page/filter rejected with 400");
        }
        var active = await Get("scope=active");
        check(active.GetProperty("items").EnumerateArray().All(x => x.GetProperty("status").GetString() is "workerOnTheWay" or "inProgress" or "rework"),
            "active scope applied before paging");
        check((await Get("scope=mine")).GetProperty("totalCount").GetInt32() == 0, "staff mine filter does not include others' orders");
        check((await Get("quality=true")).GetProperty("items").EnumerateArray().All(x => x.GetProperty("status").GetString() is "workFinished" or "approved"),
            "quality view filters on server");
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        var source = await db.Orders.AsNoTracking().FirstAsync();
        var hidden = new Order { InitiatorId = DemoAccounts.All[1].Id, AddressId = source.AddressId, Comment = "Private pagination fixture",
            Title = "PRIVATE 100%_token", OrderType = source.OrderType };
        var own = new Order { InitiatorId = DemoAccounts.All[0].Id, AddressId = source.AddressId, Comment = "Search fixture",
            Title = "Search 100%_token", OrderType = source.OrderType };
        db.Orders.AddRange(hidden, own); await db.SaveChangesAsync();
        try
        {
            var literal = await Get("search=" + Uri.EscapeDataString("100%_token"));
            check(literal.GetProperty("totalCount").GetInt32() == 2, "special search characters are literal");
            check((await Get("search=" + Uri.EscapeDataString("100%Xtoken"))).GetProperty("totalCount").GetInt32() == 0,
                "underscore search does not behave as wildcard");
            using (var login = await browser.PostAsync("demo/login/client", null)) login.EnsureSuccessStatusCode();
            literal = await Get("search=" + Uri.EscapeDataString("100%_token"));
            check(literal.GetProperty("totalCount").GetInt32() == 1
                && literal.GetProperty("items")[0].GetProperty("id").GetGuid() == own.Id
                && literal.GetProperty("stats").GetProperty("total").GetInt32() == total + 1,
                "client rows, stats and search never include private order");
            check((await Get("search=" + hidden.Id)).GetProperty("totalCount").GetInt32() == 0, "search by foreign identifier does not disclose order");
            var found = await Get("search=" + own.Id.ToString()[..8]);
            check(found.GetProperty("totalCount").GetInt32() == 1, "partial displayed order number searchable on server");
            var byName = await Get("search=" + Uri.EscapeDataString("клиент"));
            check(byName.GetProperty("totalCount").GetInt32() == total + 1, "case-insensitive author search covers later pages");
            using (var cancel = await browser.PatchAsJsonAsync("order/status", new { orderId = own.Id, status = "cancelled" })) cancel.EnsureSuccessStatusCode();
            check((await Get("status=created&search=" + own.Id)).GetProperty("totalCount").GetInt32() == 0,
                "action immediately removes order from old filtered list");
            var card = await browser.GetFromJsonAsync<JsonElement>($"order/{own.Id}");
            check(card.GetProperty("status").GetString() == "cancelled", "independent detail API remains available outside filtered page");
            using (var login = await browser.PostAsync("demo/login/worker2", null)) login.EnsureSuccessStatusCode();
            var workerPage = await Get("pageSize=100");
            check(workerPage.GetProperty("items").EnumerateArray().All(x => x.GetProperty("status").GetString() == "published"
                || x.GetProperty("executorId").ValueKind == JsonValueKind.String && x.GetProperty("executorId").GetGuid() == DemoAccounts.All[2].Id
                || x.GetProperty("initiatorId").GetGuid() == DemoAccounts.All[2].Id), "worker list obeys existing visibility");
        }
        finally
        {
            await db.OrderHistoryEvents.Where(x => x.OrderId == own.Id || x.OrderId == hidden.Id).ExecuteDeleteAsync();
            await db.Orders.Where(x => x.Id == own.Id || x.Id == hidden.Id).ExecuteDeleteAsync();
            using var login = await browser.PostAsync("demo/login/superadmin", null); login.EnsureSuccessStatusCode();
        }
        using var anonymous = new HttpClient { BaseAddress = browser.BaseAddress };
        using var denied = await anonymous.GetAsync("order/page");
        check(denied.StatusCode == HttpStatusCode.Unauthorized, "paged endpoint requires authenticated identity");
    }
}
