using System.Threading.Channels;
using GameController.FBServiceExt.Application.Abstractions.Messaging;
using GameController.FBServiceExt.Application.Contracts.Normalization;
using GameController.FBServiceExt.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace GameController.FBServiceExt.Infrastructure.Messaging;

internal sealed class RabbitMqNormalizedEventConsumer : INormalizedEventConsumer, IAsyncDisposable
{
    private readonly RabbitMqConnectionProvider _connectionProvider;
    private readonly IOptionsMonitor<RabbitMqOptions> _optionsMonitor;
    private readonly ILogger<RabbitMqNormalizedEventConsumer> _logger;
    private readonly Channel<IMessageLease<NormalizedMessengerEvent>> _deliveries = Channel.CreateUnbounded<IMessageLease<NormalizedMessengerEvent>>();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChannel? _channel;
    private bool _started;

    public RabbitMqNormalizedEventConsumer(
        RabbitMqConnectionProvider connectionProvider,
        IOptionsMonitor<RabbitMqOptions> optionsMonitor,
        ILogger<RabbitMqNormalizedEventConsumer> logger)
    {
        _connectionProvider = connectionProvider;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    // normalized-event queue-დან ერთ event-ს lease-ით აბრუნებს processor worker-სთვის.
    public async ValueTask<IMessageLease<NormalizedMessengerEvent>?> ReceiveAsync(CancellationToken cancellationToken)
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

    // normalized queue-ზე consumer-ს სტარტავს და delivery-ებს შიდა channel-ში აკუმულირებს.
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
                queue: options.NormalizedEventQueueName,
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
                queue: options.NormalizedEventQueueName,
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
                "RabbitMQ normalized event consumer started. Queue: {Queue}, Prefetch: {Prefetch}",
                options.NormalizedEventQueueName,
                options.PrefetchCount);
        }
        finally
        {
            _gate.Release();
        }
    }

    // RabbitMQ payload-ს NormalizedMessengerEvent-ად შლის და processor-ისთვის ამზადებს.
    private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs eventArgs)
    {
        if (_channel is null)
        {
            return;
        }

        try
        {
            var payload = RabbitMqMessageSerializer.DeserializeNormalizedEvent(eventArgs.Body.ToArray());
            var lease = new RabbitMqMessageLease<NormalizedMessengerEvent>(payload, _channel, eventArgs.DeliveryTag);
            await _deliveries.Writer.WriteAsync(lease, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize RabbitMQ normalized event message. DeliveryTag: {DeliveryTag}", eventArgs.DeliveryTag);
            await _channel.BasicNackAsync(eventArgs.DeliveryTag, multiple: false, requeue: false, CancellationToken.None);
        }
    }
}



