using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using rabotyagi.Core.Consts;
using rabotyagi.DataAccess.Entities;

namespace rabotyagi.Core.Extensions;

public static class ConfigExtensions
{
    public static async Task SeedRolesAndAdminAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var userManager = services.GetRequiredService<UserManager<User>>();
        const string password = "Wertukan!234";

        foreach (var roleName in RolesConstants.AllRoles)
        {
            if (!await roleManager.RoleExistsAsync(roleName))
                CheckResult(await roleManager.CreateAsync(new IdentityRole<Guid> { Name = roleName }));

            var role = await roleManager.Roles.FirstAsync(x => x.Name == roleName);
            var email = $"{roleName.ToLowerInvariant()}@mail.ru";
            var user = await userManager.FindByEmailAsync(email);

            if (user == null)
            {
                user = new User
                {
                    Name = roleName,
                    Surname = roleName,
                    UserName = email,
                    Email = email,
                    RoleId = role.Id
                };

                CheckResult(await userManager.CreateAsync(user, password));
            }
            else
            {
                user.RoleId = role.Id;
                CheckResult(await userManager.UpdateAsync(user));

                if (!await userManager.CheckPasswordAsync(user, password))
                {
                    var token = await userManager.GeneratePasswordResetTokenAsync(user);
                    CheckResult(await userManager.ResetPasswordAsync(user, token, password));
                }
            }

            var otherRoles = (await userManager.GetRolesAsync(user)).Where(x => x != roleName).ToList();
            if (otherRoles.Count > 0)
                CheckResult(await userManager.RemoveFromRolesAsync(user, otherRoles));

            if (!await userManager.IsInRoleAsync(user, roleName))
                CheckResult(await userManager.AddToRoleAsync(user, roleName));
        }
    }

    private static void CheckResult(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException(string.Join(", ", result.Errors.Select(x => x.Description)));
    }
}
