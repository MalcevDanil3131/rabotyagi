namespace rabotyagi.API.Features.Account.DTOs;

public class RegisterUserDto
{
    public string Name { get; set; }
    public string Surname { get; set; }
    public string PhoneNumber { get; set; }
    public string Email { get; set; }
    public string Password { get; set; }
    public bool IsPersistent { get; set; }
}