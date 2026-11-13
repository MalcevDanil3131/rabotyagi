using System.ComponentModel.DataAnnotations;

namespace rabotyagi.API.Features.Order.DTOs;

public class ProposePriceDto
{
    public decimal Amount { get; set; }
    [Required, StringLength(2000)]
    public string Scope { get; set; } = "";
    [Required]
    public DateTimeOffset? ExpectedUpdated { get; set; }
}
