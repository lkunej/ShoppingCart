using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Shared.Models.Entities;

namespace Shared.Infrastructure;

/// <summary>
/// Interface for the generic resilient event publisher.
/// </summary>
public interface IResilientEventPublisher
{
    /// <summary>
    /// Publishes an event with the specified routing key.
    /// Falls back to in-memory queue when RabbitMQ is unavailable,
    /// with exponential backoff retry and PostgreSQL persistence on failure.
    /// </summary>
    Task PublishAsync<TEvent>(TEvent eventMessage, string routingKey, string eventType, string? correlationId)
        where TEvent : class;
}

/// <summary>
/// Generic RabbitMQ event publisher with resilience features:
/// - In-memory queue fallback (max 1000 messages) when RabbitMQ is unavailable
/// - Exponential backoff retry (1s, 2s, 4s) up to 3 attempts
/// - PostgreSQL persistence for failed messages after all retries exhausted
/// 
/// Shared across all services. Each service provides its own DbContext type
/// via the TDbContext generic parameter for failed event persistence.
/// </summary>
public class ResilientEventPublisher<TDbContext> : IResilientEventPublisher, IDisposable
    where TDbContext : DbContext
{
    private const string ExchangeName = "platform.events";
    private const int MaxQueueSize = 1000;
    private const int MaxRetryAttempts = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4)
    ];

    private readonly IConnectionFactory _connectionFactory;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<ResilientEventPublisher<TDbContext>> _logger;
    private readonly ConcurrentQueue<QueuedMessage> _messageQueue = new();
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    private IConnection? _connection;
    private IChannel? _channel;
    private volatile int _queueCount;

    public ResilientEventPublisher(
        IConnectionFactory connectionFactory,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<ResilientEventPublisher<TDbContext>> logger)
    {
        _connectionFactory = connectionFactory;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task PublishAsync<TEvent>(TEvent eventMessage, string routingKey, string eventType, string? correlationId)
        where TEvent : class
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(eventMessage, JsonOptions);

        var message = new QueuedMessage
        {
            RoutingKey = routingKey,
            Body = body,
            EventType = eventType,
            CorrelationId = correlationId
        };

        await PublishWithRetryAsync(message);
    }

    private async Task PublishWithRetryAsync(QueuedMessage message)
    {
        for (int attempt = 0; attempt < MaxRetryAttempts; attempt++)
        {
            try
            {
                await EnsureConnectionAsync();
                await PublishToRabbitMqAsync(message);

                _logger.LogInformation(
                    "Published event {EventType} with routing key {RoutingKey}. CorrelationId: {CorrelationId}",
                    message.EventType, message.RoutingKey, message.CorrelationId);
                return;
            }
            catch (Exception ex) when (IsRabbitMqUnavailableException(ex))
            {
                _logger.LogWarning(ex,
                    "RabbitMQ publish attempt {Attempt}/{MaxAttempts} failed for event {EventType}. CorrelationId: {CorrelationId}",
                    attempt + 1, MaxRetryAttempts, message.EventType, message.CorrelationId);

                message.LastError = ex.Message;
                await ResetConnectionAsync();

                if (attempt < MaxRetryAttempts - 1)
                {
                    await Task.Delay(RetryDelays[attempt]);
                }
            }
        }

        // All retries exhausted — queue locally and persist to PostgreSQL
        EnqueueMessage(message);
        await PersistFailedMessageAsync(message);
    }

    private async Task PublishToRabbitMqAsync(QueuedMessage message)
    {
        if (_channel == null)
        {
            throw new InvalidOperationException("RabbitMQ channel is not available.");
        }

        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            CorrelationId = message.CorrelationId,
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        };

        await _channel.BasicPublishAsync(
            exchange: ExchangeName,
            routingKey: message.RoutingKey,
            mandatory: false,
            basicProperties: properties,
            body: message.Body);
    }

    private async Task EnsureConnectionAsync()
    {
        if (_connection is { IsOpen: true } && _channel is { IsOpen: true })
        {
            return;
        }

        await _connectionLock.WaitAsync();
        try
        {
            if (_connection is { IsOpen: true } && _channel is { IsOpen: true })
            {
                return;
            }

            _channel?.Dispose();
            _connection?.Dispose();

            _connection = await _connectionFactory.CreateConnectionAsync();
            _channel = await _connection.CreateChannelAsync();

            await _channel.ExchangeDeclareAsync(
                exchange: ExchangeName,
                type: ExchangeType.Topic,
                durable: true,
                autoDelete: false);

            _logger.LogInformation("RabbitMQ connection established. Exchange '{Exchange}' declared.", ExchangeName);
        }
        finally
        {
            _connectionLock.Release();
        }
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

    private void EnqueueMessage(QueuedMessage message)
    {
        if (_queueCount >= MaxQueueSize)
        {
            _logger.LogError(
                "In-memory message queue is full ({MaxSize} messages). Dropping event {EventType}. CorrelationId: {CorrelationId}",
                MaxQueueSize, message.EventType, message.CorrelationId);
            return;
        }

        _messageQueue.Enqueue(message);
        Interlocked.Increment(ref _queueCount);

        _logger.LogWarning(
            "Event {EventType} queued locally. Queue size: {QueueSize}/{MaxSize}. CorrelationId: {CorrelationId}",
            message.EventType, _queueCount, MaxQueueSize, message.CorrelationId);
    }

    private async Task PersistFailedMessageAsync(QueuedMessage message)
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();

            var failedEvent = new FailedEvent
            {
                EventType = message.EventType,
                RoutingKey = message.RoutingKey,
                Payload = Encoding.UTF8.GetString(message.Body),
                CorrelationId = message.CorrelationId,
                RetryCount = MaxRetryAttempts,
                LastError = message.LastError,
                CreatedAt = DateTime.UtcNow
            };

            dbContext.Set<FailedEvent>().Add(failedEvent);
            await dbContext.SaveChangesAsync();

            _logger.LogWarning(
                "Failed event {EventType} persisted to PostgreSQL for recovery. CorrelationId: {CorrelationId}",
                message.EventType, message.CorrelationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to persist event {EventType} to PostgreSQL. Message may be lost. CorrelationId: {CorrelationId}",
                message.EventType, message.CorrelationId);
        }
    }

    private static bool IsRabbitMqUnavailableException(Exception ex)
    {
        return ex is BrokerUnreachableException
            or AlreadyClosedException
            or OperationInterruptedException
            or InvalidOperationException
            or IOException
            or TimeoutException;
    }

    public void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        _connectionLock.Dispose();
    }

    private sealed class QueuedMessage
    {
        public string RoutingKey { get; init; } = string.Empty;
        public byte[] Body { get; init; } = [];
        public string EventType { get; init; } = string.Empty;
        public string? CorrelationId { get; init; }
        public string? LastError { get; set; }
    }
}
