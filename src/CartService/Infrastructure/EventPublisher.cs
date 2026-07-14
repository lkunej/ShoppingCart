using Shared.Infrastructure;
using Shared.Models.Events;

namespace CartService.Infrastructure;

/// <summary>
/// Interface for publishing cart domain events to the message broker.
/// </summary>
public interface ICartEventPublisher
{
    /// <summary>
    /// Publishes a CartCleared event to the platform.events exchange.
    /// Falls back to in-memory queue when RabbitMQ is unavailable,
    /// with exponential backoff retry and PostgreSQL persistence on failure.
    /// </summary>
    Task PublishCartClearedAsync(CartClearedEvent eventMessage);
}

/// <summary>
/// Cart service event publisher that delegates to the shared ResilientEventPublisher.
/// Provides a typed interface specific to cart domain events.
/// </summary>
public class CartEventPublisher : ICartEventPublisher
{
    private const string CartClearedRoutingKey = "cart.cleared";

    private readonly IResilientEventPublisher _publisher;

    public CartEventPublisher(IResilientEventPublisher publisher)
    {
        _publisher = publisher;
    }

    /// <inheritdoc />
    public Task PublishCartClearedAsync(CartClearedEvent eventMessage)
    {
        return _publisher.PublishAsync(
            eventMessage,
            CartClearedRoutingKey,
            eventMessage.Type,
            eventMessage.CorrelationId);
    }
}
