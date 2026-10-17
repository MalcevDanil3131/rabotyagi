using rabotyagi.DataAccess.Enums;

namespace rabotyagi.API.Features.Order.DTOs;

public class OrderHistoryEventDto
{
    public Guid Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public EOrderHistoryEventType EventType { get; set; }
    public Guid ActorUserId { get; set; }
    public string ActorNameSnapshot { get; set; } = "";
    public EOrderStatus? PreviousStatus { get; set; }
    public EOrderStatus NewStatus { get; set; }
    public Guid? ExecutorId { get; set; }
    public string? ExecutorNameSnapshot { get; set; }
    public string? Comment { get; set; }
}
