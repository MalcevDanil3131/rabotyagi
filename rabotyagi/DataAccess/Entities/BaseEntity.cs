using System.ComponentModel.DataAnnotations;

namespace rabotyagi.DataAccess.Entities;

public class BaseEntity
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
}