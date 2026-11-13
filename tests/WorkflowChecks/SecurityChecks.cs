using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using rabotyagi.DataAccess;

internal static class SecurityChecks
{
    public static async Task Run(string testConnection, string root, Action<bool, string> check)
    {
        var builder = new NpgsqlConnectionStringBuilder(testConnection) { Database = "postgres", Pooling = false };
        var name = "rabotyagi_security_checks_" + Guid.NewGuid().ToString("N");
        var temp = Path.GetFullPath(Path.Combine(Path.GetTempPath(), name));
        Directory.CreateDirectory(temp);
        await using var maintenance = new NpgsqlConnection(builder.ConnectionString);
        await maintenance.OpenAsync();
        await new NpgsqlCommand($"CREATE DATABASE \"{name}\"", maintenance).ExecuteNonQueryAsync();
        builder.Database = name;
        Process? server = null;
        var errors = new List<string>();
        try
        {
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = temp, UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(root, "rabotyagi/bin/Debug/net10.0/rabotyagi.dll"));
            start.Environment["ASPNETCORE_ENVIRONMENT"] = start.Environment["DOTNET_ENVIRONMENT"] = "Production";
            start.Environment["Demo__Enabled"] = "false";
            start.Environment["BootstrapAdmin__Enabled"] = "false";
            start.Environment["ConnectionStrings__ProductionConnection"] = builder.ConnectionString;
            start.Environment["ConnectionStrings__DefaultConnection"] = "";
            start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:5191";
            start.Environment["DataProtection__Path"] = Path.Combine(temp, "keys");
            Directory.CreateDirectory(start.Environment["DataProtection__Path"]!);
            start.Environment["Logging__EventLog__LogLevel__Default"] = "None";
            start.Environment["RequestSecurity__RequireHttpsCookies"] = "false";
            start.Environment["RequestSecurity__AllowedOrigins__0"] = "http://127.0.0.1:5173";
            start.Environment["RequestSecurity__FailedLoginLimit"] = "3";
            start.Environment["RequestSecurity__LockoutSeconds"] = "3";
            start.Environment["RequestSecurity__LoginPermitLimit"] = "5";
            start.Environment["RequestSecurity__LoginWindowSeconds"] = "5";
            start.Environment["RequestSecurity__RegisterPermitLimit"] = "20";
            start.Environment["RequestSecurity__PasswordPermitLimit"] = "2";
            start.Environment["RequestSecurity__PasswordWindowSeconds"] = "5";
            async Task Start()
            {
                if (server is { HasExited: false }) { server.Kill(true); await server.WaitForExitAsync(); }
                server = Process.Start(start)!;
                server.OutputDataReceived += (_, e) => { if (e.Data != null) lock (errors) { errors.Add(e.Data); if (errors.Count > 20) errors.RemoveAt(0); } };
                server.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) errors.Add(e.Data); };
                server.BeginOutputReadLine(); server.BeginErrorReadLine();
                using var probe = Client();
                for (var i = 0; i < 100; i++)
                {
                    try { using var response = await probe.GetAsync("account"); if (response.StatusCode == HttpStatusCode.Unauthorized) return; }
                    catch (HttpRequestException) { }
                    if (server.HasExited) throw new InvalidOperationException("Isolated security API exited: " + string.Join('\n', errors));
                    await Task.Delay(200);
                }
                throw new InvalidOperationException("Security test API not ready.");
            }
            HttpClient Client() => new(new HttpClientHandler { CookieContainer = new CookieContainer(), UseProxy = false })
                { BaseAddress = new Uri("http://127.0.0.1:5191/api/") };
            var password = "Aa1!" + Guid.NewGuid().ToString("N");
            var changedPassword = "Bb2!" + Guid.NewGuid().ToString("N");
            object Registration(string email, string? pwd = null) => new { email, password = pwd ?? password, name = "Security", surname = "Fixture", phoneNumber = "+70000000000" };
            Task<HttpResponseMessage> Login(HttpClient client, string email, string pwd) => client.PostAsJsonAsync("account/login", new { email, password = pwd });
            async Task<string> Rejected(Task<HttpResponseMessage> task, string label)
            {
                using var response = await task;
                check(response.StatusCode == HttpStatusCode.BadRequest && !response.Headers.Contains("Set-Cookie"), label);
                return await response.Content.ReadAsStringAsync();
            }
            int Retry(HttpResponseMessage response)
            {
                var seconds = (int)(response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 0);
                check(response.StatusCode == HttpStatusCode.TooManyRequests && seconds > 0, "429 supplies positive Retry-After");
                return seconds;
            }
            await Start();
            using var browser = Client();
            using (var response = await browser.PostAsJsonAsync("account/register", Registration("alice@security.local")))
            {
                check(response.IsSuccessStatusCode, "isolated registration succeeds");
                var cookie = string.Join(';', response.Headers.GetValues("Set-Cookie"));
                check(cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase) && cookie.Contains("samesite=lax", StringComparison.OrdinalIgnoreCase), "identity cookie is HttpOnly and SameSite=Lax");
            }
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(builder.ConnectionString).Options);
            var alice = await db.Users.AsNoTracking().SingleAsync(x => x.Email == "alice@security.local");
            var originalHash = alice.PasswordHash;
            check(alice.LockoutEnabled, "new users have account lockout enabled");
            using (var logout = await browser.PostAsync("account/logout", null)) logout.EnsureSuccessStatusCode();
            var unknown = await Rejected(Login(browser, "missing@security.local", password), "unknown login returns generic failure without cookie");
            var wrong = await Rejected(Login(browser, "alice@security.local", "wrong"), "wrong password fails without cookie");
            check(unknown == wrong, "unknown account and wrong password have identical response");
            await Rejected(Login(browser, "alice@security.local", "wrong"), "second wrong password rejected");
            await Rejected(Login(browser, "alice@security.local", "wrong"), "third wrong password locks account");
            alice = await db.Users.AsNoTracking().SingleAsync(x => x.Id == alice.Id);
            check(alice.LockoutEnd > DateTimeOffset.UtcNow && alice.PasswordHash == originalHash, "lockout persists in PostgreSQL without changing password");
            check(await Rejected(Login(browser, "alice@security.local", password), "correct password cannot bypass temporary lockout") == wrong,
                "locked account response does not reveal lock status");
            using (var limited = await Login(browser, "alice@security.local", password))
            {
                var seconds = Retry(limited);
                check(!limited.Headers.Contains("Set-Cookie"), "limited login never issues session");
                await Task.Delay(TimeSpan.FromSeconds(seconds + 1));
            }
            string oldCookie;
            using (var response = await Login(browser, "ALICE@security.local", password))
            {
                check(response.IsSuccessStatusCode, "login recovers after lockout and IP window expire");
                oldCookie = string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(x => x.Split(';')[0]));
            }
            using (var current = await browser.GetAsync("account")) check(current.IsSuccessStatusCode, "recovered session is usable");
            alice = await db.Users.AsNoTracking().SingleAsync(x => x.Id == alice.Id);
            check(alice.AccessFailedCount == 0, "successful login resets failed-attempt counter");
            foreach (var origin in new[] { "http://localhost.evil.example", "http://evil.localhost", "null" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "account/login") { Content = JsonContent.Create(new { email = "alice@security.local", password }) };
                request.Headers.Add("Origin", origin);
                using var denied = await browser.SendAsync(request);
                check(denied.StatusCode == HttpStatusCode.Forbidden, "foreign browser origin cannot mutate session");
            }
            using (var crossSite = new HttpRequestMessage(HttpMethod.Post, "account/logout"))
            {
                crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
                using var denied = await browser.SendAsync(crossSite);
                check(denied.StatusCode == HttpStatusCode.Forbidden, "cross-site logout without Origin rejected");
            }
            using (var cors = new HttpRequestMessage(HttpMethod.Options, "account/login"))
            {
                cors.Headers.Add("Origin", "http://127.0.0.1:5173"); cors.Headers.Add("Access-Control-Request-Method", "POST");
                using var allowed = await browser.SendAsync(cors);
                check(allowed.Headers.GetValues("Access-Control-Allow-Origin").Single() == "http://127.0.0.1:5173", "exact development origin allowed");
            }
            using (var cors = new HttpRequestMessage(HttpMethod.Options, "account/login"))
            {
                cors.Headers.Add("Origin", "http://localhost.evil.example"); cors.Headers.Add("Access-Control-Request-Method", "POST");
                using var denied = await browser.SendAsync(cors);
                check(!denied.Headers.Contains("Access-Control-Allow-Origin"), "substring localhost origin is not allowed by CORS");
            }
            using (var first = await browser.PatchAsJsonAsync("account/password", new { oldPassword = "wrong", newPassword = changedPassword })) check(first.StatusCode == HttpStatusCode.BadRequest, "wrong old password rejected");
            using (var second = await browser.PatchAsJsonAsync("account/password", new { oldPassword = "wrong", newPassword = changedPassword })) check(second.StatusCode == HttpStatusCode.BadRequest, "password attempts counted");
            using (var limited = await browser.PatchAsJsonAsync("account/password", new { oldPassword = password, newPassword = changedPassword }))
            {
                var seconds = Retry(limited);
                check((await db.Users.AsNoTracking().SingleAsync(x => x.Id == alice.Id)).PasswordHash == originalHash, "rate-limited password change has no side effects");
                await Task.Delay(TimeSpan.FromSeconds(seconds + 1));
            }
            using (var change = await browser.PatchAsJsonAsync("account/password", new { oldPassword = password, newPassword = changedPassword })) check(change.IsSuccessStatusCode, "password change recovers after window");
            using (var request = new HttpRequestMessage(HttpMethod.Get, "account"))
            using (var oldSession = Client())
            {
                request.Headers.Add("Cookie", oldCookie);
                using var response = await oldSession.SendAsync(request);
                check(response.StatusCode == HttpStatusCode.Unauthorized, "password change revokes previously issued cookies");
            }
            using (var response = await browser.GetAsync("account")) check(response.IsSuccessStatusCode, "password change refreshes current authenticated session");
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_security_role() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'Injected role creation failure'; END $$;
                CREATE TRIGGER reject_security_role BEFORE INSERT ON "AspNetUserRoles"
                FOR EACH ROW EXECUTE FUNCTION reject_security_role();
                """);
            try
            {
                using var rejected = await browser.PostAsJsonAsync("account/register", Registration("failed-role@security.local"));
                check(rejected.StatusCode == HttpStatusCode.InternalServerError && !rejected.Headers.Contains("Set-Cookie")
                    && !await db.Users.AnyAsync(x => x.Email == "failed-role@security.local"), "role failure rolls back registration without partial account or session");
            }
            finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_security_role ON \"AspNetUserRoles\"; DROP FUNCTION reject_security_role();"); }
            using (var oversized = await browser.PostAsJsonAsync("account/login", new { email = "alice@security.local", password = new string('x', 12000) }))
                check(oversized.StatusCode == HttpStatusCode.RequestEntityTooLarge, "oversized login body rejected with 413");
            // Restart with fresh limiter state to isolate registration and global limiter checks.
            start.Environment["RequestSecurity__RegisterPermitLimit"] = "2";
            start.Environment["RequestSecurity__RegisterWindowSeconds"] = "60";
            await Start();
            using (var weak = await browser.PostAsJsonAsync("account/register", Registration("weak@security.local", "short"))) check(weak.StatusCode == HttpStatusCode.BadRequest, "weak new password rejected before account creation");
            using (var valid = await browser.PostAsJsonAsync("account/register", Registration("bob@security.local"))) check(valid.IsSuccessStatusCode, "second registration attempt succeeds");
            using (var limited = await browser.PostAsJsonAsync("account/register", Registration("limited@security.local"))) Retry(limited);
            check(!await db.Users.AnyAsync(x => x.Email == "weak@security.local" || x.Email == "limited@security.local"), "invalid and rate-limited registration create no users");
            start.Environment["RequestSecurity__ReadPermitLimit"] = "3";
            start.Environment["RequestSecurity__ApiWindowSeconds"] = "60";
            await Start(); // readiness probe uses one read permit
            using var anonymous = Client();
            for (var i = 0; i < 2; i++) { using var response = await anonymous.GetAsync("account"); check(response.StatusCode == HttpStatusCode.Unauthorized, "anonymous read counted before authentication"); }
            using (var forged = new HttpRequestMessage(HttpMethod.Get, "account"))
            {
                forged.Headers.Add("X-Forwarded-For", "198.51.100.12"); forged.Headers.Add("X-Real-IP", "198.51.100.13");
                using var limited = await anonymous.SendAsync(forged); Retry(limited);
            }
            start.Environment["RequestSecurity__ReadPermitLimit"] = "600";
            start.Environment["RequestSecurity__WritePermitLimit"] = "2";
            await Start();
            for (var i = 0; i < 2; i++) { using var response = await anonymous.PatchAsJsonAsync("order/status", new { }); check(response.StatusCode == HttpStatusCode.Unauthorized, "unauthorized writes counted by global limiter"); }
            using (var limited = await anonymous.PatchAsJsonAsync("order/status", new { })) Retry(limited);
            start.Environment["RequestSecurity__WritePermitLimit"] = "120";
            start.Environment.Remove("RequestSecurity__RequireHttpsCookies");
            await Start();
            using (var secured = await Login(anonymous, "alice@security.local", changedPassword))
            {
                check(secured.IsSuccessStatusCode && secured.Headers.GetValues("Set-Cookie").Any(x => x.Contains("secure", StringComparison.OrdinalIgnoreCase)), "Production cookie requires HTTPS by default");
            }
            var blockedUntil = DateTimeOffset.UtcNow.AddMinutes(5);
            await db.Users.Where(x => x.Id == alice.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LockoutEnd, blockedUntil));
            var persistedUntil = (await db.Users.AsNoTracking().SingleAsync(x => x.Id == alice.Id)).LockoutEnd;
            await Start();
            await Rejected(Login(anonymous, "alice@security.local", changedPassword), "server restart does not bypass persisted account block");
            check((await db.Users.AsNoTracking().SingleAsync(x => x.Id == alice.Id)).LockoutEnd == persistedUntil, "startup preserves existing block deadline");
            using (var swagger = await anonymous.GetAsync("../swagger/index.html")) check(swagger.StatusCode == HttpStatusCode.NotFound, "Swagger is not exposed in Production");
        }
        finally
        {
            if (server is { HasExited: false }) { server.Kill(true); await server.WaitForExitAsync(); }
            NpgsqlConnection.ClearAllPools();
            await new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", maintenance).ExecuteNonQueryAsync();
            if (temp.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(temp) == name) Directory.Delete(temp, true);
        }
    }
}
