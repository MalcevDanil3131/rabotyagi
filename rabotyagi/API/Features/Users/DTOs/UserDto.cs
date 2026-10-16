namespace rabotyagi.API.Features.Users.DTOs;

public class UserDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }
    public List<string> Roles { get; set; } = [];
    public bool Blocked { get; set; }
}
