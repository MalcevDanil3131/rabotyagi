using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using rabotyagi.API.Features.Address.DTOs;
using AddressItemDto = rabotyagi.API.Features.Account.DTOs.AddressItemDto;

namespace rabotyagi.API.Features.Address;

[Route("api/address")]
[Authorize]
[ApiController]
public class AddressApiController(AddressService service) : ControllerBase
{
    /// <summary> Получить все адреса </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<AddressItemDto>), 200)]
    public async Task<ActionResult> GetAddress() =>
        await service.GetAddress();

    /// <summary> Сохранить новый адрес </summary>
    [HttpPost]
    [ProducesResponseType(200)]
    public async Task<ActionResult> SaveAddress([FromBody] SaveAddressDto itemDto) =>
        await service.SaveAddress(itemDto);
}