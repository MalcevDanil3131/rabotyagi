using System.ComponentModel.DataAnnotations;
namespace rabotyagi.API.Features.Account.DTOs;
public class LogInDto
{
    [Required, EmailAddress, StringLength(254)] public string Email { get; set; } = "";
    [Required, StringLength(1024)] public string Password { get; set; } = "";
    public bool IsPersistent { get; set; }
}
