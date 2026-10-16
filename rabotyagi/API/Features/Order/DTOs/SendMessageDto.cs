using System.ComponentModel.DataAnnotations;

namespace rabotyagi.API.Features.Order.DTOs;

public class SendMessageDto
{
    [Required, StringLength(2000)]
    public string Text { get; set; } = "";
}
