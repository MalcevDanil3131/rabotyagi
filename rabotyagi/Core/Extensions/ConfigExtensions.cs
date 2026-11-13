using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using rabotyagi.Core.Consts;
using rabotyagi.DataAccess;
using rabotyagi.DataAccess.Entities;

namespace rabotyagi.Core.Extensions;

public static class ConfigExtensions
{
    public static async Task SeedRolesAndAdminAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        await InitializeIdentityAsync(scope.ServiceProvider, app.Configuration);
    }

    // No demo-account seeder: fixtures belong exclusively to the isolated test harness.
    public static async Task InitializeIdentityAsync(IServiceProvider services, IConfiguration configuration)
    {
        var roles = services.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var roleName in RolesConstants.AllRoles)
            if (!await roles.RoleExistsAsync(roleName))
                CheckResult(await roles.CreateAsync(new IdentityRole<Guid> { Name = roleName }), "Role initialization failed.");

        if (!configuration.GetValue<bool>("BootstrapAdmin:Enabled")) return;
        var email = configuration["BootstrapAdmin:Email"]?.Trim();
        var password = configuration["BootstrapAdmin:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("BootstrapAdmin requires Email and Password. Disable bootstrap after initial provisioning.");

        var db = services.GetRequiredService<AppDbContext>();
        var users = services.GetRequiredService<UserManager<User>>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        // Never adopt an existing account, even if it currently has elevated privileges.
        if (await users.FindByEmailAsync(email) != null || await users.FindByNameAsync(email) != null)
            throw new InvalidOperationException("BootstrapAdmin account already exists. No account was changed. Disable bootstrap or provision a different new account.");
        var role = await roles.FindByNameAsync(RolesConstants.SuperAdminRole)
            ?? throw new InvalidOperationException("SuperAdmin role is unavailable.");
        var user = new User { Name = "Administrator", Surname = "", Email = email, UserName = email, RoleId = role.Id };
        CheckResult(await users.CreateAsync(user, password), "BootstrapAdmin creation failed; check the email and password policy.");
        CheckResult(await users.AddToRoleAsync(user, RolesConstants.SuperAdminRole), "BootstrapAdmin role assignment failed.");
        await transaction.CommitAsync();
    }

    private static void CheckResult(IdentityResult result, string message)
    {
        // Validators can include submitted values in error descriptions; do not log them.
        if (!result.Succeeded) throw new InvalidOperationException(message);
    }
}
