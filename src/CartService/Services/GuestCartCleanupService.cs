using CartService.DAL.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CartService.Services;

/// <summary>
/// Background service that periodically cleans up expired guest carts.
/// Identifies guest carts not updated in the last 10 days and deletes them
/// in batches of up to 100 per execution cycle.
/// </summary>
public class GuestCartCleanupService : BackgroundService
{
    private const string GuestCartKeyPrefix = "guest_cart:";
    private const int BatchSize = 100;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan ExpiryThreshold = TimeSpan.FromDays(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<GuestCartCleanupService> _logger;

    public GuestCartCleanupService(
        IServiceScopeFactory scopeFactory,
        IConnectionMultiplexer redis,
        ILogger<GuestCartCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _redis = redis;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("GuestCartCleanupService starting. Cleanup interval: {Interval} minutes, expiry threshold: {Threshold} days.",
            CleanupInterval.TotalMinutes, ExpiryThreshold.TotalDays);

        using var timer = new PeriodicTimer(CleanupInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CleanupExpiredGuestCartsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during guest cart cleanup cycle. Will retry on next cycle.");
            }
        }

        _logger.LogInformation("GuestCartCleanupService stopping.");
    }

    private async Task CleanupExpiredGuestCartsAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CartDbContext>();

        var cutoff = DateTime.UtcNow - ExpiryThreshold;

        var expiredCarts = await dbContext.Carts
            .Where(c => c.GuestSessionToken != null && c.UpdatedAt < cutoff)
            .OrderBy(c => c.UpdatedAt)
            .Take(BatchSize)
            .Include(c => c.Items)
            .AsSplitQuery()
            .ToListAsync(stoppingToken);

        if (expiredCarts.Count == 0)
        {
            _logger.LogDebug("No expired guest carts found during cleanup cycle.");
            return;
        }

        _logger.LogInformation("Found {Count} expired guest carts to clean up.", expiredCarts.Count);

        if (stoppingToken.IsCancellationRequested) return;

        // Batch all removals into a single SaveChangesAsync call
        foreach (var cart in expiredCarts)
        {
            dbContext.CartItems.RemoveRange(cart.Items);
            dbContext.Carts.Remove(cart);
        }

        try
        {
            await dbContext.SaveChangesAsync(stoppingToken);

            _logger.LogInformation("Guest cart cleanup cycle complete. Deleted {Count} expired carts.",
                expiredCarts.Count);

            // Best-effort Redis cache invalidation for all deleted carts
            foreach (var cart in expiredCarts)
            {
                await TryInvalidateRedisCache(cart.GuestSessionToken!.Value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to delete batch of {Count} expired guest carts. Will retry on next cycle.",
                expiredCarts.Count);
        }
    }

    private async Task TryInvalidateRedisCache(Guid guestSessionToken)
    {
        try
        {
            var db = _redis.GetDatabase();
            await db.KeyDeleteAsync($"{GuestCartKeyPrefix}{guestSessionToken}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to invalidate Redis cache for expired guest cart session {SessionToken}. Redis TTL will handle eviction.",
                guestSessionToken);
        }
    }
}
