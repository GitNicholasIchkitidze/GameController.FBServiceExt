using System.Collections.Concurrent;
using System.Text;
using GameController.FBServiceExt.Application.Abstractions.Messaging;
using GameController.FBServiceExt.Application.Contracts.Normalization;
using GameController.FBServiceExt.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace GameController.FBServiceExt.Infrastructure.Messaging;

internal sealed class RabbitMqNormalizedEventPublisher : INormalizedEventPublisher, IAsyncDisposable
{
    private readonly RabbitMqConnectionProvider _connectionProvider;
    private readonly IOptionsMonitor<RabbitMqOptions> _optionsMonitor;
    private readonly ILogger<RabbitMqNormalizedEventPublisher> _logger;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly ConcurrentBag<IChannel> _availableChannels = new();
    private SemaphoreSlim? _channelLeaseGate;
    private bool _initialized;

    public RabbitMqNormalizedEventPublisher(
        RabbitMqConnectionProvider connectionProvider,
        IOptionsMonitor<RabbitMqOptions> optionsMonitor,
        ILogger<RabbitMqNormalizedEventPublisher> logger)
    {
        _connectionProvider = connectionProvider;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    // normalize-ის შემდეგ მიღებულ event-ებს normalized-event queue-ში აგზავნის batch-ად.
    // შემდეგ ეტაპზე ამ queue-ს NormalizedEventProcessorWorker მოიხმარს.
    public async ValueTask PublishBatchAsync(IReadOnlyCollection<NormalizedMessengerEvent> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return;
        }

        var options = _optionsMonitor.CurrentValue;
        await EnsurePoolAsync(options, cancellationToken);

        var channel = await RentChannelAsync(options, cancellationToken);
        var returnToPool = true;

        try
        {
            if (!channel.IsOpen)
            {
                await channel.DisposeAsync();
                channel = await CreateDeclaredChannelAsync(options, cancellationToken);
            }

            foreach (var normalizedEvent in events)
            {
                var body = RabbitMqMessageSerializer.Serialize(normalizedEvent);
                var properties = new BasicProperties
                {
                    ContentType = "application/json",
                    ContentEncoding = Encoding.UTF8.WebName,
                    DeliveryMode = DeliveryModes.Persistent,
                    MessageId = normalizedEvent.EventId,
                    CorrelationId = normalizedEvent.MessageId ?? normalizedEvent.EventId,
                    Type = nameof(NormalizedMessengerEvent),
                    Timestamp = new AmqpTimestamp(new DateTimeOffset(normalizedEvent.OccurredAtUtc).ToUnixTimeSeconds())
                };

                await channel.BasicPublishAsync(
                    exchange: string.Empty,
                    routingKey: options.NormalizedEventQueueName,
                    mandatory: true,
                    basicProperties: properties,
                    body: body,
                    cancellationToken: cancellationToken);
            }

            _logger.LogDebug(
                "Published normalized event batch to RabbitMQ. Count: {Count}, Queue: {Queue}",
                events.Count,
                options.NormalizedEventQueueName);
        }
        catch
        {
            returnToPool = false;
            await DisposeChannelQuietlyAsync(channel);
            throw;
        }
        finally
        {
            if (returnToPool)
            {
                ReturnChannel(channel);
            }
            else
            {
                await TryReplaceChannelAsync(options, cancellationToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _initializationGate.WaitAsync();
        try
        {
            while (_availableChannels.TryTake(out var channel))
            {
                await DisposeChannelQuietlyAsync(channel);
            }

            _channelLeaseGate?.Dispose();
            _channelLeaseGate = null;
            _initialized = false;
        }
        finally
        {
            _initializationGate.Release();
            _initializationGate.Dispose();
        }
    }

    // normalized publisher-ის channel pool-ს ამზადებს მაღალი დატვირთვისთვის.
    private async ValueTask EnsurePoolAsync(RabbitMqOptions options, CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            var poolSize = Math.Max(1, options.PublisherChannelPoolSize);
            _channelLeaseGate = new SemaphoreSlim(poolSize, poolSize);

            for (var index = 0; index < poolSize; index++)
            {
                var channel = await CreateDeclaredChannelAsync(options, cancellationToken);
                _availableChannels.Add(channel);
            }

            _initialized = true;
            _logger.LogInformation(
                "RabbitMQ normalized event publisher pool initialized. Queue: {Queue}, Channels: {ChannelCount}",
                options.NormalizedEventQueueName,
                poolSize);
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async ValueTask<IChannel> RentChannelAsync(RabbitMqOptions options, CancellationToken cancellationToken)
    {
        await EnsurePoolAsync(options, cancellationToken);

        if (_channelLeaseGate is null)
        {
            throw new InvalidOperationException("Normalized event channel pool is not initialized.");
        }

        await _channelLeaseGate.WaitAsync(cancellationToken);

        if (_availableChannels.TryTake(out var channel))
        {
            return channel;
        }

        try
        {
            return await CreateDeclaredChannelAsync(options, cancellationToken);
        }
        catch
        {
            _channelLeaseGate.Release();
            throw;
        }
    }

    private void ReturnChannel(IChannel channel)
    {
        _availableChannels.Add(channel);
        _channelLeaseGate?.Release();
    }

    private async ValueTask TryReplaceChannelAsync(RabbitMqOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var replacement = await CreateDeclaredChannelAsync(options, CancellationToken.None);
            _availableChannels.Add(replacement);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to replace a normalized event publisher channel after publish failure. Future publishes will recreate channels on demand.");
        }
        finally
        {
            _channelLeaseGate?.Release();
        }
    }

    private async ValueTask<IChannel> CreateDeclaredChannelAsync(RabbitMqOptions options, CancellationToken cancellationToken)
    {
        var connection = await _connectionProvider.GetConnectionAsync(cancellationToken);
        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(options.PublisherConfirmationsEnabled, options.PublisherConfirmationTrackingEnabled, null, null),
            cancellationToken);

        await channel.QueueDeclareAsync(
            queue: options.NormalizedEventQueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>(),
            passive: false,
            noWait: false,
            cancellationToken: cancellationToken);

        return channel;
    }

    private static async ValueTask DisposeChannelQuietlyAsync(IChannel channel)
    {
        try
        {
            await channel.DisposeAsync();
        }
        catch
        {
        }
    }
}


