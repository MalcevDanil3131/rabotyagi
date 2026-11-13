using System.ComponentModel.DataAnnotations;
namespace rabotyagi.API.Features.Account.DTOs;
public class ChangePasswordDto
{
    [Required, StringLength(1024)] public string OldPassword { get; set; } = "";
    [Required, StringLength(128, MinimumLength = 10)] public string NewPassword { get; set; } = "";
}
