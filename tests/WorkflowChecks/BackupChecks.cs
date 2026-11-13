using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using rabotyagi.API.Features.Demo;
using rabotyagi.Core.Extensions;
using rabotyagi.DataAccess;
using rabotyagi.DataAccess.Entities;

internal static class BackupChecks
{
    public static async Task Run(string testConnection, string root, Action<bool, string> check)
    {
        var builder = new NpgsqlConnectionStringBuilder(testConnection) { Database = "postgres", Pooling = false };
        if (builder.Host != "127.0.0.1" || builder.Port != 55439 || builder.Username != "demo_admin")
            throw new InvalidOperationException("Backup checks require the dedicated local demo PostgreSQL on port 55439.");
        var id = Guid.NewGuid().ToString("N");
        var source = "rabotyagi_demo_backupcheck_" + id;
        var target = "rabotyagi_demo_restore_" + Guid.NewGuid().ToString("N");
        var failure = "rabotyagi_demo_restore_" + Guid.NewGuid().ToString("N");
        var work = Path.Combine(root, ".tools/backup-runtime/checks", id);
        var sourcePhotos = Path.Combine(work, "source-uploads");
        Directory.CreateDirectory(sourcePhotos);
        await using var maintenance = new NpgsqlConnection(builder.ConnectionString);
        await maintenance.OpenAsync();
        await new NpgsqlCommand($"CREATE DATABASE \"{source}\"", maintenance).ExecuteNonQueryAsync();
        builder.Database = source;
        var sourceConnection = builder.ConnectionString;
        Process? api = null;
        var testPassword = "Aa1!" + Guid.NewGuid().ToString("N");
        try
        {
            ServiceProvider Provider(string connection)
            {
                var services = new ServiceCollection();
                services.AddLogging();
                services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));
                services.AddIdentity<User, IdentityRole<Guid>>().AddEntityFrameworkStores<AppDbContext>().AddDefaultTokenProviders();
                services.AddSingleton(new DemoRuntime(true));
                return services.BuildServiceProvider();
            }
            await using (var provider = Provider(sourceConnection))
            {
                await using (var scope = provider.CreateAsyncScope())
                    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Uploads:Path"] = sourcePhotos }).Build();
                await ConfigExtensions.InitializeIdentityAsync(provider, config);
                await DemoSeeder.SeedAsync(provider, config);
                await using var scope2 = provider.CreateAsyncScope();
                var users = scope2.ServiceProvider.GetRequiredService<UserManager<User>>();
                var fixture = new User { Name = "Backup", Surname = "Fixture", Email = "backup@checks.invalid", UserName = "backup@checks.invalid" };
                check((await users.CreateAsync(fixture, testPassword)).Succeeded && (await users.AddToRoleAsync(fixture, "User")).Succeeded, "disposable password fixture exists only in backup test database");
                var db = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
                var order = await db.Orders.FirstAsync();
                db.OrderMessages.Add(new OrderMessage { OrderId = order.Id, SenderId = DemoAccounts.All[0].Id, Text = "Проверка восстановления: цена, история и фото — ✓" });
                await db.SaveChangesAsync();
            }
            async Task<Dictionary<string, string>> Fingerprints(string connection)
            {
                await using var db = new NpgsqlConnection(connection);
                await db.OpenAsync();
                var tables = new List<string>();
                await using (var command = new NpgsqlCommand("SELECT tablename FROM pg_tables WHERE schemaname='public' ORDER BY tablename", db))
                await using (var reader = await command.ExecuteReaderAsync())
                    while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
                var values = new Dictionary<string, string>();
                foreach (var table in tables)
                {
                    var escaped = table.Replace("\"", "\"\"");
                    var sql = $"SELECT count(*)::text || ':' || md5(coalesce(string_agg(row_to_json(t)::text, '' ORDER BY row_to_json(t)::text),'')) FROM \"{escaped}\" t";
                    values[table] = (string)(await new NpgsqlCommand(sql, db).ExecuteScalarAsync())!;
                }
                values["@constraints"] = (string)(await new NpgsqlCommand("SELECT md5(coalesce(string_agg(conname || ':' || pg_get_constraintdef(oid), '' ORDER BY conname),'')) FROM pg_constraint WHERE connamespace='public'::regnamespace", db).ExecuteScalarAsync())!;
                values["@indexes"] = (string)(await new NpgsqlCommand("SELECT md5(coalesce(string_agg(indexname || ':' || indexdef, '' ORDER BY indexname),'')) FROM pg_indexes WHERE schemaname='public'", db).ExecuteScalarAsync())!;
                return values; // Only row counts and digests; no account fields/password hashes are exported.
            }
            var before = await Fingerprints(sourceConnection);
            var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("BACKUP_CHECKS_PWSH") ?? "pwsh")
                { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-File", Path.Combine(root, "tests/DatabaseBackupChecks.ps1"), "-SourceDatabase", source,
                "-RestoreDatabase", target, "-FailureDatabase", failure, "-SourceUploads", sourcePhotos,
                "-OutputDirectory", Path.Combine(root, ".backups"), "-ResultPath", Path.Combine(work, "result.json") }) info.ArgumentList.Add(arg);
            using (var process = Process.Start(info)!)
            {
                var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Console.Write(await output);
                if (process.ExitCode != 0) throw new InvalidOperationException("Backup scripts failed: " + await errors);
            }
            var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(work, "result.json"))).RootElement;
            check(result.GetProperty("Checks").GetInt32() >= 12, "backup/restore and safety scripts passed their integration checks");
            builder.Database = target;
            var targetConnection = builder.ConnectionString;
            var restored = await Fingerprints(targetConnection);
            check(before.Count == restored.Count && before.All(x => restored.GetValueOrDefault(x.Key) == x.Value),
                "all tables, migrations, identity records, constraints and indexes match after restore");
            check((await Fingerprints(sourceConnection)).All(x => before.GetValueOrDefault(x.Key) == x.Value), "backup and restore leave source data unchanged");
            await using (var provider = Provider(targetConnection))
            await using (var scope = provider.CreateAsyncScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
                var fixture = await users.FindByEmailAsync("backup@checks.invalid");
                check(fixture != null && await users.CheckPasswordAsync(fixture, testPassword) && await users.IsInRoleAsync(fixture, "User"), "restored password hash and roles authenticate correctly");
            }
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Path.Combine(root, "rabotyagi"), UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(root, "rabotyagi/bin/Debug/net10.0/rabotyagi.dll"));
            start.Environment["ASPNETCORE_ENVIRONMENT"] = start.Environment["DOTNET_ENVIRONMENT"] = "Demo";
            start.Environment["Demo__Enabled"] = "true"; start.Environment["Demo__Port"] = "5192";
            start.Environment["BootstrapAdmin__Enabled"] = "false";
            start.Environment["ConnectionStrings__DemoConnection"] = targetConnection;
            start.Environment["ConnectionStrings__DefaultConnection"] = start.Environment["ConnectionStrings__ProductionConnection"] = "";
            start.Environment["Logging__EventLog__LogLevel__Default"] = "None";
            api = Process.Start(start)!;
            api.OutputDataReceived += (_, _) => { }; api.ErrorDataReceived += (_, _) => { };
            api.BeginOutputReadLine(); api.BeginErrorReadLine();
            using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), UseProxy = false })
                { BaseAddress = new Uri("http://127.0.0.1:5192/api/"), Timeout = TimeSpan.FromSeconds(3) };
            var ready = false;
            for (var i = 0; i < 100; i++)
            {
                try { using var response = await client.GetAsync("account"); ready = response.StatusCode == HttpStatusCode.Unauthorized; }
                catch (HttpRequestException) { }
                if (ready || api.HasExited) break;
                await Task.Delay(200);
            }
            check(ready, "application starts only on restored isolated database without pending migrations");
            client.DefaultRequestHeaders.Add("X-Rabotyagi-Demo", "1");
            using (var login = await client.PostAsync("demo/login/client", null)) check(login.IsSuccessStatusCode, "real cookie login works on restored database");
            var orders = JsonDocument.Parse(await client.GetStringAsync("order/page?pageSize=100")).RootElement.GetProperty("items").EnumerateArray().ToArray();
            check(orders.Length == 12, "restored API returns original demo orders without reseeding duplicates");
            var withReport = orders.First(x => x.GetProperty("photos").GetArrayLength() > 0);
            using (var photo = await client.GetAsync(withReport.GetProperty("photos")[0].GetProperty("url").GetString()!.Replace("/api/", "")))
                check(photo.IsSuccessStatusCode && (await photo.Content.ReadAsByteArrayAsync()).Length > 0, "restored photo file is served through authorized API");
            var history = JsonDocument.Parse(await client.GetStringAsync($"order/{withReport.GetProperty("id").GetGuid()}/history")).RootElement;
            check(history.GetArrayLength() > 0, "restored history remains available through authorized API");
            var pending = orders.First(x => x.GetProperty("status").GetString() == "workFinished");
            using (var accepted = await client.PostAsJsonAsync($"order/{pending.GetProperty("id").GetGuid()}/review", new {
                approved = true, expectedUpdated = pending.GetProperty("updated").GetString() }))
                check(accepted.IsSuccessStatusCode, "restored database accepts a real business mutation and new history event");
            await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(targetConnection).Options))
            {
                var lastClaimId = await db.UserClaims.MaxAsync(x => x.Id);
                var claim = new IdentityUserClaim<Guid> { UserId = DemoAccounts.All[0].Id, ClaimType = "backup-verification", ClaimValue = "new-record" };
                db.UserClaims.Add(claim); await db.SaveChangesAsync();
                check(claim.Id > lastClaimId, "restored identity sequence generates new unique identifiers");
            }
            var sourceAfter = await Fingerprints(sourceConnection);
            check(before.All(x => sourceAfter.GetValueOrDefault(x.Key) == x.Value),
                "mutations in restored copy do not change the source database");
            var reportPath = Path.Combine(work, "result.json");
            var report = JsonNode.Parse(await File.ReadAllTextAsync(reportPath))!.AsObject();
            report["Verification"] = new JsonObject { ["FinishedUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                ["TablesVerified"] = before.Count - 2, ["ConstraintsAndIndexesMatch"] = true, ["RestoredOrders"] = orders.Length,
                ["SourceUnchanged"] = true, ["PasswordAndRolesVerified"] = true, ["PhotoApiVerified"] = true,
                ["HistoryApiVerified"] = true, ["BusinessMutationVerified"] = true, ["SequenceVerified"] = true };
            await File.WriteAllTextAsync(reportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Encrypted test backup retained at: " + result.GetProperty("Backup").GetProperty("BackupPath").GetString());
            Console.WriteLine("Verification report: " + Path.Combine(work, "result.json"));
        }
        finally
        {
            if (api is { HasExited: false }) { api.Kill(true); await api.WaitForExitAsync(); }
            NpgsqlConnection.ClearAllPools();
            // Only names generated by this invocation; never drop a supplied/existing database.
            foreach (var database in new[] { failure, target, source })
            {
                if (!(database.StartsWith("rabotyagi_demo_restore_") || database == source)) throw new InvalidOperationException("Cleanup name guard failed.");
                await new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", maintenance).ExecuteNonQueryAsync();
            }
            var restoredRoot = Path.GetFullPath(Path.Combine(root, "rabotyagi/.demo", target));
            var demoRoot = Path.GetFullPath(Path.Combine(root, "rabotyagi/.demo")) + Path.DirectorySeparatorChar;
            if (!restoredRoot.StartsWith(demoRoot, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(restoredRoot) != target)
                throw new InvalidOperationException("Restore file cleanup path guard failed.");
            if (Directory.Exists(restoredRoot) && !new DirectoryInfo(restoredRoot).Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(restoredRoot, true);
            var sourcePath = Path.GetFullPath(sourcePhotos);
            var checkRoot = Path.GetFullPath(Path.Combine(root, ".tools/backup-runtime/checks")) + Path.DirectorySeparatorChar;
            if (!sourcePath.StartsWith(checkRoot, StringComparison.OrdinalIgnoreCase) || new DirectoryInfo(sourcePath).Parent?.Name != id)
                throw new InvalidOperationException("Source file cleanup path guard failed.");
            if (Directory.Exists(sourcePath) && !new DirectoryInfo(sourcePath).Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(sourcePath, true);
        }
    }
}
