using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AuthService.DAL.Data;

/// <summary>
/// Design-time factory for EF Core migrations tooling.
/// Uses a placeholder connection string; actual connection is configured at runtime in Program.cs.
/// </summary>
public class AuthDbContextFactory : IDesignTimeDbContextFactory<AuthDbContext>
{
    public AuthDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AuthDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Database=authdb;Username=postgres;Password=postgres;Minimum Pool Size=5;Maximum Pool Size=20;Connection Idle Lifetime=300");

        return new AuthDbContext(optionsBuilder.Options);
    }
}
