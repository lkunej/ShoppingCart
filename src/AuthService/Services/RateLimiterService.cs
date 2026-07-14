using Shared.Models.Interfaces;

namespace AuthService.Services;

/// <summary>
/// Stub implementation of IRateLimiterService that always allows requests.
/// The real sliding-window implementation backed by Redis is task 2.5.
/// This stub ensures DI compiles and the system functions without rate limiting.
/// </summary>
public class RateLimiterService : IRateLimiterService
{
    /// <inheritdoc />
    public Task<RateLimitResult> CheckEmailRateLimit(string email)
    {
        return Task.FromResult(new RateLimitResult(true));
    }

    /// <inheritdoc />
    public Task<RateLimitResult> CheckIpRateLimit(string ipAddress)
    {
        return Task.FromResult(new RateLimitResult(true));
    }

    /// <inheritdoc />
    public Task RecordFailedAttempt(string email)
    {
        return Task.CompletedTask;
    }
}
