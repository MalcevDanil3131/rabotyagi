namespace rabotyagi.API.Features.Account.DTOs;

public class LogInDto
{
    public string Email { get; set; }
    public string Password { get; set; }
    public bool IsPersistent { get; set; }
}