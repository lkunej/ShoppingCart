namespace Shared.Models.Interfaces;

public record RateLimitResult(bool IsAllowed, int? RetryAfterSeconds = null);

public interface IRateLimiterService
{
    /// <summary>
    /// Checks the per-email rate limit (5 failed attempts per 15-minute sliding window).
    /// </summary>
    Task<RateLimitResult> CheckEmailRateLimit(string email);

    /// <summary>
    /// Checks the per-IP rate limit (100 requests per 1-minute sliding window).
    /// </summary>
    Task<RateLimitResult> CheckIpRateLimit(string ipAddress);

    /// <summary>
    /// Records a failed login attempt for the given email.
    /// </summary>
    Task RecordFailedAttempt(string email);
}
