using System.ComponentModel.DataAnnotations;

namespace rabotyagi.API.Features.Order.DTOs;

public class SubmitReportDto
{
    [Required, StringLength(2000, MinimumLength = 10)]
    public string Summary { get; set; } = "";
    public bool WorkDone { get; set; }
    public bool WorkplaceClean { get; set; }
    public bool ClientInformed { get; set; }
    public List<IFormFile> Photos { get; set; } = [];
    public List<Guid> KeepPhotoIds { get; set; } = [];
}
