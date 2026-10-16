using System.ComponentModel.DataAnnotations;

namespace rabotyagi.API.Features.Order.DTOs;

public class ReviewReportDto
{
    public bool Approved { get; set; }
    [StringLength(2000)]
    public string? Feedback { get; set; }
}
