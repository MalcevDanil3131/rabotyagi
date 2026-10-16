using System.ComponentModel.DataAnnotations;
using rabotyagi.DataAccess.Enums;

namespace rabotyagi.API.Features.Order.DTOs;

public class PlaceOrderDto
{
    public Guid InitiatorId { get; set; }
    public EOrderType OrderType { get; set; }
    [Required, StringLength(2000, MinimumLength = 10)]
    public string Comment { get; set; }
    [StringLength(100)]
    public string Title { get; set; } = "";
    [Required, StringLength(300)]
    public string AddressText { get; set; } = "";
    public DateTimeOffset? ScheduledAt { get; set; }
}
