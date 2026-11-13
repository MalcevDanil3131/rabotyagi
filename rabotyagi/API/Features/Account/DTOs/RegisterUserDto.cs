using System.ComponentModel.DataAnnotations;
namespace rabotyagi.API.Features.Account.DTOs;
public class RegisterUserDto
{
    [Required, StringLength(100)] public string Name { get; set; } = "";
    [Required, StringLength(100)] public string Surname { get; set; } = "";
    [Required, StringLength(32)] public string PhoneNumber { get; set; } = "";
    [Required, EmailAddress, StringLength(254)] public string Email { get; set; } = "";
    [Required, StringLength(128, MinimumLength = 10)] public string Password { get; set; } = "";
    public bool IsPersistent { get; set; }
}
