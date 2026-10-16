using System.Net;
using Microsoft.EntityFrameworkCore;
using rabotyagi.API.Features.Address.DTOs;
using rabotyagi.API.Utils;
using rabotyagi.Core.Response;
using rabotyagi.DataAccess;
using AddressItemDto = rabotyagi.API.Features.Account.DTOs.AddressItemDto;

namespace rabotyagi.API.Features.Address;

public class AddressService(AppDbContext context, ClaimsPrincipalUtils claimsPrincipalUtils)
{
    public async Task<ApiResponse<AddressItemDto>> GetAddress()
    {
        var response = new ApiResponse<AddressItemDto>();

        var userId = claimsPrincipalUtils.ReqUserId;
        var address = await context.Addresses.Where(i => i.UserId == userId)
            .Select(a => new AddressItemDto
            {
                Id = a.Id,
                ApartmentNumber = a.ApartmentNumber,
                City = a.City,
                Country = a.Country,
                FloorNumber = a.FloorNumber,
                HouseNumber = a.HouseNumber,
                IntercomNumber = a.IntercomNumber,
                Street = a.Street
            }).FirstOrDefaultAsync();

        if (address == null)
            return response.WithStatusCode(HttpStatusCode.NotFound).WithErrorMsg("Address not found");

        return response.WithData(address).WithStatusCode(HttpStatusCode.OK);
    }

    public async Task<ApiResponse> SaveAddress(SaveAddressDto dto)
    {
        var response = new ApiResponse();

        var userId = claimsPrincipalUtils.ReqUserId;

        var user = await context.Users.Include(user => user.Address).FirstAsync(i => i.Id == userId);

        if (user.Address == null)
        {
            var address = new DataAccess.Entities.Address
            {
                HouseNumber = dto.HouseNumber,
                ApartmentNumber = dto.ApartmentNumber,
                City = dto.City,
                Country = dto.Country,
                FloorNumber = dto.FloorNumber,
                IntercomNumber = dto.IntercomNumber,
                Street = dto.Street,
                UserId = userId!.Value
            };

            await context.Addresses.AddAsync(address);
            await context.SaveChangesAsync();
            return response.WithStatusCode(HttpStatusCode.OK);
        }

        var existingAddress = await context.Addresses.FirstAsync(x => x.Id == user.Address.Id);
        existingAddress.HouseNumber = dto.HouseNumber;
        existingAddress.ApartmentNumber = dto.ApartmentNumber;
        existingAddress.City = dto.City;
        existingAddress.Country = dto.Country;
        existingAddress.FloorNumber = dto.FloorNumber;
        existingAddress.IntercomNumber = dto.IntercomNumber;
        existingAddress.Street = dto.Street;
        await context.SaveChangesAsync();

        return response.WithStatusCode(HttpStatusCode.OK);
    }
}
