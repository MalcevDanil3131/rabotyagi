using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using rabotyagi.API.Features.Account.DTOs;

namespace rabotyagi.API.Features.Account;

[ApiController]
[Route("api/account")]
public class AccountApiController(AccountService accountService) : ControllerBase
{
    /// <summary> Регистрирует пользователя </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    public async Task<ActionResult> Register([FromBody] RegisterUserDto dto) =>
        await accountService.Register(dto);
    
    /// <summary> Логинит пользователя </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    public async Task<ActionResult> LogIn([FromBody] LogInDto dto) =>
        await accountService.LogIn(dto);
    
    /// <summary> Выход из аккаунта </summary>
    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(200)]
    public async Task<ActionResult> LogOut() =>
        await accountService.LogOut();
    
    /// <summary> Возвращает информацию об аккаунте </summary>
    [HttpGet]
    [Authorize]
    [ProducesResponseType(typeof(AccountInfoDto),200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult> GetAccountInfo() =>
        await accountService.GetAccountInfo();

    /// <summary> Изменяет пароль пользователя </summary>
    [HttpPatch("password")]
    [Authorize]
    [ProducesResponseType(200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult> ChangePassword([FromBody] ChangePasswordDto dto) =>
        await accountService.ChangePassword(dto);
}