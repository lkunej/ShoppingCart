using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CartService.DAL.Data;

/// <summary>
/// Design-time factory for EF Core migrations tooling.
/// Uses a placeholder connection string; actual connection is configured at runtime in Program.cs.
/// </summary>
public class CartDbContextFactory : IDesignTimeDbContextFactory<CartDbContext>
{
    public CartDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<CartDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Database=cartdb;Username=postgres;Password=postgres;Minimum Pool Size=5;Maximum Pool Size=20;Connection Idle Lifetime=300");

        return new CartDbContext(optionsBuilder.Options);
    }
}
