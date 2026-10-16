using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace rabotyagi.DataAccess.Entities;

public class User : IdentityUser<Guid>
{
    public string Name { get; set; }
    public string Surname { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AvailableUntil { get; set; }
    public Address? Address { get; set; }
    public List<Order> InitiatedOrders { get; set; } = [];
    public List<Order> ExecutedOrders { get; set; } = [];

    public Guid? RoleId { get; set; }

    [ForeignKey(nameof(RoleId))]
    [DeleteBehavior(DeleteBehavior.SetNull)]
    public IdentityRole<Guid>? Role { get; set; }
}
