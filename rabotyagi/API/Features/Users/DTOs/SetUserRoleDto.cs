using System.ComponentModel.DataAnnotations;

namespace rabotyagi.API.Features.Users.DTOs;

public class SetUserRoleDto
{
    [Required]
    public string Role { get; set; } = "";
}
