using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using rabotyagi.API.Features.Account.DTOs;
using rabotyagi.API.Utils;
using rabotyagi.Core.Consts;
using rabotyagi.Core.Response;
using rabotyagi.DataAccess;
using rabotyagi.DataAccess.Entities;

namespace rabotyagi.API.Features.Account;

public class AccountService(AppDbContext context, SignInManager<User> signInManager, UserManager<User> userManager, ClaimsPrincipalUtils claimsPrincipalUtils)
{
    public async Task<ApiResponse> Register(RegisterUserDto dto)
    {
        var response = new ApiResponse();
        var userRole = await context.Roles.FirstOrDefaultAsync(x => x.Name == RolesConstants.UserRole);

        var user = new User
        {
            Name = dto.Name,
            Surname = dto.Surname,
            PhoneNumber = dto.PhoneNumber,
            Email = dto.Email,
            UserName = dto.Email,
            RoleId = userRole!.Id,
        };

        var result = await userManager.CreateAsync(user, dto.Password);
        if (!result.Succeeded)
        {
            var errors = result.Errors.Select(e => e.Description);
            return response
                .WithStatusCode(HttpStatusCode.BadRequest)
                .WithErrorMsg(string.Join(',', errors));
        }

        var roleResult = await userManager.AddToRoleAsync(user, userRole!.Name!);
        if (!roleResult.Succeeded)
            return response.WithStatusCode(HttpStatusCode.BadRequest)
                .WithErrorMsg(string.Join(", ", roleResult.Errors.Select(x => x.Description)));
        await signInManager.SignInAsync(user, dto.IsPersistent);

        return response.WithStatusCode(HttpStatusCode.OK);
    }

    public async Task<ApiResponse> LogIn(LogInDto dto)
    {
        var response = new ApiResponse();
        var user = await userManager.FindByEmailAsync(dto.Email);
        if (user == null)
        {
            return response
                .WithStatusCode(HttpStatusCode.BadRequest)
                .WithErrorMsg("User not found");
        }

        var result = await signInManager.PasswordSignInAsync(user, dto.Password,
            dto.IsPersistent, false);
        if (!result.Succeeded)
        {
            return response
                .WithStatusCode(HttpStatusCode.BadRequest)
                .WithErrorMsg("User not found");
        }

        return response.WithStatusCode(HttpStatusCode.OK);
    }

    public async Task<ApiResponse> LogOut()
    {
        await signInManager.SignOutAsync();
        return new ApiResponse().WithStatusCode(HttpStatusCode.OK);
    }

    public async Task<ApiResponse<AccountInfoDto>> GetAccountInfo()
    {
        var response = new ApiResponse<AccountInfoDto>();

        var user = await context.Users
            .Include(u => u.Address)
            .FirstOrDefaultAsync(u => u.Id == claimsPrincipalUtils.ReqUserId);

        if (user == null)
            return response.WithStatusCode(HttpStatusCode.NotFound).WithErrorMsg("User not found");

        var roles = await userManager.GetRolesAsync(user);
        var dto = new AccountInfoDto
        {
            Id = user.Id,
            Email = user.Email,
            Name = user.Name,
            Surname = user.Surname,
            PhoneNumber = user.PhoneNumber,
            RoleName = roles.FirstOrDefault() ?? RolesConstants.UserRole,
            Roles = roles,
            AvailableUntil = user.AvailableUntil,
            Address = user.Address == null
                ? null
                : new AddressItemDto
                {
                    Id = user.Address.Id,
                    Country = user.Address.Country,
                    City = user.Address.City,
                    Street = user.Address.Street,
                    HouseNumber = user.Address.HouseNumber,
                    ApartmentNumber = user.Address.ApartmentNumber,
                    FloorNumber = user.Address.FloorNumber,
                    IntercomNumber = user.Address.IntercomNumber,
                }
        };

        return response.WithData(dto).WithStatusCode(HttpStatusCode.OK);
    }

    public async Task<ApiResponse> ChangePassword(ChangePasswordDto dto)
    {
        var response = new ApiResponse();

        var userId = claimsPrincipalUtils.ReqUserId;
        var user = await context.Users.FindAsync(userId);
        if (user == null)
            return response.WithStatusCode(HttpStatusCode.NotFound).WithErrorMsg("User not found");

        var result = await userManager.ChangePasswordAsync(user, dto.OldPassword, dto.NewPassword);

        if (!result.Succeeded)
            return response.WithStatusCode(HttpStatusCode.BadRequest).WithErrorMsg(string.Join(", ", result.Errors.Select(x => x.Description)));
        await userManager.UpdateSecurityStampAsync(user);
        await signInManager.RefreshSignInAsync(user);

        return response.WithStatusCode(HttpStatusCode.OK);
    }
}
