using System.ComponentModel.DataAnnotations;
using rabotyagi.DataAccess.Enums;

namespace rabotyagi.API.Features.Order.DTOs;

public class ChangeOrderDto
{
    public Guid OrderId { get; set; }
    public EOrderType OrderType { get; set; }
    [Required, StringLength(2000, MinimumLength = 10)]
    public string Comment { get; set; }
}
