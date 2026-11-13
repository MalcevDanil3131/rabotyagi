using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

internal static class DemoIntegrationChecks
{
    public static async Task Run(string testConnection, string root, Action<bool, string> check)
    {
        var builder = new NpgsqlConnectionStringBuilder(testConnection) { Database = "postgres", Pooling = false };
        var name = "rabotyagi_demo_checks_" + Guid.NewGuid().ToString("N");
        var temp = Path.GetFullPath(Path.Combine(Path.GetTempPath(), name));
        Directory.CreateDirectory(temp);
        await using var maintenance = new NpgsqlConnection(builder.ConnectionString);
        await maintenance.OpenAsync();
        await new NpgsqlCommand($"CREATE DATABASE \"{name}\"", maintenance).ExecuteNonQueryAsync();
        Process? server = null;
        builder.Database = name;
        try
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = temp };
            start.ArgumentList.Add(Path.Combine(root, "rabotyagi/bin/Debug/net10.0/rabotyagi.dll"));
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Demo";
            start.Environment["DOTNET_ENVIRONMENT"] = "Demo";
            start.Environment["Demo__Enabled"] = "true";
            start.Environment["Demo__Port"] = "5190";
            start.Environment["BootstrapAdmin__Enabled"] = "false";
            start.Environment["ConnectionStrings__DemoConnection"] = builder.ConnectionString;
            start.Environment["ConnectionStrings__DefaultConnection"] = "";
            start.Environment["ConnectionStrings__ProductionConnection"] = "";
            start.Environment["Logging__EventLog__LogLevel__Default"] = "None";
            async Task Start()
            {
                server = Process.Start(start)!;
                server.OutputDataReceived += (_, _) => { };
                server.ErrorDataReceived += (_, _) => { };
                server.BeginOutputReadLine(); server.BeginErrorReadLine();
                using var probe = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5190/api/"), Timeout = TimeSpan.FromSeconds(1) };
                for (var i = 0; i < 100; i++)
                {
                    try { using var response = await probe.GetAsync("account"); if (response.StatusCode == HttpStatusCode.Unauthorized) return; }
                    catch (HttpRequestException) { }
                    catch (TaskCanceledException) { }
                    if (server.HasExited) throw new InvalidOperationException("Isolated demo API exited before becoming ready.");
                    await Task.Delay(200);
                }
                throw new InvalidOperationException("Isolated demo API did not become ready.");
            }
            await Start();
            using var browser = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), UseProxy = false })
                { BaseAddress = new Uri("http://127.0.0.1:5190/api/") };
            using (var response = await browser.GetAsync("demo/accounts"))
                check(response.StatusCode == HttpStatusCode.NotFound, "demo HTTP endpoint requires explicit header");
            browser.DefaultRequestHeaders.Add("X-Rabotyagi-Demo", "1");
            async Task<JsonElement> Get(string path)
            {
                using var response = await browser.GetAsync(path); response.EnsureSuccessStatusCode();
                return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
            }
            var accounts = await Get("demo/accounts");
            check(accounts.GetArrayLength() == 6, "demo exposes six predefined accounts");
            foreach (var account in accounts.EnumerateArray())
            {
                using var login = await browser.PostAsync("demo/login/" + account.GetProperty("key").GetString(), null);
                check(login.IsSuccessStatusCode, "demo account switch succeeds");
                var current = await Get("account");
                check(current.GetProperty("id").GetGuid() == account.GetProperty("id").GetGuid()
                    && current.GetProperty("roles").EnumerateArray().Single().GetString() == account.GetProperty("role").GetString(),
                    "switch changes real authenticated identity and role");
            }
            using (var invalid = await browser.PostAsync("demo/login/arbitrary-user", null))
                check(invalid.StatusCode == HttpStatusCode.NotFound, "arbitrary account cannot be impersonated");
            browser.DefaultRequestHeaders.Add("Origin", "http://localhost.evil.example");
            using (var foreign = await browser.PostAsync("demo/login/client", null))
                check(foreign.StatusCode == HttpStatusCode.NotFound, "foreign origin cannot replace the session");
            browser.DefaultRequestHeaders.Remove("Origin");
            var queues = await Get("order/queues");
            check(queues.GetArrayLength() == 8, "new demo lifecycle has eight stages without mandatory staff approval");
            var published = await Get("order?status=published");
            check(published.GetArrayLength() == 1 && published[0].GetProperty("status").GetString() == "published", "queue filter is applied on server");
            await DemoFixtureChecks.CheckScenarios(browser, check);

            // Make the unfiltered result exceed one page, without touching any application DB.
            await using (var db = new NpgsqlConnection(builder.ConnectionString))
            {
                await db.OpenAsync();
                await new NpgsqlCommand("""
                    INSERT INTO "Orders" ("Id", "InitiatorId", "OrderType", "Comment", "Status", "AddressId", "Created", "Updated",
                        "AddressText", "Title", "WorkDone", "WorkplaceClean", "ClientInformed")
                    SELECT gen_random_uuid(), "InitiatorId", "OrderType", "Comment", 'Created', "AddressId", now(), now(),
                        "AddressText", 'Pagination fixture', false, false, false FROM "Orders", generate_series(1, 105)
                    WHERE "Status" = 'Created';
                    """, db).ExecuteNonQueryAsync();
            }
            queues = await Get("order/queues");
            check(queues.EnumerateArray().Single(x => x.GetProperty("status").GetString() == "created").GetProperty("count").GetInt32() == 106,
                "queue counters include rows beyond page one");
            check((await Get("order?status=created&page=2&pageSize=100")).GetArrayLength() == 6, "filtered queue supports next page");
            await OrderPaginationChecks.Run(builder.ConnectionString, browser, check);
            var worker2 = accounts.EnumerateArray().Single(x => x.GetProperty("key").GetString() == "worker2").GetProperty("id").GetGuid();
            using (var change = await browser.PutAsJsonAsync($"user/{worker2}/role", new { role = "User" })) change.EnsureSuccessStatusCode();
            using (var login = await browser.PostAsync("demo/login/worker2", null))
                check(login.StatusCode == HttpStatusCode.Conflict, "quick login does not restore changed roles");
            server!.Kill(true); await server.WaitForExitAsync();
            await Start();
            static string Counts(JsonElement rows) => string.Join(";", rows.EnumerateArray()
                .OrderBy(x => x.GetProperty("status").GetString())
                .Select(x => $"{x.GetProperty("status").GetString()}:{x.GetProperty("count").GetInt32()}"));
            check(Counts(await Get("order/queues")) == Counts(queues), "demo restart preserves orders without reseeding");
            using (var login = await browser.PostAsync("demo/login/worker2", null))
                check(login.StatusCode == HttpStatusCode.Conflict, "demo restart preserves changed account roles");
            await DemoFixtureChecks.CheckUpgrade(builder.ConnectionString, async () =>
            {
                server!.Kill(true); await server.WaitForExitAsync(); await Start();
            }, check);
            using (var login = await browser.PostAsync("demo/login/client", null)) login.EnsureSuccessStatusCode();
            using (var denied = await browser.GetAsync("order/queues"))
                check(denied.StatusCode == HttpStatusCode.Forbidden, "client cannot read staff queue counters");
            var limitedDemo = false;
            for (var i = 0; i < 31; i++)
            {
                using var probe = await browser.PostAsync("demo/login/not-a-demo-account", null);
                if (probe.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    limitedDemo = probe.Headers.RetryAfter?.Delta?.TotalSeconds > 0;
                    break;
                }
                check(probe.StatusCode == HttpStatusCode.NotFound, "invalid demo key remains undiscoverable before rate limit");
            }
            check(limitedDemo, "local demo switching also has a rate limit with Retry-After");
            check((await Get("account")).GetProperty("id").GetGuid() == accounts[0].GetProperty("id").GetGuid(),
                "rejected demo switches preserve existing authenticated identity");
        }
        finally
        {
            if (server is { HasExited: false }) { server.Kill(true); await server.WaitForExitAsync(); }
            NpgsqlConnection.ClearAllPools();
            await new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", maintenance).ExecuteNonQueryAsync();
            if (temp.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(temp) == name) Directory.Delete(temp, true);
        }
    }
}
