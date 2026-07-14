using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CartService.DAL.Data;
using Shared.Models.DTOs;
using StackExchange.Redis;

namespace CartService.Controllers;

[ApiController]
public class HealthController : ControllerBase
{
    private readonly CartDbContext _dbContext;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<HealthController> _logger;

    public HealthController(
        CartDbContext dbContext,
        IConnectionMultiplexer redis,
        ILogger<HealthController> logger)
    {
        _dbContext = dbContext;
        _redis = redis;
        _logger = logger;
    }

    /// <summary>
    /// GET /health — Returns service health with dependency checks.
    /// Healthy: all deps respond within 2s
    /// Degraded: at least one 2-5s, none >5s or error
    /// Unhealthy: at least one >5s or error → HTTP 503
    /// </summary>
    [HttpGet("health")]
    [AllowAnonymous]
    public async Task<IActionResult> Health()
    {
        var dependencies = new List<DependencyHealth>();

        // Check PostgreSQL
        dependencies.Add(await CheckPostgresAsync());

        // Check Redis
        dependencies.Add(await CheckRedisAsync());

        // Check Inventory Service (local table query)
        dependencies.Add(await CheckInventoryServiceAsync());

        var overallStatus = DetermineOverallStatus(dependencies);
        var response = new HealthResponse(overallStatus, dependencies, DateTime.UtcNow);

        var statusCode = overallStatus == "Unhealthy"
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status200OK;

        return StatusCode(statusCode, response);
    }

    /// <summary>
    /// GET /metrics — Handled by prometheus-net middleware (MapMetrics).
    /// This endpoint is a placeholder if needed for custom metrics.
    /// </summary>
    [HttpGet("metrics")]
    [AllowAnonymous]
    public IActionResult Metrics()
    {
        // Prometheus metrics are served by the MapMetrics() middleware.
        // This action exists so the route is documented, but the middleware handles it.
        return Ok();
    }

    private async Task<DependencyHealth> CheckPostgresAsync()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _dbContext.Database.ExecuteSqlRawAsync("SELECT 1", cts.Token);
            sw.Stop();

            var status = ClassifyResponseTime(sw.ElapsedMilliseconds);
            return new DependencyHealth("PostgreSQL", status, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "PostgreSQL health check failed after {ElapsedMs}ms.", sw.ElapsedMilliseconds);
            return new DependencyHealth("PostgreSQL", "Unhealthy", sw.ElapsedMilliseconds);
        }
    }

    private async Task<DependencyHealth> CheckRedisAsync()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var db = _redis.GetDatabase();
            await db.PingAsync();
            sw.Stop();

            var status = ClassifyResponseTime(sw.ElapsedMilliseconds);
            return new DependencyHealth("Redis", status, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Redis health check failed after {ElapsedMs}ms.", sw.ElapsedMilliseconds);
            return new DependencyHealth("Redis", "Unhealthy", sw.ElapsedMilliseconds);
        }
    }

    private async Task<DependencyHealth> CheckInventoryServiceAsync()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            // For this PoC the inventory is a local table — verify it's queryable
            await _dbContext.InventoryItems
                .AsNoTracking()
                .Take(1)
                .FirstOrDefaultAsync(cts.Token);
            sw.Stop();

            var status = ClassifyResponseTime(sw.ElapsedMilliseconds);
            return new DependencyHealth("InventoryService", status, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Inventory Service health check failed after {ElapsedMs}ms.", sw.ElapsedMilliseconds);
            return new DependencyHealth("InventoryService", "Unhealthy", sw.ElapsedMilliseconds);
        }
    }

    private static string ClassifyResponseTime(long elapsedMs)
    {
        if (elapsedMs < 2000) return "Healthy";
        if (elapsedMs <= 5000) return "Degraded";
        return "Unhealthy";
    }

    private static string DetermineOverallStatus(List<DependencyHealth> dependencies)
    {
        if (dependencies.Any(d => d.Status == "Unhealthy"))
            return "Unhealthy";
        if (dependencies.Any(d => d.Status == "Degraded"))
            return "Degraded";
        return "Healthy";
    }
}
