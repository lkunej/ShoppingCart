using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Shared.Infrastructure;
using StackExchange.Redis;

namespace CartService.Infrastructure;

/// <summary>
/// Interface for Redis cart cache operations used by Cart Service.
/// </summary>
public interface ICartRedisWrapper
{
    /// <summary>
    /// Gets the cached cart JSON for a given user ID.
    /// Returns null when Redis is unavailable.
    /// </summary>
    Task<string?> GetCartAsync(Guid userId);

    /// <summary>
    /// Stores cart JSON with the specified TTL.
    /// Logs a warning and continues when Redis is unavailable.
    /// </summary>
    Task SetCartAsync(Guid userId, string json, TimeSpan ttl);

    /// <summary>
    /// Deletes the cached cart for a given user ID.
    /// Logs a warning and continues when Redis is unavailable.
    /// </summary>
    Task DeleteCartAsync(Guid userId);

    /// <summary>
    /// Invalidates carts for a specific product ID using a reverse index.
    /// Uses the product_carts:{productId} set to find affected users efficiently.
    /// </summary>
    Task InvalidateCartsByProductIdAsync(Guid productId);

    /// <summary>
    /// Tracks that a user's cart contains the specified product.
    /// Maintains a reverse index for efficient cache invalidation on inventory updates.
    /// </summary>
    Task TrackProductInCartAsync(Guid userId, Guid productId, TimeSpan ttl);

    /// <summary>
    /// Removes product tracking for a user's cart (called when product is removed or cart is cleared).
    /// </summary>
    Task UntrackProductsInCartAsync(Guid userId, IEnumerable<Guid> productIds);

    /// <summary>
    /// Gets the cached guest cart JSON for a given guest session token.
    /// Returns null when Redis is unavailable or key doesn't exist.
    /// </summary>
    Task<string?> GetGuestCartAsync(Guid guestSessionToken);

    /// <summary>
    /// Stores guest cart JSON with the specified TTL.
    /// Logs a warning and continues when Redis is unavailable.
    /// </summary>
    Task SetGuestCartAsync(Guid guestSessionToken, string json, TimeSpan ttl);

    /// <summary>
    /// Deletes the cached guest cart for a given session token.
    /// Logs a warning and continues when Redis is unavailable.
    /// </summary>
    Task DeleteGuestCartAsync(Guid guestSessionToken);
}

/// <summary>
/// Wraps IConnectionMultiplexer with a Polly v8 circuit breaker for resilient Redis access.
/// Falls back gracefully when Redis is unavailable (circuit open or connection failure).
/// </summary>
public class CartRedisWrapper : ICartRedisWrapper
{
    private const string CartKeyPrefix = "cart:";
    private const string ProductCartsPrefix = "product_carts:";

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<CartRedisWrapper> _logger;
    private readonly ResiliencePipeline _resiliencePipeline;

    public CartRedisWrapper(
        IConnectionMultiplexer redis,
        ILogger<CartRedisWrapper> logger,
        CircuitBreakerConfig? config = null)
    {
        _redis = redis;
        _logger = logger;

        var cbConfig = config ?? new CircuitBreakerConfig();
        _resiliencePipeline = BuildResiliencePipeline(cbConfig);
    }

    /// <inheritdoc />
    public async Task<string?> GetCartAsync(Guid userId)
    {
        try
        {
            var result = await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                var db = _redis.GetDatabase();
                var value = await db.StringGetAsync($"{CartKeyPrefix}{userId}");
                return value.HasValue ? value.ToString() : null;
            });

            return result;
        }
        catch (BrokenCircuitException)
        {
            _logger.LogWarning(
                "Redis circuit breaker is open when getting cart for user {UserId}. Falling back to PostgreSQL.",
                userId);
            return null;
        }
        catch (TimeoutRejectedException)
        {
            _logger.LogWarning(
                "Redis call timed out when getting cart for user {UserId}. Falling back to PostgreSQL.",
                userId);
            return null;
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex,
                "Redis unavailable when getting cart for user {UserId}. Falling back to PostgreSQL.",
                userId);
            return null;
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex,
                "Redis timeout when getting cart for user {UserId}. Falling back to PostgreSQL.",
                userId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SetCartAsync(Guid userId, string json, TimeSpan ttl)
    {
        try
        {
            await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                var db = _redis.GetDatabase();
                await db.StringSetAsync($"{CartKeyPrefix}{userId}", json, ttl);
            });
        }
        catch (BrokenCircuitException)
        {
            _logger.LogWarning(
                "Redis circuit breaker is open when setting cart for user {UserId}. Cache will not be updated.",
                userId);
        }
        catch (TimeoutRejectedException)
        {
            _logger.LogWarning(
                "Redis call timed out when setting cart for user {UserId}. Cache will not be updated.",
                userId);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex,
                "Redis unavailable when setting cart for user {UserId}. Cache will not be updated.",
                userId);
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex,
                "Redis timeout when setting cart for user {UserId}. Cache will not be updated.",
                userId);
        }
    }

    /// <inheritdoc />
    public async Task DeleteCartAsync(Guid userId)
    {
        try
        {
            await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                var db = _redis.GetDatabase();
                await db.KeyDeleteAsync($"{CartKeyPrefix}{userId}");
            });
        }
        catch (BrokenCircuitException)
        {
            _logger.LogWarning(
                "Redis circuit breaker is open when deleting cart for user {UserId}. Cache entry may be stale.",
                userId);
        }
        catch (TimeoutRejectedException)
        {
            _logger.LogWarning(
                "Redis call timed out when deleting cart for user {UserId}. Cache entry may be stale.",
                userId);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex,
                "Redis unavailable when deleting cart for user {UserId}. Cache entry may be stale.",
                userId);
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex,
                "Redis timeout when deleting cart for user {UserId}. Cache entry may be stale.",
                userId);
        }
    }

    /// <inheritdoc />
    public async Task InvalidateCartsByProductIdAsync(Guid productId)
    {
        try
        {
            await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                var db = _redis.GetDatabase();
                var productSetKey = $"{ProductCartsPrefix}{productId}";

                // Get all user IDs whose carts contain this product (O(n) on set size, not keyspace)
                var members = await db.SetMembersAsync(productSetKey);
                if (members.Length == 0)
                {
                    return;
                }

                // Delete each affected cart cache entry
                var keysToDelete = members.Select(m => (RedisKey)$"{CartKeyPrefix}{m}").ToArray();
                if (keysToDelete.Length > 0)
                {
                    await db.KeyDeleteAsync(keysToDelete);
                }

                // Clear the reverse index for this product (carts will re-register on next write)
                await db.KeyDeleteAsync(productSetKey);

                _logger.LogInformation(
                    "Invalidated {Count} cart caches due to inventory update for product {ProductId}.",
                    members.Length, productId);
            });
        }
        catch (BrokenCircuitException)
        {
            _logger.LogWarning(
                "Redis circuit breaker is open during cart invalidation for product {ProductId}.",
                productId);
        }
        catch (TimeoutRejectedException)
        {
            _logger.LogWarning(
                "Redis call timed out during cart invalidation for product {ProductId}.",
                productId);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex,
                "Redis unavailable during cart invalidation for product {ProductId}.",
                productId);
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex,
                "Redis timeout during cart invalidation for product {ProductId}.",
                productId);
        }
    }

    /// <inheritdoc />
    public async Task TrackProductInCartAsync(Guid userId, Guid productId, TimeSpan ttl)
    {
        try
        {
            await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                var db = _redis.GetDatabase();
                var productSetKey = $"{ProductCartsPrefix}{productId}";
                await db.SetAddAsync(productSetKey, userId.ToString());
                await db.KeyExpireAsync(productSetKey, ttl);
            });
        }
        catch (BrokenCircuitException)
        {
            _logger.LogWarning(
                "Redis circuit breaker is open when tracking product {ProductId} for user {UserId}.",
                productId, userId);
        }
        catch (TimeoutRejectedException)
        {
            _logger.LogWarning(
                "Redis call timed out when tracking product {ProductId} for user {UserId}.",
                productId, userId);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex,
                "Redis unavailable when tracking product {ProductId} for user {UserId}.",
                productId, userId);
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex,
                "Redis timeout when tracking product {ProductId} for user {UserId}.",
                productId, userId);
        }
    }

    /// <inheritdoc />
    public async Task UntrackProductsInCartAsync(Guid userId, IEnumerable<Guid> productIds)
    {
        try
        {
            await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                var db = _redis.GetDatabase();
                foreach (var productId in productIds)
                {
                    var productSetKey = $"{ProductCartsPrefix}{productId}";
                    await db.SetRemoveAsync(productSetKey, userId.ToString());
                }
            });
        }
        catch (BrokenCircuitException)
        {
            _logger.LogWarning(
                "Redis circuit breaker is open when untracking products for user {UserId}.",
                userId);
        }
        catch (TimeoutRejectedException)
        {
            _logger.LogWarning(
                "Redis call timed out when untracking products for user {UserId}.",
                userId);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex,
                "Redis unavailable when untracking products for user {UserId}.",
                userId);
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex,
                "Redis timeout when untracking products for user {UserId}.",
                userId);
        }
    }

    private const string GuestCartKeyPrefix = "guest_cart:";

    /// <inheritdoc />
    public async Task<string?> GetGuestCartAsync(Guid guestSessionToken)
    {
        try
        {
            var result = await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                var db = _redis.GetDatabase();
                var value = await db.StringGetAsync($"{GuestCartKeyPrefix}{guestSessionToken}");
                return value.HasValue ? value.ToString() : null;
            });

            return result;
        }
        catch (BrokenCircuitException)
        {
            _logger.LogWarning(
                "Redis circuit breaker is open when getting guest cart for session {Token}. Falling back to PostgreSQL.",
                guestSessionToken);
            return null;
        }
        catch (TimeoutRejectedException)
        {
            _logger.LogWarning(
                "Redis call timed out when getting guest cart for session {Token}. Falling back to PostgreSQL.",
                guestSessionToken);
            return null;
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex,
                "Redis unavailable when getting guest cart for session {Token}. Falling back to PostgreSQL.",
                guestSessionToken);
            return null;
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex,
                "Redis timeout when getting guest cart for session {Token}. Falling back to PostgreSQL.",
                guestSessionToken);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SetGuestCartAsync(Guid guestSessionToken, string json, TimeSpan ttl)
    {
        try
        {
            await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                var db = _redis.GetDatabase();
                await db.StringSetAsync($"{GuestCartKeyPrefix}{guestSessionToken}", json, ttl);
            });
        }
        catch (BrokenCircuitException)
        {
            _logger.LogWarning(
                "Redis circuit breaker is open when setting guest cart for session {Token}. Cache will not be updated.",
                guestSessionToken);
        }
        catch (TimeoutRejectedException)
        {
            _logger.LogWarning(
                "Redis call timed out when setting guest cart for session {Token}. Cache will not be updated.",
                guestSessionToken);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex,
                "Redis unavailable when setting guest cart for session {Token}. Cache will not be updated.",
                guestSessionToken);
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex,
                "Redis timeout when setting guest cart for session {Token}. Cache will not be updated.",
                guestSessionToken);
        }
    }

    /// <inheritdoc />
    public async Task DeleteGuestCartAsync(Guid guestSessionToken)
    {
        try
        {
            await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                var db = _redis.GetDatabase();
                await db.KeyDeleteAsync($"{GuestCartKeyPrefix}{guestSessionToken}");
            });
        }
        catch (BrokenCircuitException)
        {
            _logger.LogWarning(
                "Redis circuit breaker is open when deleting guest cart for session {Token}. Cache entry may be stale.",
                guestSessionToken);
        }
        catch (TimeoutRejectedException)
        {
            _logger.LogWarning(
                "Redis call timed out when deleting guest cart for session {Token}. Cache entry may be stale.",
                guestSessionToken);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex,
                "Redis unavailable when deleting guest cart for session {Token}. Cache entry may be stale.",
                guestSessionToken);
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex,
                "Redis timeout when deleting guest cart for session {Token}. Cache entry may be stale.",
                guestSessionToken);
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
                    _logger.LogWarning("Redis call timed out after {Timeout}s.", config.CallTimeout.TotalSeconds);
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
