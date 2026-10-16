using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using rabotyagi.DataAccess.Enums;

namespace rabotyagi.DataAccess.Entities;

public class Order : BaseEntity
{
    public Guid InitiatorId { get; set; }
    public Guid? ExecutorId { get; set; }
    [InverseProperty(nameof(User.InitiatedOrders))]
    [DeleteBehavior((DeleteBehavior.Cascade))]
    public User Initiator { get; set; } = null!;

    [InverseProperty(nameof(User.ExecutedOrders))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public User? Executor { get; set; }

    public EOrderType OrderType { get; set; }
    public string Comment { get; set; }
    public EOrderStatus Status { get; set; } = EOrderStatus.Created;

    public Guid AddressId { get; set; }
    [ForeignKey(nameof(AddressId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public Address Address { get; set; } = null!;
    public string Title { get; set; } = "";
    public string AddressText { get; set; } = "";
    public DateTimeOffset? ScheduledAt { get; set; }
    public string? ReportSummary { get; set; }
    public bool WorkDone { get; set; }
    public bool WorkplaceClean { get; set; }
    public bool ClientInformed { get; set; }
    public DateTimeOffset? ReportSubmittedAt { get; set; }
    public string? ReviewFeedback { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public Guid? ReviewerId { get; set; }
    public List<OrderPhoto> Photos { get; set; } = [];
    public List<OrderMessage> Messages { get; set; } = [];
}
