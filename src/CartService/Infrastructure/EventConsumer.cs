using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Shared.Models.Events;

namespace CartService.Infrastructure;

/// <summary>
/// Background service that consumes InventoryUpdated events from RabbitMQ.
/// Invalidates cached cart items for affected products.
/// Implements acknowledgment after successful cache invalidation,
/// rejection and re-queue on failure (max 3 redeliveries → dead-letter queue).
/// </summary>
public class InventoryEventConsumer : BackgroundService
{
    private const string ExchangeName = "platform.events";
    private const string QueueName = "cart.inventory-updates";
    private const string RoutingKey = "inventory.updated";
    private const string DeadLetterExchange = "platform.dlx";
    private const string DeadLetterQueue = "cart.inventory-updates.dlq";
    private const int MaxRedeliveryAttempts = 3;

    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly IConnectionFactory _connectionFactory;
    private readonly ICartRedisWrapper _redisWrapper;
    private readonly ILogger<InventoryEventConsumer> _logger;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    private IConnection? _connection;
    private IChannel? _channel;

    public InventoryEventConsumer(
        IConnectionFactory connectionFactory,
        ICartRedisWrapper redisWrapper,
        ILogger<InventoryEventConsumer> logger)
    {
        _connectionFactory = connectionFactory;
        _redisWrapper = redisWrapper;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("InventoryEventConsumer starting. Will consume from queue '{Queue}'.", QueueName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureConnectionAsync(stoppingToken);
                await ConsumeMessagesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "InventoryEventConsumer encountered an error. Reconnecting in {Delay}s.",
                    ReconnectDelay.TotalSeconds);

                await ResetConnectionAsync();

                try
                {
                    await Task.Delay(ReconnectDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("InventoryEventConsumer stopping.");
    }

    private async Task EnsureConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true } && _channel is { IsOpen: true })
        {
            return;
        }

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true } && _channel is { IsOpen: true })
            {
                return;
            }

            _channel?.Dispose();
            _connection?.Dispose();

            _connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
            _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);

            // Declare dead-letter exchange and queue
            await _channel.ExchangeDeclareAsync(
                exchange: DeadLetterExchange,
                type: ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                cancellationToken: cancellationToken);

            await _channel.QueueDeclareAsync(
                queue: DeadLetterQueue,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null,
                cancellationToken: cancellationToken);

            await _channel.QueueBindAsync(
                queue: DeadLetterQueue,
                exchange: DeadLetterExchange,
                routingKey: RoutingKey,
                cancellationToken: cancellationToken);

            // Declare main exchange
            await _channel.ExchangeDeclareAsync(
                exchange: ExchangeName,
                type: ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                cancellationToken: cancellationToken);

            // Declare main queue with dead-letter routing
            var queueArgs = new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = DeadLetterExchange,
                ["x-dead-letter-routing-key"] = RoutingKey
            };

            await _channel.QueueDeclareAsync(
                queue: QueueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: queueArgs,
                cancellationToken: cancellationToken);

            await _channel.QueueBindAsync(
                queue: QueueName,
                exchange: ExchangeName,
                routingKey: RoutingKey,
                cancellationToken: cancellationToken);

            // Prefetch 1 message at a time for fair dispatch
            await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, cancellationToken: cancellationToken);

            _logger.LogInformation(
                "RabbitMQ consumer connection established. Queue '{Queue}' bound to exchange '{Exchange}' with routing key '{RoutingKey}'.",
                QueueName, ExchangeName, RoutingKey);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task ConsumeMessagesAsync(CancellationToken stoppingToken)
    {
        if (_channel == null)
        {
            throw new InvalidOperationException("RabbitMQ channel is not available.");
        }

        var consumer = new AsyncEventingBasicConsumer(_channel);

        consumer.ReceivedAsync += async (sender, ea) =>
        {
            try
            {
                await HandleMessageAsync(ea);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in message handler for delivery tag {DeliveryTag}.", ea.DeliveryTag);
            }
        };

        await _channel.BasicConsumeAsync(
            queue: QueueName,
            autoAck: false,
            consumer: consumer,
            cancellationToken: stoppingToken);

        // Keep the method alive until cancellation
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
    }

    private async Task HandleMessageAsync(BasicDeliverEventArgs ea)
    {
        var body = ea.Body.ToArray();
        var correlationId = ea.BasicProperties?.CorrelationId ?? "unknown";

        try
        {
            var json = Encoding.UTF8.GetString(body);
            var inventoryEvent = JsonSerializer.Deserialize<InventoryUpdatedEvent>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            });

            if (inventoryEvent == null || inventoryEvent.Payload == null)
            {
                _logger.LogWarning(
                    "Received malformed InventoryUpdated event. Acknowledging to discard. CorrelationId: {CorrelationId}",
                    correlationId);

                if (_channel != null)
                {
                    await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                }
                return;
            }

            var productId = inventoryEvent.Payload.ProductId;

            _logger.LogInformation(
                "Processing InventoryUpdated event for product {ProductId}. CorrelationId: {CorrelationId}",
                productId, correlationId);

            // Invalidate cached carts containing this product
            if (Guid.TryParse(productId, out var productGuid))
            {
                await _redisWrapper.InvalidateCartsByProductIdAsync(productGuid);
            }
            else
            {
                _logger.LogWarning(
                    "Invalid productId format '{ProductId}' in InventoryUpdated event. CorrelationId: {CorrelationId}",
                    productId, correlationId);
            }

            // Acknowledge only after successful invalidation
            if (_channel != null)
            {
                await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
            }

            _logger.LogInformation(
                "Successfully processed InventoryUpdated event for product {ProductId}. CorrelationId: {CorrelationId}",
                productId, correlationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to process InventoryUpdated event. DeliveryTag: {DeliveryTag}. CorrelationId: {CorrelationId}",
                ea.DeliveryTag, correlationId);

            // Check redelivery count from x-death header
            var redeliveryCount = GetRedeliveryCount(ea.BasicProperties);

            if (redeliveryCount >= MaxRedeliveryAttempts)
            {
                // Max retries reached — reject without requeue (routes to DLQ via dead-letter exchange)
                _logger.LogWarning(
                    "Max redelivery attempts ({MaxAttempts}) reached for message. Routing to dead-letter queue. CorrelationId: {CorrelationId}",
                    MaxRedeliveryAttempts, correlationId);

                if (_channel != null)
                {
                    await _channel.BasicRejectAsync(ea.DeliveryTag, requeue: false);
                }
            }
            else
            {
                // Reject and re-queue for redelivery
                _logger.LogWarning(
                    "Rejecting and re-queuing message for redelivery (attempt {Attempt}/{MaxAttempts}). CorrelationId: {CorrelationId}",
                    redeliveryCount + 1, MaxRedeliveryAttempts, correlationId);

                if (_channel != null)
                {
                    await _channel.BasicRejectAsync(ea.DeliveryTag, requeue: true);
                }
            }
        }
    }

    /// <summary>
    /// Gets the redelivery count from the x-death header.
    /// RabbitMQ populates x-death when messages are dead-lettered/redelivered.
    /// </summary>
    private static int GetRedeliveryCount(IReadOnlyBasicProperties? properties)
    {
        if (properties?.Headers == null)
        {
            return 0;
        }

        if (!properties.Headers.TryGetValue("x-death", out var xDeathObj))
        {
            // If no x-death header but message is redelivered, count as 1
            return properties.Headers.ContainsKey("x-redelivered-count")
                ? Convert.ToInt32(properties.Headers["x-redelivered-count"])
                : 0;
        }

        if (xDeathObj is List<object> xDeathList)
        {
            long totalCount = 0;
            foreach (var entry in xDeathList)
            {
                if (entry is Dictionary<string, object> deathEntry &&
                    deathEntry.TryGetValue("count", out var countObj))
                {
                    totalCount += Convert.ToInt64(countObj);
                }
            }
            return (int)totalCount;
        }

        return 0;
    }

    private async Task ResetConnectionAsync()
    {
        await _connectionLock.WaitAsync();
        try
        {
            if (_channel != null)
            {
                try { _channel.Dispose(); } catch { /* best effort */ }
                _channel = null;
            }

            if (_connection != null)
            {
                try { _connection.Dispose(); } catch { /* best effort */ }
                _connection = null;
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        _connectionLock.Dispose();
        base.Dispose();
    }
}
