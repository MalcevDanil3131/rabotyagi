namespace rabotyagi.API.Features.Account.DTOs;

public class AddressItemDto
{
    public Guid? Id { get; set; }
    /// <summary> Страна </summary>
    public string? Country { get; set; } = null!;
    /// <summary> Город </summary>
    public string? City { get; set; } = null!;
    /// <summary> Улица </summary>
    public string? Street { get; set; } = null!;
    /// <summary> Номер дома </summary>
    public string? HouseNumber { get; set; } = null!;
    /// <summary> Номер квартиры/офиса </summary>
    public string? ApartmentNumber { get; set; }
    /// <summary> Этаж </summary>
    public int? FloorNumber { get; set; }
    /// <summary> Номер домофона </summary>
    public string? IntercomNumber { get; set; }
}