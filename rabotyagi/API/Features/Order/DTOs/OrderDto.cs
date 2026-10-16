using rabotyagi.DataAccess.Enums;

namespace rabotyagi.API.Features.Order.DTOs;

public class OrderDto
{
    public Guid Id { get; set; }
    public Guid InitiatorId { get; set; }
    public Guid? ExecutorId { get; set; }
    public EOrderType OrderType { get; set; }
    public EOrderStatus Status { get; set; }
    public string Comment { get; set; }
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Updated { get; set; }
    public string Title { get; set; } = "";
    public string AddressText { get; set; } = "";
    public DateTimeOffset? ScheduledAt { get; set; }
    public string InitiatorName { get; set; } = "";
    public string? InitiatorPhone { get; set; }
    public string? ExecutorName { get; set; }
    public string? ExecutorPhone { get; set; }
    public string? ReportSummary { get; set; }
    public bool WorkDone { get; set; }
    public bool WorkplaceClean { get; set; }
    public bool ClientInformed { get; set; }
    public DateTimeOffset? ReportSubmittedAt { get; set; }
    public string? ReviewFeedback { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public List<OrderPhotoDto> Photos { get; set; } = [];
}
