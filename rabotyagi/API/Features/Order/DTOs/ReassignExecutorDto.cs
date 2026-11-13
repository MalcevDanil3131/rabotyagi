using System.ComponentModel.DataAnnotations;

namespace rabotyagi.API.Features.Order.DTOs;

public class ReassignExecutorDto
{
    public Guid ExecutorId { get; set; }
    public Guid ExpectedExecutorId { get; set; }
    [Required] public DateTimeOffset? ExpectedUpdated { get; set; }
    [Required, StringLength(2000)] public string Reason { get; set; } = "";
}
