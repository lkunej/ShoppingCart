namespace Shared.Models.Interfaces;

/// <summary>
/// Client interface for the external Payment Service (e.g., Stripe, Adyen).
/// Handles payment authorization, capture, and refund operations.
/// Implementations use circuit breaker to prevent cascading failures.
/// </summary>
public interface IPaymentClient
{
    /// <summary>
    /// Authorizes a payment for the given amount. Returns a payment transaction ID.
    /// Does NOT capture funds — only places a hold.
    /// </summary>
    Task<PaymentResult> AuthorizePayment(PaymentRequest request);

    /// <summary>
    /// Captures a previously authorized payment.
    /// </summary>
    Task<PaymentResult> CapturePayment(string transactionId);

    /// <summary>
    /// Refunds a captured payment (full amount).
    /// </summary>
    Task<PaymentResult> RefundPayment(string transactionId);
}

public record PaymentRequest(
    Guid OrderId,
    Guid UserId,
    int AmountCents,
    string Currency,
    string PaymentMethod,
    string? CardToken = null
);

public record PaymentResult(
    string TransactionId,
    PaymentStatus Status,
    string? ErrorMessage = null,
    DateTime? ProcessedAt = null
);

public enum PaymentStatus
{
    Authorized,
    Captured,
    Refunded,
    Declined,
    Failed,
    Pending
}
