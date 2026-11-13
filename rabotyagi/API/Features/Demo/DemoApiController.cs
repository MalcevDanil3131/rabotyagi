using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using rabotyagi.DataAccess.Entities;

namespace rabotyagi.API.Features.Demo;

[ApiController, Route("api/demo"), AllowAnonymous]
public class DemoApiController(DemoRuntime runtime, UserManager<User> users, SignInManager<User> signIn) : ControllerBase
{
    [HttpGet("accounts")]
    public ActionResult Accounts() => runtime.Allows(Request)
        ? Ok(DemoAccounts.All.Select(x => new { x.Key, x.Id, x.Label, x.Role })) : NotFound();

    [HttpPost("login/{key}")]
    [EnableRateLimiting("demo-login")]
    public async Task<ActionResult> Login(string key)
    {
        if (!runtime.Allows(Request)) return NotFound();
        var entry = DemoAccounts.All.SingleOrDefault(x => x.Key == key);
        if (entry == null) return NotFound();
        var user = await users.FindByIdAsync(entry.Id.ToString());
        if (user == null || !(await users.GetClaimsAsync(user)).Any(x => x.Type == DemoAccounts.ClaimType && x.Value == entry.Key))
            return NotFound();
        var roles = await users.GetRolesAsync(user);
        if (user.LockoutEnd > DateTimeOffset.UtcNow || roles.Count != 1 || roles[0] != entry.Role)
            return Conflict(new { error = "Демо-аккаунт заблокирован или его роль изменена. Восстановите его через управление пользователями." });
        await signIn.SignOutAsync();
        await signIn.SignInAsync(user, isPersistent: false);
        return Ok();
    }
}
