using AuthService.DAL.Data;
using Shared.Infrastructure;
using Shared.Models.Events;

namespace AuthService.Infrastructure;

/// <summary>
/// Interface for publishing domain events to the message broker.
/// </summary>
public interface IEventPublisher
{
    /// <summary>
    /// Publishes a UserRegistered event to the platform.events exchange.
    /// Falls back to in-memory queue when RabbitMQ is unavailable,
    /// with exponential backoff retry and PostgreSQL persistence on failure.
    /// </summary>
    Task PublishUserRegisteredAsync(UserRegisteredEvent eventMessage);
}

/// <summary>
/// Auth service event publisher that delegates to the shared ResilientEventPublisher.
/// Provides a typed interface specific to auth domain events.
/// </summary>
public class EventPublisher : IEventPublisher
{
    private const string UserRegisteredRoutingKey = "user.registered";

    private readonly IResilientEventPublisher _publisher;

    public EventPublisher(IResilientEventPublisher publisher)
    {
        _publisher = publisher;
    }

    /// <inheritdoc />
    public Task PublishUserRegisteredAsync(UserRegisteredEvent eventMessage)
    {
        return _publisher.PublishAsync(
            eventMessage,
            UserRegisteredRoutingKey,
            eventMessage.Type,
            eventMessage.CorrelationId);
    }
}
