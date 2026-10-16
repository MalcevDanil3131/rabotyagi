using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "rabotyagi/appsettings.json")));
var connection = new NpgsqlConnectionStringBuilder(config.RootElement.GetProperty("ConnectionStrings").GetProperty("DefaultConnection").GetString()) { Host = "localhost", Database = "postgres", Pooling = false };
var database = "rabotyagi_checks_" + Guid.NewGuid().ToString("N");
var uploads = Path.Combine(Path.GetTempPath(), database);
await using var adminDb = new NpgsqlConnection(connection.ConnectionString);
await adminDb.OpenAsync();
await new NpgsqlCommand($"CREATE DATABASE \"{database}\"", adminDb).ExecuteNonQueryAsync();
Process? server = null;
var output = new List<string>();
var checks = 0;
try
{
    connection.Database = database;
    var start = new ProcessStartInfo("dotnet", Path.Combine(root, "rabotyagi/bin/Debug/net10.0/rabotyagi.dll")) { WorkingDirectory = Path.Combine(root, "rabotyagi"), UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
    start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:5188";
    start.Environment["ConnectionStrings__ProductionConnection"] = connection.ConnectionString;
    start.Environment["Uploads__Path"] = uploads;
    start.Environment["Logging__EventLog__LogLevel__Default"] = "None";
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
        email = "superadmin@mail.ru",
        password = "Wertukan!234",
        isPersistent = false
    });
    var adminInfo = await Json(admin, "account");
    var adminId = adminInfo.GetProperty("id").GetGuid();
    Check(adminInfo.GetProperty("roles").EnumerateArray().Any(x => x.GetString() == "SuperAdmin"), "account contains Identity role");
    async Task<Guid> Register(HttpClient c, string email)
    {
        await Status(c, "account/register", HttpStatusCode.OK, new
        {
            name = "Test",
            surname = email,
            phoneNumber = "+70000000000",
            email = email + "@checks.local",
            password = "Check123!",
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
            password = "Check123!",
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
    await Status(loser, "order/status", HttpStatusCode.Forbidden, new
    {
        orderId = id,
        status = "inProgress"
    }, "PATCH");
    await Status(winner, "order/status", HttpStatusCode.OK, new
    {
        orderId = id,
        status = "inProgress"
    }, "PATCH");
    await Status(admin, "order/status", HttpStatusCode.Conflict, new
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
    order = await Json(client, $"order/{id}");
    var photo = order.GetProperty("photos")[0].GetProperty("id").GetGuid();
    await Status(client, $"order/{id}/photos/{photo}", HttpStatusCode.OK, null, "GET");
    await Status(outsider, $"order/{id}/photos/{photo}", HttpStatusCode.NotFound, null, "GET");
    await Status(client, $"order/{id}/review", HttpStatusCode.Forbidden, new
    {
        approved = true
    });
    await Status(dispatcher, $"order/{id}/review", HttpStatusCode.BadRequest, new
    {
        approved = false
    });
    await Status(dispatcher, $"order/{id}/review", HttpStatusCode.OK, new
    {
        approved = false,
        feedback = "Нужен другой ракурс"
    });
    await Report(true);
    await Status(winner, $"order/{id}/messages", HttpStatusCode.OK, new
    {
        text = "Работу закончил"
    });
    Check((await Json(dispatcher, $"order/{id}/messages")).GetArrayLength() == 1, "order discussion is shared with dispatcher");
    await Status(dispatcher, $"order/{id}/review", HttpStatusCode.OK, new
    {
        approved = true
    });
    await Status(dispatcher, "order/status", HttpStatusCode.Forbidden, new
    {
        orderId = id,
        status = "completed"
    }, "PATCH");
    await Status(admin, "order/status", HttpStatusCode.OK, new
    {
        orderId = id,
        status = "completed"
    }, "PATCH");
    Check((await Json(client, $"order/{id}")).GetProperty("status").GetString() == "completed", "full workflow persists completed status");
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
        password = "Check123!",
        isPersistent = false
    });
    await Status(admin, $"user/{w1}/blocked", HttpStatusCode.OK, new
    {
        blocked = false
    }, "PUT");
    await Status(client, "account/password", HttpStatusCode.BadRequest, new
    {
        oldPassword = "bad",
        newPassword = "Changed123!"
    }, "PATCH");
    await Status(client, "account/logout", HttpStatusCode.OK);
    await Status(client, "account", HttpStatusCode.Unauthorized, null, "GET");
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
