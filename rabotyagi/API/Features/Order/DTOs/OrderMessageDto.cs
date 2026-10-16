namespace rabotyagi.API.Features.Order.DTOs;

public class OrderMessageDto
{
    public Guid Id { get; set; }
    public Guid SenderId { get; set; }
    public string SenderName { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset Created { get; set; }
}
