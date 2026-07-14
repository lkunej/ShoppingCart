using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Shared.Infrastructure;
using StackExchange.Redis;

namespace CartService.Infrastructure;

/// <summary>
/// Result of a guest rate limit check.
/// </summary>
public record GuestRateLimitResult(bool IsAllowed, int? RetryAfterSeconds = null);

/// <summary>
/// Interface for guest session creation rate limiting using Redis sliding window.
/// </summary>
public interface IGuestRateLimiterService
{
    /// <summary>
    /// Checks whether the given IP address is allowed to create a new guest session.
    /// Returns IsAllowed=true if under the limit, or IsAllowed=false with RetryAfterSeconds if rate limited.
    /// When Redis is unavailable, fails open (allows the request).
    /// </summary>
    Task<GuestRateLimitResult> CheckSessionCreationLimit(string ipAddress);
}

/// <summary>
/// Implements guest session creation rate limiting using Redis sorted sets with a sliding window.
/// Max 10 sessions per IP per 60-minute window.
/// Falls back to allowing requests when Redis is unavailable (fail-open).
/// </summary>
public class GuestRateLimiterService : IGuestRateLimiterService
{
    private const string RateKeyPrefix = "guest_rate:";
    private const int MaxSessionsPerWindow = 10;
    private const int WindowSeconds = 3600; // 60 minutes

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<GuestRateLimiterService> _logger;
    private readonly ResiliencePipeline _resiliencePipeline;

    public GuestRateLimiterService(
        IConnectionMultiplexer redis,
        ILogger<GuestRateLimiterService> logger,
        CircuitBreakerConfig? config = null)
    {
        _redis = redis;
        _logger = logger;

        var cbConfig = config ?? new CircuitBreakerConfig();
        _resiliencePipeline = BuildResiliencePipeline(cbConfig);
    }

    /// <inheritdoc />
    public async Task<GuestRateLimitResult> CheckSessionCreationLimit(string ipAddress)
    {
        try
        {
            var result = await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                var db = _redis.GetDatabase();
                var key = $"{RateKeyPrefix}{ipAddress}";
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var windowStart = now - WindowSeconds;

                // 1. Remove expired entries outside the sliding window
                await db.SortedSetRemoveRangeByScoreAsync(key, double.NegativeInfinity, windowStart);

                // 2. Count remaining entries in the window
                var count = await db.SortedSetLengthAsync(key);

                if (count >= MaxSessionsPerWindow)
                {
                    // 3. Rate limited — calculate RetryAfterSeconds from the oldest entry
                    var oldestEntries = await db.SortedSetRangeByScoreAsync(key, order: Order.Ascending, take: 1);
                    int retryAfter = WindowSeconds; // fallback

                    if (oldestEntries.Length > 0 && oldestEntries[0].TryParse(out long oldestTimestamp))
                    {
                        // The oldest entry will expire at oldestTimestamp + WindowSeconds
                        var expiresAt = oldestTimestamp + WindowSeconds;
                        retryAfter = (int)(expiresAt - now);
                        if (retryAfter <= 0) retryAfter = 1;
                    }

                    return new GuestRateLimitResult(IsAllowed: false, RetryAfterSeconds: retryAfter);
                }

                // 4. Allowed — record this session creation
                await db.SortedSetAddAsync(key, now.ToString(), now);
                await db.KeyExpireAsync(key, TimeSpan.FromSeconds(WindowSeconds));

                return new GuestRateLimitResult(IsAllowed: true);
            });

            return result;
        }
        catch (BrokenCircuitException)
        {
            _logger.LogWarning(
                "Redis circuit breaker is open when checking rate limit for IP {IpAddress}. Allowing request (fail-open).",
                ipAddress);
            return new GuestRateLimitResult(IsAllowed: true);
        }
        catch (TimeoutRejectedException)
        {
            _logger.LogWarning(
                "Redis call timed out when checking rate limit for IP {IpAddress}. Allowing request (fail-open).",
                ipAddress);
            return new GuestRateLimitResult(IsAllowed: true);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex,
                "Redis unavailable when checking rate limit for IP {IpAddress}. Allowing request (fail-open).",
                ipAddress);
            return new GuestRateLimitResult(IsAllowed: true);
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex,
                "Redis timeout when checking rate limit for IP {IpAddress}. Allowing request (fail-open).",
                ipAddress);
            return new GuestRateLimitResult(IsAllowed: true);
        }
    }

    /// <summary>
    /// Builds the Polly v8 resilience pipeline with circuit breaker and timeout.
    /// </summary>
    private ResiliencePipeline BuildResiliencePipeline(CircuitBreakerConfig config)
    {
        return new ResiliencePipelineBuilder()
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = config.CallTimeout,
                OnTimeout = args =>
                {
                    _logger.LogWarning("Redis rate limiter call timed out after {Timeout}s.", config.CallTimeout.TotalSeconds);
                    return default;
                }
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 1.0,
                SamplingDuration = config.RollingWindow,
                MinimumThroughput = config.FailureThreshold,
                BreakDuration = config.ResetTimeout,
                ShouldHandle = new PredicateBuilder()
                    .Handle<RedisConnectionException>()
                    .Handle<RedisTimeoutException>()
                    .Handle<TimeoutRejectedException>(),
                OnOpened = args =>
                {
                    _logger.LogWarning(
                        "Redis rate limiter circuit breaker OPENED. Break duration: {BreakDuration}s.",
                        config.ResetTimeout.TotalSeconds);
                    return default;
                },
                OnClosed = args =>
                {
                    _logger.LogInformation("Redis rate limiter circuit breaker CLOSED. Normal operations resumed.");
                    return default;
                },
                OnHalfOpened = args =>
                {
                    _logger.LogInformation("Redis rate limiter circuit breaker HALF-OPEN. Allowing probe requests.");
                    return default;
                }
            })
            .Build();
    }
}
