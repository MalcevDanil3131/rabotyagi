namespace rabotyagi.API.Features.Account.DTOs;

public class AccountInfoDto
{
    public Guid Id { get; set; }
    public string Email { get; set; }
    public string Name { get; set; }
    public string Surname { get; set; }
    public string PhoneNumber { get; set; }
    public AddressItemDto? Address { get; set; }
    public string RoleName { get; set; }
    public IList<string> Roles { get; set; } = [];
    public DateTimeOffset? AvailableUntil { get; set; }
}
