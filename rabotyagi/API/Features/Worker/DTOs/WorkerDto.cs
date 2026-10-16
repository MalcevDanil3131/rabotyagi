namespace rabotyagi.API.Features.Worker.DTOs;

public class WorkerDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? PhoneNumber { get; set; }
    public DateTimeOffset? AvailableUntil { get; set; }
    public int ActiveOrders { get; set; }
}
