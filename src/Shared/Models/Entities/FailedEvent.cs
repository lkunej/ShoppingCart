namespace Shared.Models.Entities;

/// <summary>
/// Represents an event that failed to publish to RabbitMQ after all retry attempts.
/// Persisted to PostgreSQL for later recovery.
/// Shared across all services that publish domain events.
/// </summary>
public class FailedEvent
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string RoutingKey { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string? CorrelationId { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
}
