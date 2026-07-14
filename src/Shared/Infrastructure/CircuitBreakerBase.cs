namespace Shared.Infrastructure;

/// <summary>
/// Configuration for circuit breaker behavior.
/// Used by Polly CircuitBreakerPolicy in both Auth and Cart services.
/// </summary>
public class CircuitBreakerConfig
{
    /// <summary>
    /// Number of failures in the rolling window before the circuit opens.
    /// </summary>
    public int FailureThreshold { get; set; } = 5;

    /// <summary>
    /// Duration of the rolling failure window.
    /// </summary>
    public TimeSpan RollingWindow { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Duration the circuit remains open before transitioning to half-open.
    /// </summary>
    public TimeSpan ResetTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum number of probe requests allowed in half-open state.
    /// </summary>
    public int HalfOpenMaxCalls { get; set; } = 3;

    /// <summary>
    /// Timeout for individual calls; exceeding this is treated as a failure.
    /// </summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
