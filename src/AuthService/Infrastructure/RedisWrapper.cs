using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Shared.Infrastructure;
using StackExchange.Redis;

namespace AuthService.Infrastructure;

/// <summary>
/// Interface for Redis operations used by Auth Service.
/// Provides token revocation checks, revocation writes, and token family management.
/// </summary>
public interface IRedisWrapper
{
    /// <summary>
    /// Checks if a token with the given jti has been revoked.
    /// Returns false (allowing signature+expiry validation) when Redis is unavailable.
    /// </summary>
    Task<bool> IsTokenRevokedAsync(string jti);

    /// <summary>
    /// Adds a token jti to the revocation list with a TTL matching the token's remaining lifetime.
    /// Logs a warning and continues when Redis is unavailable.
    /// </summary>
    Task RevokeTokenAsync(string jti, TimeSpan ttl);

    /// <summary>
    /// Stores a token family mapping (refreshJti → familyId) with the given TTL.
    /// </summary>
    Task StoreTokenFamilyAsync(string refreshJti, string familyId, TimeSpan ttl);

    /// <summary>
    /// Retrieves the family ID for a given refresh token jti.
    /// Returns null when Redis is unavailable.
    /// </summary>
    Task<string?> GetTokenFamilyAsync(string jti);

    /// <summary>
    /// Revokes all tokens in a token family by scanning for family members and adding them to the revocation list.
    /// </summary>
    Task RevokeTokenFamilyAsync(string jti, TimeSpan ttl);

    /// <summary>
    /// Checks if a key exists in Redis. Used for generic key existence checks.
    /// Returns false when Redis is unavailable.
    /// </summary>
    Task<bool> KeyExistsAsync(string key);

    /// <summary>
    /// Sets a string value with optional TTL. Used for generic Redis writes.
    /// </summary>
    Task SetStringAsync(string key, string value, TimeSpan? ttl = null);

    /// <summary>
    /// Gets a string value from Redis.
    /// Returns null when Redis is unavailable.
    /// </summary>
    Task<string?> GetStringAsync(string key);
}

/// <summary>
/// Wraps IConnectionMultiplexer with a Polly v8 circuit breaker for resilient Redis access.
/// Falls back gracefully when Redis is unavailable (circuit open or connection failure).
/// </summary>
public class RedisWrapper : IRedisWrapper
{
    private const string RevocationKeyPrefix = "revoked:";
    private const string TokenFamilyKeyPrefix = "token_family:";
    private const string TokenFamilyMembersPrefix = "token_family_members:";

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisWrapper> _logger;
    private readonly ResiliencePipeline _resiliencePipeline;

    public RedisWrapper(
        IConnectionMultiplexer redis,
        ILogger<RedisWrapper> logger,
        CircuitBreakerConfig? config = null)
    {
        _redis = redis;
        _logger = logger;

        var cbConfig = config ?? new CircuitBreakerConfig();
        _resiliencePipeline = BuildResiliencePipeline(cbConfig);
    }

    /// <inheritdoc />
    public async Task<bool> IsTokenRevokedAsync(string jti)
    {
        try
        {
            var result = await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                var db = _redis.GetDatabase();
                return await db.KeyExistsAsync($"{RevocationKeyPrefix}{jti}");
            });

            return result;
        }
        catch (BrokenCircuitException)
        {
            _logger.LogWarning(
                "Redis circuit breaker is open during revocation check for jti {Jti}. Falling back to signature+expiry validation.",
                jti);
            return false;
        }
        catch (TimeoutRejectedException)
        {
            _logger.LogWarning(
                "Redis call timed out during revocation check for jti {Jti}. Falling back to signature+expiry validation.",
                jti);
            return false;
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex,
                "Redis unavailable during revocation check for jti {Jti}. Falling back to signature+expiry validation.",
                jti);
            return false;
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex,
                "Redis timeout during revocation check for jti {Jti}. Falling back to signature+expiry validation.",
                jti);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task RevokeTokenAsync(string jti, TimeSpan ttl)
    {
        try
        {
            await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                var db = _redis.GetDatabase();
                await db.StringSetAsync($"{RevocationKeyPrefix}{jti}", "1", ttl);
            });
        }
        catch (BrokenCircuitException)
        {
            _logger.LogWarning(
                "Redis circuit breaker is open when revoking token {Jti}. Revocation will not be persisted to cache.",
                jti);
        }
        catch (TimeoutRejectedException)
        {
            _logger.LogWarning(
                "Redis call timed out when revoking token {Jti}. Revocation will not be persisted to cache.",
                jti);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex,
                "Redis unavailable when revoking token {Jti}. Revocation will not be persisted to cache.",
                jti);
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex,
                "Redis timeout when revoking token {Jti}. Revocation will not be persisted to cache.",
                jti);
        }
    }

    /// <inheritdoc />
    public async Task StoreTokenFamilyAsync(string refreshJti, string familyId, TimeSpan ttl)
    {
        try
        {
            await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                var db = _redis.GetDatabase();
                await db.StringSetAsync($"{TokenFamilyKeyPrefix}{refreshJti}", familyId, ttl);
            });
        }
        catch (BrokenCircuitException)
        {
            _logger.LogWarning(
                "Redis circuit breaker is open when storing token family for jti {Jti}.",
                refreshJti);
        }
        catch (TimeoutRejectedException)
        {
            _logger.LogWarning(
                "Redis call timed out when storing token family for jti {Jti}.",
                refreshJti);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex,
                "Redis unavailable when storing token family for jti {Jti}.",
                refreshJti);
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex,
                "Redis timeout when storing token family for jti {Jti}.",
                refreshJti);
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetTokenFamilyAsync(string jti)
    {
        try
        {
            var result = await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                var db = _redis.GetDatabase();
                var value = await db.StringGetAsync($"{TokenFamilyKeyPrefix}{jti}");
                return value.HasValue ? value.ToString() : null;
            });

            return result;
        }
        catch (BrokenCircuitException)
        {
            _logger.LogWarning(
                "Redis circuit breaker is open when getting token family for jti {Jti}.",
                jti);
            return null;
        }
        catch (TimeoutRejectedException)
        {
            _logger.LogWarning(
                "Redis call timed out when getting token family for jti {Jti}.",
                jti);
            return null;
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex,
                "Redis unavailable when getting token family for jti {Jti}.",
                jti);
            return null;
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex,
                "Redis timeout when getting token family for jti {Jti}.",
                jti);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task RevokeTokenFamilyAsync(string jti, TimeSpan ttl)
    {
        try
        {
            await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                var familyId = await GetTokenFamilyInternalAsync(jti);
                familyId ??= jti;

                var db = _redis.GetDatabase();
                var familySetKey = $"{TokenFamilyMembersPrefix}{familyId}";

                // Get all member JTIs from the family set (O(n) on family size, not keyspace)
                var members = await db.SetMembersAsync(familySetKey);
                if (members.Length > 0)
                {
                    foreach (var member in members)
                    {
                        var memberJti = member.ToString();
                        await db.StringSetAsync($"{RevocationKeyPrefix}{memberJti}", "1", ttl);
                    }
                }

                // Also revoke the triggering jti itself
                await db.StringSetAsync($"{RevocationKeyPrefix}{jti}", "1", ttl);

                _logger.LogWarning("Token family {FamilyId} revoked ({MemberCount} tokens) due to refresh token reuse.", familyId, members.Length);
            });
        }
        catch (BrokenCircuitException)
        {
            _logger.LogWarning(
                "Redis circuit breaker is open during family-wide revocation for jti {Jti}.",
                jti);
        }
        catch (TimeoutRejectedException)
        {
            _logger.LogWarning(
                "Redis call timed out during family-wide revocation for jti {Jti}.",
                jti);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex,
                "Redis unavailable during family-wide revocation for jti {Jti}.",
                jti);
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex,
                "Redis timeout during family-wide revocation for jti {Jti}.",
                jti);
        }
    }

    /// <inheritdoc />
    public async Task<bool> KeyExistsAsync(string key)
    {
        try
        {
            var result = await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                var db = _redis.GetDatabase();
                return await db.KeyExistsAsync(key);
            });

            return result;
        }
        catch (BrokenCircuitException)
        {
            _logger.LogWarning("Redis circuit breaker is open for key existence check: {Key}.", key);
            return false;
        }
        catch (TimeoutRejectedException)
        {
            _logger.LogWarning("Redis call timed out for key existence check: {Key}.", key);
            return false;
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex, "Redis unavailable for key existence check: {Key}.", key);
            return false;
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex, "Redis timeout for key existence check: {Key}.", key);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task SetStringAsync(string key, string value, TimeSpan? ttl = null)
    {
        try
        {
            await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                var db = _redis.GetDatabase();
                if (ttl.HasValue)
                {
                    await db.StringSetAsync(key, value, ttl.Value);
                }
                else
                {
                    await db.StringSetAsync(key, value);
                }
            });
        }
        catch (BrokenCircuitException)
        {
            _logger.LogWarning("Redis circuit breaker is open for set operation: {Key}.", key);
        }
        catch (TimeoutRejectedException)
        {
            _logger.LogWarning("Redis call timed out for set operation: {Key}.", key);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex, "Redis unavailable for set operation: {Key}.", key);
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex, "Redis timeout for set operation: {Key}.", key);
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetStringAsync(string key)
    {
        try
        {
            var result = await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                var db = _redis.GetDatabase();
                var value = await db.StringGetAsync(key);
                return value.HasValue ? value.ToString() : null;
            });

            return result;
        }
        catch (BrokenCircuitException)
        {
            _logger.LogWarning("Redis circuit breaker is open for get operation: {Key}.", key);
            return null;
        }
        catch (TimeoutRejectedException)
        {
            _logger.LogWarning("Redis call timed out for get operation: {Key}.", key);
            return null;
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex, "Redis unavailable for get operation: {Key}.", key);
            return null;
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex, "Redis timeout for get operation: {Key}.", key);
            return null;
        }
    }

    /// <summary>
    /// Internal method to get token family without going through the resilience pipeline again
    /// (used within RevokeTokenFamilyAsync which already wraps in the pipeline).
    /// </summary>
    private async Task<string?> GetTokenFamilyInternalAsync(string jti)
    {
        var db = _redis.GetDatabase();
        var value = await db.StringGetAsync($"{TokenFamilyKeyPrefix}{jti}");
        return value.HasValue ? value.ToString() : null;
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
                    _logger.LogWarning("Redis call timed out after {Timeout}s.", config.CallTimeout.TotalSeconds);
                    return default;
                }
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 1.0, // Use failure count logic via SamplingDuration
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
                        "Redis circuit breaker OPENED. Break duration: {BreakDuration}s.",
                        config.ResetTimeout.TotalSeconds);
                    return default;
                },
                OnClosed = args =>
                {
                    _logger.LogInformation("Redis circuit breaker CLOSED. Normal operations resumed.");
                    return default;
                },
                OnHalfOpened = args =>
                {
                    _logger.LogInformation("Redis circuit breaker HALF-OPEN. Allowing probe requests.");
                    return default;
                }
            })
            .Build();
    }
}
