using System.ComponentModel.DataAnnotations;

namespace rabotyagi.API.Features.Order.DTOs;

public class DecidePriceDto
{
    [Required]
    public bool? Accepted { get; set; }
    [StringLength(2000)]
    public string? Reason { get; set; }
    [Required]
    public DateTimeOffset? ExpectedUpdated { get; set; }
}
