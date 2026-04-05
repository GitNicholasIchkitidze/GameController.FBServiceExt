using System.Text;
using System.Threading.Channels;
using GameController.FBServiceExt.Application.Abstractions.Messaging;
using GameController.FBServiceExt.Application.Contracts.RawIngress;
using GameController.FBServiceExt.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace GameController.FBServiceExt.Infrastructure.Messaging;

internal sealed class RabbitMqRawIngressConsumer : IRawIngressConsumer, IAsyncDisposable
{
    private readonly RabbitMqConnectionProvider _connectionProvider;
    private readonly IOptionsMonitor<RabbitMqOptions> _optionsMonitor;
    private readonly ILogger<RabbitMqRawIngressConsumer> _logger;
    private readonly Channel<IMessageLease<RawWebhookEnvelope>> _deliveries = Channel.CreateUnbounded<IMessageLease<RawWebhookEnvelope>>();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChannel? _channel;
    private bool _started;

    public RabbitMqRawIngressConsumer(
        RabbitMqConnectionProvider connectionProvider,
        IOptionsMonitor<RabbitMqOptions> optionsMonitor,
        ILogger<RabbitMqRawIngressConsumer> logger)
    {
        _connectionProvider = connectionProvider;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    public async ValueTask<IMessageLease<RawWebhookEnvelope>?> ReceiveAsync(CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);
        return await _deliveries.Reader.ReadAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _deliveries.Writer.TryComplete();

            if (_channel is not null)
            {
                await _channel.DisposeAsync();
                _channel = null;
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async ValueTask EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_started)
            {
                return;
            }

            var options = _optionsMonitor.CurrentValue;
            var connection = await _connectionProvider.GetConnectionAsync(cancellationToken);
            var channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(false, false, null, 1),
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

            await channel.BasicQosAsync(0, options.PrefetchCount, false, cancellationToken);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += OnReceivedAsync;

            await channel.BasicConsumeAsync(
                queue: options.RawIngressQueueName,
                autoAck: false,
                consumerTag: string.Empty,
                noLocal: false,
                exclusive: false,
                arguments: new Dictionary<string, object?>(),
                consumer: consumer,
                cancellationToken: cancellationToken);

            _channel = channel;
            _started = true;

            _logger.LogInformation(
                "RabbitMQ raw ingress consumer started. Queue: {Queue}, Prefetch: {Prefetch}",
                options.RawIngressQueueName,
                options.PrefetchCount);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs eventArgs)
    {
        if (_channel is null)
        {
            return;
        }

        try
        {
            var payload = CreateEnvelope(eventArgs);
            var lease = new RabbitMqMessageLease<RawWebhookEnvelope>(payload, _channel, eventArgs.DeliveryTag);
            await _deliveries.Writer.WriteAsync(lease, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize RabbitMQ raw ingress message. DeliveryTag: {DeliveryTag}", eventArgs.DeliveryTag);
            await _channel.BasicNackAsync(eventArgs.DeliveryTag, multiple: false, requeue: false, CancellationToken.None);
        }
    }

    private static RawWebhookEnvelope CreateEnvelope(BasicDeliverEventArgs eventArgs)
    {
        var properties = eventArgs.BasicProperties;
        if (!string.Equals(properties.Type, RabbitMqMessageSerializer.RawIngressBodyMessageType, StringComparison.Ordinal))
        {
            return RabbitMqMessageSerializer.DeserializeLegacyRawEnvelope(eventArgs.Body);
        }

        var envelopeId = TryParseEnvelopeId(properties.MessageId)
            ?? throw new InvalidOperationException("Raw ingress message did not include a valid message id envelope identifier.");

        var requestId = properties.CorrelationId ?? string.Empty;
        var source = string.IsNullOrWhiteSpace(properties.AppId) ? "unknown" : properties.AppId;
        var receivedAtUtc = ResolveReceivedAtUtc(properties);
        var body = Encoding.UTF8.GetString(eventArgs.Body.Span);

        return new RawWebhookEnvelope(
            envelopeId,
            source,
            requestId,
            receivedAtUtc,
            body);
    }

    private static Guid? TryParseEnvelopeId(string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return null;
        }

        return Guid.TryParse(messageId, out var envelopeId) ? envelopeId : null;
    }

    private static DateTime ResolveReceivedAtUtc(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers is not null &&
            properties.Headers.TryGetValue(RabbitMqMessageSerializer.RawIngressReceivedAtUnixMillisecondsHeader, out var value) &&
            TryReadUnixMilliseconds(value, out var unixMilliseconds))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).UtcDateTime;
        }

        if (properties.Timestamp.UnixTime > 0)
        {
            return DateTimeOffset.FromUnixTimeSeconds(properties.Timestamp.UnixTime).UtcDateTime;
        }

        return DateTime.UtcNow;
    }

    private static bool TryReadUnixMilliseconds(object? value, out long unixMilliseconds)
    {
        switch (value)
        {
            case long longValue:
                unixMilliseconds = longValue;
                return true;
            case int intValue:
                unixMilliseconds = intValue;
                return true;
            case string text when long.TryParse(text, out var parsedString):
                unixMilliseconds = parsedString;
                return true;
            case byte[] bytes when long.TryParse(Encoding.UTF8.GetString(bytes), out var parsedBytes):
                unixMilliseconds = parsedBytes;
                return true;
            default:
                unixMilliseconds = 0;
                return false;
        }
    }
}
