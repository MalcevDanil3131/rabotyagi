using System.ComponentModel.DataAnnotations;

namespace rabotyagi.API.Features.Worker.DTOs;

public class SetAvailabilityDto
{
    [Range(0, 12)]
    public int Hours { get; set; }
}
