using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using rabotyagi.API.Features.Users.DTOs;
using rabotyagi.Core.Consts;

namespace rabotyagi.API.Features.Users;

[ApiController]
[Route("api/user")]
[Authorize(Roles = RolesConstants.SuperAdminRole)]
public class UserApiController(UserService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult> GetUsers(int page = 1, string? search = null) => await service.GetUsers(page, search);

    [HttpPut("{userId:guid}/role")]
    public async Task<ActionResult> SetRole(Guid userId, SetUserRoleDto dto) => await service.SetRole(userId, dto);

    [HttpPut("{userId:guid}/blocked")]
    public async Task<ActionResult> SetBlocked(Guid userId, SetUserBlockedDto dto) => await service.SetBlocked(userId, dto);
}
