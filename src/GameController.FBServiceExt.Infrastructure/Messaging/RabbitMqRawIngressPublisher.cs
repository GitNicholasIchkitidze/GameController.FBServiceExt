using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using GameController.FBServiceExt.Application.Abstractions.Messaging;
using GameController.FBServiceExt.Application.Abstractions.Observability;
using GameController.FBServiceExt.Application.Contracts.RawIngress;
using GameController.FBServiceExt.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace GameController.FBServiceExt.Infrastructure.Messaging;

internal sealed class RabbitMqRawIngressPublisher : IRawIngressPublisher, IAsyncDisposable
{
    private readonly RabbitMqConnectionProvider _connectionProvider;
    private readonly IOptionsMonitor<RabbitMqOptions> _optionsMonitor;
    private readonly IRuntimeMetricsCollector _runtimeMetricsCollector;
    private readonly ILogger<RabbitMqRawIngressPublisher> _logger;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly ConcurrentBag<IChannel> _availableChannels = new();
    private SemaphoreSlim? _channelLeaseGate;
    private bool _initialized;
    private int _configuredPoolSize;
    private int _leasedChannels;

    public RabbitMqRawIngressPublisher(
        RabbitMqConnectionProvider connectionProvider,
        IOptionsMonitor<RabbitMqOptions> optionsMonitor,
        IRuntimeMetricsCollector runtimeMetricsCollector,
        ILogger<RabbitMqRawIngressPublisher> logger)
    {
        _connectionProvider = connectionProvider;
        _optionsMonitor = optionsMonitor;
        _runtimeMetricsCollector = runtimeMetricsCollector;
        _logger = logger;
    }

    // API-დან მიღებულ raw webhook body-ს RabbitMQ raw-ingress queue-ში წერს.
    // შემდეგ ამავე შეტყობინებას RawIngressConsumer და RawIngressNormalizerWorker წაიკითხავენ.
    public async ValueTask PublishAsync(RawIngressPublishRequest publishRequest, CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;
        var totalStopwatch = Stopwatch.StartNew();
        await EnsurePoolAsync(options, cancellationToken);

        var rentStopwatch = Stopwatch.StartNew();
        var channel = await RentChannelAsync(options, cancellationToken);
        rentStopwatch.Stop();
        _runtimeMetricsCollector.ObserveDuration("api.ingress.channel_rent_wait_ms", rentStopwatch.Elapsed.TotalMilliseconds);

        var returnToPool = true;
        var publishMs = 0d;

        try
        {
            if (!channel.IsOpen)
            {
                _runtimeMetricsCollector.Increment("api.ingress.closed_channel_rents");
                await channel.DisposeAsync();
                channel = await CreateDeclaredChannelAsync(options, cancellationToken);
            }

            var properties = new BasicProperties
            {
                ContentType = "application/json",
                ContentEncoding = Encoding.UTF8.WebName,
                DeliveryMode = DeliveryModes.Persistent,
                MessageId = publishRequest.EnvelopeId.ToString("N"),
                CorrelationId = publishRequest.RequestId,
                Type = RabbitMqMessageSerializer.RawIngressBodyMessageType,
                AppId = publishRequest.Source,
                Timestamp = new AmqpTimestamp(new DateTimeOffset(publishRequest.ReceivedAtUtc).ToUnixTimeSeconds()),
                Headers = new Dictionary<string, object?>
                {
                    [RabbitMqMessageSerializer.RawIngressReceivedAtUnixMillisecondsHeader] = new DateTimeOffset(publishRequest.ReceivedAtUtc).ToUnixTimeMilliseconds()
                }
            };

            var publishStopwatch = Stopwatch.StartNew();
            await channel.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: options.RawIngressQueueName,
                mandatory: true,
                basicProperties: properties,
                body: publishRequest.BodyUtf8,
                cancellationToken: cancellationToken);
            publishStopwatch.Stop();
            publishMs = publishStopwatch.Elapsed.TotalMilliseconds;
            _runtimeMetricsCollector.ObserveDuration("api.ingress.rabbitmq_publish_ms", publishMs);
            _runtimeMetricsCollector.Increment("api.ingress.publish_success");

            if (rentStopwatch.Elapsed.TotalMilliseconds >= 100 || publishMs >= 100)
            {
                _logger.LogWarning(
                    "Raw ingress RabbitMQ publish pressure detected. EnvelopeId: {EnvelopeId}, RentWaitMs: {RentWaitMs}, PublishMs: {PublishMs}, LeasedChannels: {LeasedChannels}, PoolSize: {PoolSize}",
                    publishRequest.EnvelopeId,
                    rentStopwatch.Elapsed.TotalMilliseconds,
                    publishMs,
                    Volatile.Read(ref _leasedChannels),
                    _configuredPoolSize);
            }

            _logger.LogDebug(
                "Published raw ingress body to RabbitMQ. EnvelopeId: {EnvelopeId}, Queue: {Queue}, BodyBytes: {BodyBytes}",
                publishRequest.EnvelopeId,
                options.RawIngressQueueName,
                publishRequest.BodyUtf8.Length);
        }
        catch (Exception exception)
        {
            returnToPool = false;
            _runtimeMetricsCollector.Increment("api.ingress.publish_failures");
            _runtimeMetricsCollector.Increment($"api.ingress.publish_exception_type.{SanitizeMetricSegment(exception.GetType().Name)}");
            await DisposeChannelQuietlyAsync(channel);
            throw;
        }
        finally
        {
            totalStopwatch.Stop();
            _runtimeMetricsCollector.ObserveDuration("api.ingress.publish_total_ms", totalStopwatch.Elapsed.TotalMilliseconds);
            _runtimeMetricsCollector.SetGauge("api.ingress.publisher_channels_leased", Math.Max(0, Volatile.Read(ref _leasedChannels)));
            _runtimeMetricsCollector.SetGauge("api.ingress.publisher_channels_available", Math.Max(0, _configuredPoolSize - Volatile.Read(ref _leasedChannels)));
            _runtimeMetricsCollector.SetGauge("api.ingress.publisher_pool_size", _configuredPoolSize);
            _runtimeMetricsCollector.SetGauge("api.ingress.last_publish_ms", publishMs);

            if (returnToPool)
            {
                ReturnChannel(channel);
            }
            else
            {
                await TryReplaceChannelAsync(options);
            }
        }
    }

    public async ValueTask EnsureReadyAsync(CancellationToken cancellationToken)
    {
        await EnsurePoolAsync(_optionsMonitor.CurrentValue, cancellationToken);
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
            _configuredPoolSize = 0;
            Interlocked.Exchange(ref _leasedChannels, 0);
            _initialized = false;
        }
        finally
        {
            _initializationGate.Release();
            _initializationGate.Dispose();
        }
    }

    // publisher-ის channel pool-ს ინიციალიზებს, რომ publish path-ზე ზედმეტი connect/create ხარჯი შემცირდეს.
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
            _configuredPoolSize = poolSize;

            for (var index = 0; index < poolSize; index++)
            {
                var channel = await CreateDeclaredChannelAsync(options, cancellationToken);
                _availableChannels.Add(channel);
            }

            _initialized = true;
            _runtimeMetricsCollector.SetGauge("api.ingress.publisher_pool_size", poolSize);
            _runtimeMetricsCollector.SetGauge("api.ingress.publisher_channels_available", poolSize);
            _runtimeMetricsCollector.SetGauge("api.ingress.publisher_channels_leased", 0);
            _logger.LogInformation(
                "RabbitMQ raw ingress publisher pool initialized. Queue: {Queue}, Channels: {ChannelCount}",
                options.RawIngressQueueName,
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
            throw new InvalidOperationException("Raw ingress channel pool is not initialized.");
        }

        await _channelLeaseGate.WaitAsync(cancellationToken);
        Interlocked.Increment(ref _leasedChannels);
        UpdatePoolGauges();

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
            Interlocked.Decrement(ref _leasedChannels);
            UpdatePoolGauges();
            _channelLeaseGate.Release();
            throw;
        }
    }

    private void ReturnChannel(IChannel channel)
    {
        _availableChannels.Add(channel);
        Interlocked.Decrement(ref _leasedChannels);
        UpdatePoolGauges();
        _channelLeaseGate?.Release();
    }

    private async ValueTask TryReplaceChannelAsync(RabbitMqOptions options)
    {
        try
        {
            var replacement = await CreateDeclaredChannelAsync(options, CancellationToken.None);
            _availableChannels.Add(replacement);
            _runtimeMetricsCollector.Increment("api.ingress.channel_replacements");
        }
        catch (Exception ex)
        {
            _runtimeMetricsCollector.Increment("api.ingress.channel_replacement_failures");
            _logger.LogWarning(ex, "Failed to replace a raw ingress publisher channel after publish failure. Future publishes will recreate channels on demand.");
        }
        finally
        {
            Interlocked.Decrement(ref _leasedChannels);
            UpdatePoolGauges();
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
            queue: options.RawIngressQueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>(),
            passive: false,
            noWait: false,
            cancellationToken: cancellationToken);

        return channel;
    }

    private void UpdatePoolGauges()
    {
        var leased = Math.Max(0, Volatile.Read(ref _leasedChannels));
        _runtimeMetricsCollector.SetGauge("api.ingress.publisher_channels_leased", leased);
        _runtimeMetricsCollector.SetGauge("api.ingress.publisher_channels_available", Math.Max(0, _configuredPoolSize - leased));
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

    private static string SanitizeMetricSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '_');
        }

        return builder.ToString().Trim('_');
    }
}
