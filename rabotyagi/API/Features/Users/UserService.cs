using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using rabotyagi.API.Features.Users.DTOs;
using rabotyagi.API.Utils;
using rabotyagi.Core.Consts;
using rabotyagi.Core.Response;
using rabotyagi.DataAccess;

namespace rabotyagi.API.Features.Users;

public class UserService(AppDbContext context, UserManager<DataAccess.Entities.User> userManager, ClaimsPrincipalUtils claimsPrincipal)
{
    public async Task<ApiResponse<List<UserDto>>> GetUsers(int page = 1, string? search = null)
    {
        var query = context.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(x => (x.Email != null && x.Email.Contains(search)) || x.Name.Contains(search) || x.Surname.Contains(search));
        var users = await query.OrderBy(x => x.Created).Skip((Math.Clamp(page, 1, 100000) - 1) * 50).Take(50).ToListAsync();
        var ids = users.Select(x => x.Id).ToList();
        var roles = await (from ur in context.UserRoles
                           join r in context.Roles on ur.RoleId equals r.Id
                           where ids.Contains(ur.UserId)
                           select new
                           {
                               ur.UserId,
                               r.Name
                           }).ToListAsync();
        return new ApiResponse<List<UserDto>>().WithStatusCode(HttpStatusCode.OK).WithData(users.Select(x => new UserDto
        {
            Id = x.Id,
            Name = x.Name + " " + x.Surname,
            Email = x.Email,
            PhoneNumber = x.PhoneNumber,
            Roles = roles.Where(r => r.UserId == x.Id).Select(r => r.Name!).ToList(),
            Blocked = x.LockoutEnd > DateTimeOffset.UtcNow
        }).ToList());
    }

    public async Task<ApiResponse> SetRole(Guid userId, SetUserRoleDto dto)
    {
        var response = new ApiResponse();
        if (userId == claimsPrincipal.ReqUserId)
            return response.WithStatusCode(HttpStatusCode.BadRequest).WithErrorMsg("Нельзя изменить собственную роль.");
        if (!RolesConstants.AllRoles.Contains(dto.Role))
            return response.WithStatusCode(HttpStatusCode.BadRequest).WithErrorMsg("Неизвестная роль.");
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user == null)
            return response.WithStatusCode(HttpStatusCode.NotFound).WithErrorMsg("Пользователь не найден.");
        await using var transaction = await context.Database.BeginTransactionAsync();
        var oldRoles = await userManager.GetRolesAsync(user);
        var result = await userManager.RemoveFromRolesAsync(user, oldRoles);
        if (result.Succeeded)
            result = await userManager.AddToRoleAsync(user, dto.Role);
        if (!result.Succeeded)
            return response.WithStatusCode(HttpStatusCode.BadRequest).WithErrorMsg(string.Join(", ", result.Errors.Select(x => x.Description)));
        user.RoleId = await context.Roles.Where(x => x.Name == dto.Role).Select(x => x.Id).FirstAsync();
        user.AvailableUntil = null;
        user.Updated = DateTimeOffset.UtcNow;
        result = await userManager.UpdateSecurityStampAsync(user);
        if (!result.Succeeded)
            return response.WithStatusCode(HttpStatusCode.BadRequest).WithErrorMsg(string.Join(", ", result.Errors.Select(x => x.Description)));
        await transaction.CommitAsync();
        return response.WithStatusCode(HttpStatusCode.OK);
    }

    public async Task<ApiResponse> SetBlocked(Guid userId, SetUserBlockedDto dto)
    {
        var response = new ApiResponse();
        if (userId == claimsPrincipal.ReqUserId)
            return response.WithStatusCode(HttpStatusCode.BadRequest).WithErrorMsg("Нельзя заблокировать собственный аккаунт.");
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user == null)
            return response.WithStatusCode(HttpStatusCode.NotFound).WithErrorMsg("Пользователь не найден.");
        user.LockoutEnabled = true;
        user.LockoutEnd = dto.Blocked ? DateTimeOffset.UtcNow.AddYears(100) : null;
        user.AvailableUntil = null;
        user.Updated = DateTimeOffset.UtcNow;
        var result = await userManager.UpdateSecurityStampAsync(user);
        if (!result.Succeeded)
            return response.WithStatusCode(HttpStatusCode.BadRequest).WithErrorMsg(string.Join(", ", result.Errors.Select(x => x.Description)));
        return response.WithStatusCode(HttpStatusCode.OK);
    }
}
