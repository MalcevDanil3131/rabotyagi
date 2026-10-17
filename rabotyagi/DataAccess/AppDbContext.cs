using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using rabotyagi.DataAccess.Entities;

namespace rabotyagi.DataAccess;

public class AppDbContext : IdentityDbContext<User, IdentityRole<Guid>, Guid>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Address> Addresses
    {
        get; set;
    }
    public DbSet<Order> Orders
    {
        get; set;
    }
    public DbSet<OrderPhoto> OrderPhotos
    {
        get; set;
    }
    public DbSet<OrderMessage> OrderMessages
    {
        get; set;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Order>().Property(x => x.Updated).IsConcurrencyToken();
        modelBuilder.Entity<Order>().HasIndex(x => x.Status);
        modelBuilder.Entity<OrderMessage>().HasIndex(x => new { x.OrderId, x.Created });
        modelBuilder.Entity<OrderHistoryEvent>().HasIndex(x => new { x.OrderId, x.OccurredAt, x.Id });
        modelBuilder.Entity<OrderHistoryEvent>().HasOne(x => x.Order).WithMany()
            .HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<Enum>().HaveConversion<string>();
    }

    public DbSet<OrderHistoryEvent> OrderHistoryEvents { get; set; }
}
