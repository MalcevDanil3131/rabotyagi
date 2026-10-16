namespace rabotyagi.DataAccess.Entities;

public class OrderMessage : BaseEntity
{
    public Guid OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public Guid SenderId { get; set; }
    public string SenderName { get; set; } = "";
    public string Text { get; set; } = "";
}
