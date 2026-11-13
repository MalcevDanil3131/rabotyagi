using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using rabotyagi.Core.Consts;
using rabotyagi.Core.Extensions;
using rabotyagi.DataAccess;
using rabotyagi.DataAccess.Entities;

internal static class BootstrapChecks
{
    public static async Task Run(string connectionString, string password, Action<bool, string> check)
    {
        if (!new NpgsqlConnectionStringBuilder(connectionString).Database!.StartsWith("rabotyagi_checks_"))
            throw new InvalidOperationException("Bootstrap checks require an isolated database.");
        var failure = new RejectRoleAssignment();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString).AddInterceptors(failure));
        services.AddIdentity<User, IdentityRole<Guid>>().AddEntityFrameworkStores<AppDbContext>().AddDefaultTokenProviders();
        await using var provider = services.BuildServiceProvider();
        IConfiguration Config(bool enabled, string email = "bootstrap@checks.local") => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BootstrapAdmin:Enabled"] = enabled.ToString(),
                ["BootstrapAdmin:Email"] = email,
                ["BootstrapAdmin:Password"] = password
            }).Build();
        async Task Initialize(IConfiguration config)
        {
            await using var scope = provider.CreateAsyncScope();
            await ConfigExtensions.InitializeIdentityAsync(scope.ServiceProvider, config);
        }
        async Task ExpectFailure(Func<Task> action)
        {
            var failed = false;
            try { await action(); }
            catch (InvalidOperationException) { failed = true; }
            catch (DbUpdateException) { failed = true; }
            check(failed, "bootstrap rejects invalid operation");
        }
        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        await Initialize(new ConfigurationBuilder().Build());
        await Initialize(Config(false));
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            check(await db.Users.CountAsync() == 0, "disabled bootstrap creates no accounts or demo users");
            check(await db.Roles.CountAsync() == RolesConstants.AllRoles.Length, "roles are created idempotently");
        }
        await Initialize(Config(true));
        await using (var scope = provider.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var admin = (await users.FindByEmailAsync("bootstrap@checks.local"))!;
            check(await users.CheckPasswordAsync(admin, password) && await users.IsInRoleAsync(admin, RolesConstants.SuperAdminRole), "explicit bootstrap creates administrator");
            var user = new User { Name = "Existing", Surname = "User", Email = "existing@checks.local", UserName = "existing@checks.local" };
            check((await users.CreateAsync(user, password)).Succeeded, "existing account fixture created");
            check((await users.AddToRoleAsync(user, RolesConstants.WorkerRole)).Succeeded, "existing fixture has worker role");
            user.LockoutEnd = DateTimeOffset.UtcNow.AddDays(2);
            check((await users.UpdateAsync(user)).Succeeded, "existing fixture is blocked");
        }
        string before;
        async Task<string> Snapshot()
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                Users = await db.Users.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
                Roles = await db.UserRoles.AsNoTracking().OrderBy(x => x.UserId).ThenBy(x => x.RoleId).ToListAsync()
            });
        }
        before = await Snapshot();
        await Initialize(Config(false));
        await ExpectFailure(() => Initialize(Config(true))); // Explicit conflict, including an existing admin.
        await ExpectFailure(() => Initialize(Config(true, "existing@checks.local")));
        check(await Snapshot() == before, "restarts and email conflicts preserve passwords, roles, stamps and blocking");
        failure.Enabled = true;
        try { await ExpectFailure(() => Initialize(Config(true, "rollback@checks.local"))); }
        finally { failure.Enabled = false; }
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            check(!await db.Users.AnyAsync(x => x.Email == "rollback@checks.local"), "role assignment failure rolls back newly created account");
        }
    }

    private sealed class RejectRoleAssignment : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<IdentityUserRole<Guid>>().Any(x => x.State == EntityState.Added))
                throw new DbUpdateException("Injected role assignment failure.");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
