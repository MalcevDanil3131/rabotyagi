using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var checks = 0;
DemoChecks.RunGuards(Check);
if (args.Contains("--demo-guards-only"))
{
    Console.WriteLine($"PASS: {checks} demo isolation checks without database access.");
    return;
}
var testConnection = Environment.GetEnvironmentVariable("WORKFLOW_CHECKS_CONNECTION")
    ?? throw new InvalidOperationException("Set WORKFLOW_CHECKS_CONNECTION to a dedicated test PostgreSQL server; application configuration is never used.");
if (args.Contains("--backup-only"))
{
    await BackupChecks.Run(testConnection, root, Check);
    Console.WriteLine($"PASS: {checks} database backup checks against isolated PostgreSQL databases.");
    return;
}
if (args.Contains("--security-only"))
{
    await SecurityChecks.Run(testConnection, root, Check);
    Console.WriteLine($"PASS: {checks} security checks against isolated PostgreSQL database.");
    return;
}
var connection = new NpgsqlConnectionStringBuilder(testConnection) { Database = "postgres", Pooling = false };
var testPassword = Environment.GetEnvironmentVariable("WORKFLOW_CHECKS_PASSWORD") ?? "Aa1!" + Guid.NewGuid().ToString("N");
var database = "rabotyagi_checks_" + Guid.NewGuid().ToString("N");
var uploads = Path.Combine(Path.GetTempPath(), database);
await using var adminDb = new NpgsqlConnection(connection.ConnectionString);
await adminDb.OpenAsync();
await new NpgsqlCommand($"CREATE DATABASE \"{database}\"", adminDb).ExecuteNonQueryAsync();
Process? server = null;
var output = new List<string>();
try
{
    connection.Database = database;
    await BootstrapChecks.Run(connection.ConnectionString, testPassword, Check);
    var start = new ProcessStartInfo("dotnet", Path.Combine(root, "rabotyagi/bin/Debug/net10.0/rabotyagi.dll")) { WorkingDirectory = Path.Combine(root, "rabotyagi"), UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
    start.Environment["DOTNET_ENVIRONMENT"] = "Production";
    start.Environment["Demo__Enabled"] = "false";
    start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:5188";
    start.Environment["ConnectionStrings__DefaultConnection"] = "";
    start.Environment["ConnectionStrings__ProductionConnection"] = connection.ConnectionString;
    start.Environment["Uploads__Path"] = uploads;
    var testKeys = Path.Combine(uploads, "keys");
    Directory.CreateDirectory(testKeys);
    start.Environment["DataProtection__Path"] = testKeys;
    start.Environment["Logging__EventLog__LogLevel__Default"] = "None";
    start.Environment["BootstrapAdmin__Enabled"] = "false";
    start.Environment["BootstrapAdmin__Email"] = "";
    start.Environment["BootstrapAdmin__Password"] = "";
    // Workflow traffic exceeds interactive limits; real limits are checked by SecurityChecks.
    start.Environment["RequestSecurity__RequireHttpsCookies"] = "false";
    start.Environment["RequestSecurity__ReadPermitLimit"] = "10000";
    start.Environment["RequestSecurity__WritePermitLimit"] = "10000";
    server = Process.Start(start)!;
    server.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.Add(e.Data); };
    server.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.Add(e.Data); };
    server.BeginOutputReadLine();
    server.BeginErrorReadLine();
    using var anonymous = Client();
    var ready = false;
    for (var i = 0; i < 100; i++)
    {
        try
        {
            var r = await anonymous.GetAsync("account");
            if (r.StatusCode == HttpStatusCode.Unauthorized)
            {
                ready = true;
                break;
            }
        }
        catch (HttpRequestException) { }
        if (server.HasExited)
            break;
        await Task.Delay(200);
    }
    Check(ready, "API starts, migrates and returns 401 for anonymous account");
    using var admin = Client();
    using var client = Client();
    using var worker1 = Client();
    using var worker2 = Client();
    using var dispatcher = Client();
    using var outsider = Client();
    await Status(admin, "account/login", HttpStatusCode.OK, new
    {
        email = "bootstrap@checks.local",
        password = testPassword,
        isPersistent = false
    });
    var adminInfo = await Json(admin, "account");
    var adminId = adminInfo.GetProperty("id").GetGuid();
    Check((await Json(admin, "user")).GetArrayLength() == 2, "Production startup creates no demo accounts");
    Check(adminInfo.GetProperty("roles").EnumerateArray().Any(x => x.GetString() == "SuperAdmin"), "account contains Identity role");
    async Task<Guid> Register(HttpClient c, string email)
    {
        await Status(c, "account/register", HttpStatusCode.OK, new
        {
            name = "Test",
            surname = email,
            phoneNumber = "+70000000000",
            email = email + "@checks.local",
            password = testPassword,
            isPersistent = false
        });
        return (await Json(c, "account")).GetProperty("id").GetGuid();
    }
    var clientId = await Register(client, "client");
    var w1 = await Register(worker1, "worker1");
    var w2 = await Register(worker2, "worker2");
    var d = await Register(dispatcher, "dispatcher");
    await Register(outsider, "outsider");
    await Status(admin, $"user/{w1}/role", HttpStatusCode.OK, new
    {
        role = "Worker"
    }, "PUT");
    await Status(admin, $"user/{w2}/role", HttpStatusCode.OK, new
    {
        role = "Worker"
    }, "PUT");
    await Status(admin, $"user/{d}/role", HttpStatusCode.OK, new
    {
        role = "Dispatcher"
    }, "PUT");
    await Status(worker1, "account", HttpStatusCode.Unauthorized, null, "GET");
    foreach (var (c, email) in new[] { (worker1, "worker1"), (worker2, "worker2"), (dispatcher, "dispatcher") })
        await Status(c, "account/login", HttpStatusCode.OK, new
        {
            email = email + "@checks.local",
            password = testPassword,
            isPersistent = false
        });
    await Status(client, "address", HttpStatusCode.OK, new
    {
        country = "Россия",
        city = "Екатеринбург",
        street = "Тестовая",
        houseNumber = "11",
        apartmentNumber = "123",
        floorNumber = 1,
        intercomNumber = "123"
    });
    Check((await Json(client, "account")).GetProperty("address").GetProperty("country").GetString() == "Россия", "address JSON persists");
    var created = await client.PostAsJsonAsync("order", new
    {
        title = "Проверка заявки",
        orderType = "sanitary",
        comment = "Заменить смеситель и проверить работу",
        addressText = "Тестовая, 11, кв. 123",
        scheduledAt = DateTimeOffset.UtcNow.AddHours(1)
    });
    Check(created.StatusCode == HttpStatusCode.Created, "create returns 201 and order DTO");
    var order = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
    var id = order.GetProperty("id").GetGuid();
    await Status(outsider, $"order/{id}", HttpStatusCode.NotFound, null, "GET");
    await Status(worker1, $"order/{id}", HttpStatusCode.NotFound, null, "GET");
    await Status(client, "order/status", HttpStatusCode.Forbidden, new
    {
        orderId = id,
        status = "published"
    }, "PATCH");
    await Status(admin, "order/status", HttpStatusCode.OK, new
    {
        orderId = id,
        status = "published"
    }, "PATCH");
    await Status(worker1, "order/executor", HttpStatusCode.Conflict, new
    {
        orderId = id,
        executorId = w1
    }, "PATCH");
    await Status(worker1, "worker/availability", HttpStatusCode.OK, new
    {
        hours = 2
    }, "PUT");
    await Status(worker2, "worker/availability", HttpStatusCode.OK, new
    {
        hours = 2
    }, "PUT");
    var race = await Task.WhenAll(worker1.PatchAsJsonAsync("order/executor", new
    {
        orderId = id,
        executorId = w1
    }), worker2.PatchAsJsonAsync("order/executor", new
    {
        orderId = id,
        executorId = w2
    }));
    Check(race.Count(x => x.StatusCode == HttpStatusCode.OK) == 1 && race.Count(x => x.StatusCode == HttpStatusCode.Conflict) == 1, "concurrent take has exactly one winner");
    var winner = race[0].StatusCode == HttpStatusCode.OK ? worker1 : worker2;
    var loser = winner == worker1 ? worker2 : worker1;
    // Exercise cancellation via the real API and verify persistence with a new GET.
    async Task Cancel(HttpClient actor, Guid orderId, HttpStatusCode expected) =>
        await Status(actor, "order/status", expected, new { orderId, status = "cancelled" }, "PATCH");
    foreach (var initialStatus in new[] { "created", "published", "workerOnTheWay" })
    {
        using var result = await client.PostAsJsonAsync("order", new
        {
            title = "Проверка отмены",
            orderType = "sanitary",
            comment = "Заявка для проверки отмены",
            addressText = "Тестовая, 11"
        });
        Check(result.StatusCode == HttpStatusCode.Created, "cancellation order created");
        var cancelId = JsonDocument.Parse(await result.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        if (initialStatus != "created")
            await Status(admin, "order/status", HttpStatusCode.OK, new { orderId = cancelId, status = "published" }, "PATCH");
        if (initialStatus == "workerOnTheWay")
            await Status(worker1, "order/executor", HttpStatusCode.OK, new { orderId = cancelId, executorId = w1 }, "PATCH");
        await Cancel(anonymous, cancelId, HttpStatusCode.Unauthorized);
        await Cancel(outsider, cancelId, HttpStatusCode.Forbidden);
        await Cancel(dispatcher, cancelId, HttpStatusCode.Forbidden);
        await Cancel(worker1, cancelId, HttpStatusCode.Forbidden);
        Check((await Json(client, $"order/{cancelId}")).GetProperty("status").GetString() == initialStatus,
            "unauthorized cancellation does not change status");
        await Cancel(client, cancelId, HttpStatusCode.OK);
        Check((await Json(client, $"order/{cancelId}")).GetProperty("status").GetString() == "cancelled",
            $"owner cancellation from {initialStatus} persists");
        await Cancel(client, cancelId, HttpStatusCode.Conflict);
        await Status(worker1, "order/executor", HttpStatusCode.Conflict, new { orderId = cancelId, executorId = w1 }, "PATCH");
    }
    await Cancel(client, Guid.NewGuid(), HttpStatusCode.NotFound);
    using var adminCancellation = await client.PostAsJsonAsync("order", new
    {
        title = "Проверка прав администратора",
        orderType = "sanitary",
        comment = "Заявка для проверки отклонения администратором",
        addressText = "Тестовая, 11"
    });
    Check(adminCancellation.StatusCode == HttpStatusCode.Created, "admin cancellation order created");
    var adminCancelId = JsonDocument.Parse(await adminCancellation.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    await Cancel(admin, adminCancelId, HttpStatusCode.OK);
    Check((await Json(client, $"order/{adminCancelId}")).GetProperty("status").GetString() == "cancelled",
        "administrator can still reject another user's created order");
    async Task RejectLateCancellation(string expectedStatus)
    {
        await Cancel(client, id, HttpStatusCode.Conflict);
        Check((await Json(client, $"order/{id}")).GetProperty("status").GetString() == expectedStatus,
            $"cancellation cannot change {expectedStatus}");
    }
    await Status(loser, "order/status", HttpStatusCode.Forbidden, new
    {
        orderId = id,
        status = "inProgress"
    }, "PATCH");
    await Status(winner, "order/status", HttpStatusCode.Conflict, new { orderId = id, status = "inProgress" }, "PATCH");
    await PriceChecks.Agree(winner, client, id, Check);
    await Status(winner, "order/status", HttpStatusCode.OK, new
    {
        orderId = id,
        status = "inProgress"
    }, "PATCH");
    await RejectLateCancellation("inProgress");
    await Status(admin, "order/status", HttpStatusCode.Forbidden, new
    {
        orderId = id,
        status = "completed"
    }, "PATCH");
    async Task Report(bool valid)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Работа сделана, всё проверено"), "Summary");
        form.Add(new StringContent("true"), "WorkDone");
        form.Add(new StringContent("true"), "WorkplaceClean");
        form.Add(new StringContent("true"), "ClientInformed");
        var bytes = valid ? Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jX1sAAAAASUVORK5CYII=") : "bad file"u8.ToArray();
        form.Add(new ByteArrayContent(bytes), "Photos", "report.png");
        var r = await winner.PostAsync($"order/{id}/report", form);
        Check(r.StatusCode == (valid ? HttpStatusCode.OK : HttpStatusCode.BadRequest), valid ? "multipart report and photo persist" : "invalid photo signature rejected");
    }
    await Report(false);
    await Report(true);
    await RejectLateCancellation("workFinished");
    order = await Json(client, $"order/{id}");
    var photo = order.GetProperty("photos")[0].GetProperty("id").GetGuid();
    await Status(client, $"order/{id}/photos/{photo}", HttpStatusCode.OK, null, "GET");
    await Status(outsider, $"order/{id}/photos/{photo}", HttpStatusCode.NotFound, null, "GET");
    await Status(dispatcher, $"order/{id}/review", HttpStatusCode.Forbidden, new
    {
        approved = true, expectedUpdated = order.GetProperty("updated").GetString()
    });
    await Status(client, $"order/{id}/review", HttpStatusCode.BadRequest, new
    {
        approved = false, expectedUpdated = order.GetProperty("updated").GetString()
    });
    await Status(client, $"order/{id}/review", HttpStatusCode.OK, new
    {
        approved = false, feedback = "Нужен другой ракурс", expectedUpdated = order.GetProperty("updated").GetString()
    });
    await RejectLateCancellation("rework");
    await Report(true);
    await Status(winner, $"order/{id}/messages", HttpStatusCode.OK, new
    {
        text = "Работу закончил"
    });
    Check((await Json(dispatcher, $"order/{id}/messages")).GetArrayLength() == 1, "order discussion is shared with dispatcher");
    order = await Json(client, $"order/{id}");
    await Status(client, $"order/{id}/review", HttpStatusCode.OK, new
    {
        approved = true, expectedUpdated = order.GetProperty("updated").GetString()
    });
    await Status(admin, "order/status", HttpStatusCode.Forbidden, new
    {
        orderId = id, status = "completed"
    }, "PATCH");
    Check((await Json(client, $"order/{id}")).GetProperty("status").GetString() == "completed", "full workflow persists completed status");
    await RejectLateCancellation("completed");
    await OrderRepairChecks.Run(connection.ConnectionString, id, winner == worker1 ? w1 : w2, winner, uploads, Check);
    await HistoryChecks.Run(connection.ConnectionString, id, winner == worker1 ? w1 : w2,
        client, admin, dispatcher, winner, loser, outsider, anonymous, Check);
    await ReassignmentChecks.Run(connection.ConnectionString, id, w1, w2,
        client, admin, dispatcher, worker1, worker2, anonymous, Check);
    await PriceChecks.Run(connection.ConnectionString, id, w1, worker1, client, admin, dispatcher, worker2, outsider, anonymous, Check);
    await ClientAcceptanceChecks.Run(connection.ConnectionString, id, client, admin, dispatcher, winner, outsider, anonymous, Check);
    var historyBeforeRestart = (await Json(client, $"order/{id}/history")).GetRawText();
    server!.Kill(true);
    await server.WaitForExitAsync();
    server = Process.Start(start)!;
    server.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.Add(e.Data); };
    server.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.Add(e.Data); };
    server.BeginOutputReadLine();
    server.BeginErrorReadLine();
    ready = false;
    for (var i = 0; i < 100; i++)
    {
        try { using var response = await anonymous.GetAsync("account"); ready = response.StatusCode == HttpStatusCode.Unauthorized; }
        catch (HttpRequestException) { }
        if (ready || server.HasExited) break;
        await Task.Delay(200);
    }
    Check(ready, "isolated API restarts");
    Check((await Json(client, $"order/{id}/history")).GetRawText() == historyBeforeRestart, "history survives server restart unchanged");
    await Status(admin, $"user/{adminId}/blocked", HttpStatusCode.BadRequest, new
    {
        blocked = true
    }, "PUT");
    await Status(admin, $"user/{w1}/blocked", HttpStatusCode.OK, new
    {
        blocked = true
    }, "PUT");
    await Status(worker1, "account", HttpStatusCode.Unauthorized, null, "GET");
    await Status(worker1, "account/login", HttpStatusCode.BadRequest, new
    {
        email = "worker1@checks.local",
        password = testPassword,
        isPersistent = false
    });
    await Status(admin, $"user/{w1}/blocked", HttpStatusCode.OK, new
    {
        blocked = false
    }, "PUT");
    await Status(client, "account/password", HttpStatusCode.BadRequest, new
    {
        oldPassword = "bad",
        newPassword = "Aa1!" + Guid.NewGuid().ToString("N")
    }, "PATCH");
    await Status(client, "account/logout", HttpStatusCode.OK);
    await Status(client, "account", HttpStatusCode.Unauthorized, null, "GET");
    using (var demoRequest = new HttpRequestMessage(HttpMethod.Get, "demo/accounts"))
    {
        demoRequest.Headers.Add("X-Rabotyagi-Demo", "1");
        using var demoResponse = await anonymous.SendAsync(demoRequest);
        Check(demoResponse.StatusCode == HttpStatusCode.NotFound, "Production does not expose demo accounts");
    }
    await DemoIntegrationChecks.Run(testConnection, root, Check);
    await SecurityChecks.Run(testConnection, root, Check);
    Console.WriteLine($"PASS: {checks} checks against isolated PostgreSQL database.");
    if (args.Contains("--serve"))
    {
        Console.WriteLine("Temporary API is available at http://127.0.0.1:5188 for browser checks. Press Enter to stop and remove the test database.");
        Console.ReadLine();
    }
}
catch
{
    lock (output)
        foreach (var line in output.TakeLast(35))
            Console.Error.WriteLine(line);
    throw;
}
finally
{
    if (server is { HasExited: false })
    {
        server.Kill(true);
        await server.WaitForExitAsync();
    }
    NpgsqlConnection.ClearAllPools();
    await new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", adminDb).ExecuteNonQueryAsync();
    if (Directory.Exists(uploads))
        Directory.Delete(uploads, true);
}
HttpClient Client() => new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = new Uri("http://127.0.0.1:5188/api/"), Timeout = TimeSpan.FromSeconds(15) };
void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); checks++; Console.WriteLine("OK: " + label); }
async Task Status(HttpClient c, string path, HttpStatusCode expected, object? body = null, string method = "POST")
{
    using var request = new HttpRequestMessage(new HttpMethod(method), path);
    if (body != null)
        request.Content = JsonContent.Create(body);
    using var response = await c.SendAsync(request);
    Check(response.StatusCode == expected, $"{method} {path}: {(int)response.StatusCode} expected {(int)expected}" + (response.StatusCode == expected ? "" : " " + await response.Content.ReadAsStringAsync()));
}
async Task<JsonElement> Json(HttpClient c, string path) { using var r = await c.GetAsync(path); r.EnsureSuccessStatusCode(); return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone(); }
