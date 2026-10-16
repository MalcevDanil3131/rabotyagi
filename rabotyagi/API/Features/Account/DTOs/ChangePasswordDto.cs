namespace rabotyagi.API.Features.Account.DTOs;

public class ChangePasswordDto
{
    public string OldPassword { get; set; }
    public string NewPassword { get; set; }
}