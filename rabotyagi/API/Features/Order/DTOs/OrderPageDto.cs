using System.ComponentModel.DataAnnotations;
using rabotyagi.DataAccess.Enums;

namespace rabotyagi.API.Features.Order.DTOs;

public class OrderPageQuery
{
    [Range(1, 100000)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 20;
    public EOrderStatus? Status { get; set; }
    [StringLength(200)] public string? Search { get; set; }
    public string Scope { get; set; } = "all";
    public bool Quality { get; set; }
}

public record OrderListStats(int Total, int Active, int Review, int Completed);
public record OrderPageDto(List<OrderDto> Items, int Page, int PageSize, int TotalCount, OrderListStats Stats,
    List<OrderQueueCountDto> StatusCounts);
