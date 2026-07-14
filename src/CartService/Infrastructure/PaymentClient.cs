using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Shared.Models.Exceptions;
using Shared.Models.Interfaces;

namespace CartService.Infrastructure;

/// <summary>
/// HTTP-based payment client that integrates with an external payment gateway.
/// 
/// Resilience features:
/// - Circuit breaker (opens after 5 failures in 60s, resets after 30s)
/// - Retry with exponential backoff (3 attempts)
/// - Request timeout (5s per call)
/// 
/// In this PoC the actual HTTP calls are simulated to demonstrate the pattern.
/// In production, replace SimulatePaymentCall with real HTTP requests.
/// </summary>
public class PaymentClient : IPaymentClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<PaymentClient> _logger;
    private readonly PaymentServiceOptions _options;
    private readonly ResiliencePipeline _resiliencePipeline;

    public PaymentClient(
        HttpClient httpClient,
        ILogger<PaymentClient> logger,
        IOptions<PaymentServiceOptions> options)
    {
        _httpClient = httpClient;
        _logger = logger;
        _options = options.Value;

        _resiliencePipeline = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(60),
                MinimumThroughput = 5,
                BreakDuration = TimeSpan.FromSeconds(30),
                OnOpened = args =>
                {
                    _logger.LogWarning("Payment service circuit breaker OPENED. Requests will be rejected for {Duration}s.",
                        args.BreakDuration.TotalSeconds);
                    return ValueTask.CompletedTask;
                },
                OnClosed = args =>
                {
                    _logger.LogInformation("Payment service circuit breaker CLOSED. Normal operation resumed.");
                    return ValueTask.CompletedTask;
                }
            })
            .AddRetry(new Polly.Retry.RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(500),
                BackoffType = DelayBackoffType.Exponential,
                ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>().Handle<TaskCanceledException>(),
                OnRetry = args =>
                {
                    _logger.LogWarning("Payment service retry attempt {Attempt} after {Delay}ms.",
                        args.AttemptNumber, args.RetryDelay.TotalMilliseconds);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(TimeSpan.FromSeconds(5))
            .Build();
    }

    public async Task<PaymentResult> AuthorizePayment(PaymentRequest request)
    {
        _logger.LogInformation(
            "Authorizing payment for order {OrderId}, amount {Amount} {Currency}, method {Method}.",
            request.OrderId, request.AmountCents, request.Currency, request.PaymentMethod);

        try
        {
            var result = await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                // In production: POST to _options.BaseUrl + "/v1/payments/authorize"
                return await SimulatePaymentCall("authorize", request, ct);
            });

            _logger.LogInformation(
                "Payment authorized. TransactionId: {TransactionId}, Status: {Status}.",
                result.TransactionId, result.Status);

            return result;
        }
        catch (BrokenCircuitException)
        {
            _logger.LogError("Payment service circuit breaker is open. Cannot authorize payment for order {OrderId}.", request.OrderId);
            throw new ServiceUnavailableException("PaymentService");
        }
    }

    public async Task<PaymentResult> CapturePayment(string transactionId)
    {
        _logger.LogInformation("Capturing payment for transaction {TransactionId}.", transactionId);

        try
        {
            var result = await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                // In production: POST to _options.BaseUrl + $"/v1/payments/{transactionId}/capture"
                return await SimulateCaptureCall(transactionId, ct);
            });

            _logger.LogInformation(
                "Payment captured. TransactionId: {TransactionId}, Status: {Status}.",
                result.TransactionId, result.Status);

            return result;
        }
        catch (BrokenCircuitException)
        {
            _logger.LogError("Payment service circuit breaker is open. Cannot capture transaction {TransactionId}.", transactionId);
            throw new ServiceUnavailableException("PaymentService");
        }
    }

    public async Task<PaymentResult> RefundPayment(string transactionId)
    {
        _logger.LogInformation("Refunding payment for transaction {TransactionId}.", transactionId);

        try
        {
            var result = await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                // In production: POST to _options.BaseUrl + $"/v1/payments/{transactionId}/refund"
                return await SimulateRefundCall(transactionId, ct);
            });

            _logger.LogInformation(
                "Payment refunded. TransactionId: {TransactionId}, Status: {Status}.",
                result.TransactionId, result.Status);

            return result;
        }
        catch (BrokenCircuitException)
        {
            _logger.LogError("Payment service circuit breaker is open. Cannot refund transaction {TransactionId}.", transactionId);
            throw new ServiceUnavailableException("PaymentService");
        }
    }

    #region Simulated External Calls (replace with real HTTP in production)

    private async Task<PaymentResult> SimulatePaymentCall(string operation, PaymentRequest request, CancellationToken ct)
    {
        // Simulate network latency
        await Task.Delay(Random.Shared.Next(50, 200), ct);

        // Simulate a realistic response
        return new PaymentResult(
            TransactionId: $"txn_{Guid.NewGuid():N}",
            Status: PaymentStatus.Authorized,
            ProcessedAt: DateTime.UtcNow
        );
    }

    private async Task<PaymentResult> SimulateCaptureCall(string transactionId, CancellationToken ct)
    {
        await Task.Delay(Random.Shared.Next(50, 150), ct);

        return new PaymentResult(
            TransactionId: transactionId,
            Status: PaymentStatus.Captured,
            ProcessedAt: DateTime.UtcNow
        );
    }

    private async Task<PaymentResult> SimulateRefundCall(string transactionId, CancellationToken ct)
    {
        await Task.Delay(Random.Shared.Next(50, 150), ct);

        return new PaymentResult(
            TransactionId: transactionId,
            Status: PaymentStatus.Refunded,
            ProcessedAt: DateTime.UtcNow
        );
    }

    #endregion
}

/// <summary>
/// Configuration for the external Payment Service.
/// </summary>
public class PaymentServiceOptions
{
    public const string SectionName = "PaymentService";

    public string BaseUrl { get; set; } = "https://api.payment-provider.example.com";
    public string ApiKey { get; set; } = string.Empty;
    public string MerchantId { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 5;
    public int MaxRetryAttempts { get; set; } = 3;
}
