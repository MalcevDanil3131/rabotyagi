namespace rabotyagi.DataAccess.Entities;

public class OrderPhoto : BaseEntity
{
    public Guid OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string OriginalName { get; set; } = "";
}
