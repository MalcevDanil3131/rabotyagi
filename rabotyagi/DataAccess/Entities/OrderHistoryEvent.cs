using rabotyagi.DataAccess.Enums;

namespace rabotyagi.DataAccess.Entities;

public class OrderHistoryEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public EOrderHistoryEventType EventType { get; set; }
    // Historical identity: deliberately no FK to a mutable/deletable account.
    public Guid ActorUserId { get; set; }
    public string ActorNameSnapshot { get; set; } = "";
    public EOrderStatus? PreviousStatus { get; set; }
    public EOrderStatus NewStatus { get; set; }
    public Guid? ExecutorId { get; set; }
    public string? ExecutorNameSnapshot { get; set; }
    public string? Comment { get; set; }
}
