using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using rabotyagi.API.Features.Account.DTOs;
using rabotyagi.API.Utils;
using rabotyagi.Core.Consts;
using rabotyagi.Core.Response;
using rabotyagi.DataAccess;
using rabotyagi.DataAccess.Entities;
using rabotyagi.Core.Extensions;

namespace rabotyagi.API.Features.Account;

public class AccountService(AppDbContext context, SignInManager<User> signInManager, UserManager<User> userManager, ClaimsPrincipalUtils claimsPrincipalUtils,
    DummyPasswordVerifier dummyPassword)
{
    public async Task<ApiResponse> Register(RegisterUserDto dto)
    {
        var response = new ApiResponse();
        var userRole = await context.Roles.FirstOrDefaultAsync(x => x.Name == RolesConstants.UserRole);

        await using var transaction = await context.Database.BeginTransactionAsync();
        var user = new User
        {
            Name = dto.Name,
            Surname = dto.Surname,
            PhoneNumber = dto.PhoneNumber,
            Email = dto.Email.Trim(),
            UserName = dto.Email.Trim(),
            RoleId = userRole!.Id,
        };

        var result = await userManager.CreateAsync(user, dto.Password);
        if (!result.Succeeded)
        {
            return response
                .WithStatusCode(HttpStatusCode.BadRequest)
                .WithErrorMsg("Не удалось создать аккаунт. Проверьте данные и требования к паролю.");
        }

        var roleResult = await userManager.AddToRoleAsync(user, userRole!.Name!);
        if (!roleResult.Succeeded)
            return response.WithStatusCode(HttpStatusCode.BadRequest)
                .WithErrorMsg("Не удалось создать аккаунт. Попробуйте позже.");
        await transaction.CommitAsync();
        await signInManager.SignInAsync(user, dto.IsPersistent);

        return response.WithStatusCode(HttpStatusCode.OK);
    }

    public async Task<ApiResponse> LogIn(LogInDto dto)
    {
        var response = new ApiResponse();
        var user = await userManager.FindByEmailAsync(dto.Email.Trim());
        if (user == null)
        {
            dummyPassword.Verify(dto.Password);
            return response
                .WithStatusCode(HttpStatusCode.BadRequest)
                .WithErrorMsg("Не удалось войти. Проверьте почту и пароль или повторите попытку позже.");
        }

        var result = await signInManager.PasswordSignInAsync(user, dto.Password,
            dto.IsPersistent, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            if (result.IsLockedOut || result.IsNotAllowed) dummyPassword.Verify(dto.Password);
            return response
                .WithStatusCode(HttpStatusCode.BadRequest)
                .WithErrorMsg("Не удалось войти. Проверьте почту и пароль или повторите попытку позже.");
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
            return response.WithStatusCode(HttpStatusCode.BadRequest).WithErrorMsg("Не удалось изменить пароль. Проверьте текущий пароль и требования к новому.");
        // ChangePasswordAsync already updates the security stamp atomically with the hash.
        await signInManager.RefreshSignInAsync(user);

        return response.WithStatusCode(HttpStatusCode.OK);
    }
}
