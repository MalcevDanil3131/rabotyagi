using System.ComponentModel.DataAnnotations;

namespace rabotyagi.API.Features.Order.DTOs;

public class ReviewReportDto
{
    [Required]
    public bool? Approved { get; set; }
    [Required]
    public DateTimeOffset? ExpectedUpdated { get; set; }
    [StringLength(2000)]
    public string? Feedback { get; set; }
}
