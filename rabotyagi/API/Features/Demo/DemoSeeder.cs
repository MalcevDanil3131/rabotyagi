using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using rabotyagi.DataAccess;
using rabotyagi.DataAccess.Entities;
using rabotyagi.DataAccess.Enums;

namespace rabotyagi.API.Features.Demo;

public static class DemoSeeder
{
    public static async Task SeedAsync(IServiceProvider services, IConfiguration config)
    {
        if (!services.GetRequiredService<DemoRuntime>().Enabled) throw new InvalidOperationException("Demo seeding is disabled.");
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var files = new List<string>();
        var committed = false;
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            // Serialize demo initialization, including version checks, across concurrent starts.
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8243102026)");
            if (await db.Users.AnyAsync())
            {
                // Never adopt users by email, or reset passwords, roles and other account data.
                foreach (var account in DemoAccounts.All)
                {
                    var existing = await users.FindByIdAsync(account.Id.ToString());
                    if (existing == null || !(await users.GetClaimsAsync(existing)).Any(x => x.Type == DemoAccounts.ClaimType && x.Value == account.Key))
                        throw new InvalidOperationException("This database is not an initialized Rabotyagi demo. Use a new empty demo database.");
                }
            }
            else
            {
                if (await db.Orders.AnyAsync() || await db.Addresses.AnyAsync())
                    throw new InvalidOperationException("Demo fixtures require an empty database.");
                foreach (var account in DemoAccounts.All)
                {
                    var role = await db.Roles.SingleAsync(x => x.Name == account.Role);
                    var user = new User { Id = account.Id, Name = account.Label, Surname = "Демо", Email = account.Key + "@demo.invalid",
                        UserName = account.Key + "@demo.invalid", RoleId = role.Id,
                        AvailableUntil = account.Role == "Worker" ? DateTimeOffset.UtcNow.AddHours(12) : null };
                    // Local quick login can select only these marked IDs; no reusable password.
                    Ensure(await users.CreateAsync(user));
                    Ensure(await users.AddToRoleAsync(user, account.Role));
                    Ensure(await users.AddClaimAsync(user, new Claim(DemoAccounts.ClaimType, account.Key)));
                }
            }
            await DemoScenarios.AddMissingAsync(db, config, files);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            committed = true;
        }
        finally
        {
            if (!committed) foreach (var file in files) File.Delete(file);
        }
    }

    private static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException("Could not initialize demo accounts.");
    }
}
