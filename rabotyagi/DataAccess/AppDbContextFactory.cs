using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace rabotyagi.DataAccess;

// Keeps EF tooling out of Program.cs, which applies migrations on application startup.
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=rabotyagi_design_only;Username=design_only")
            .Options);
}
