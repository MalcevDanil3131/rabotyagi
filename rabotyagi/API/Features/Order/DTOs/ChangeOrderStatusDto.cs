using rabotyagi.DataAccess.Enums;

namespace rabotyagi.API.Features.Order.DTOs;

public class ChangeOrderStatusDto
{
    public Guid OrderId { get; set; }
    public EOrderStatus Status { get; set; }
}
