using RabbitMQ.Client;

namespace GameController.FBServiceExt.Infrastructure.Messaging;

internal sealed class RabbitMqMessageLease<T> : GameController.FBServiceExt.Application.Abstractions.Messaging.IMessageLease<T>
{
    private readonly IChannel _channel;
    private readonly ulong _deliveryTag;
    private int _completionState;

    public RabbitMqMessageLease(T payload, IChannel channel, ulong deliveryTag)
    {
        Payload = payload;
        _channel = channel;
        _deliveryTag = deliveryTag;
    }

    public T Payload { get; }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _completionState, 1) != 0)
        {
            return;
        }

        await _channel.BasicAckAsync(_deliveryTag, multiple: false, cancellationToken);
    }

    public async ValueTask AbandonAsync(Exception? exception, CancellationToken cancellationToken)
    {
        _ = exception;

        if (Interlocked.Exchange(ref _completionState, 1) != 0)
        {
            return;
        }

        await _channel.BasicNackAsync(_deliveryTag, multiple: false, requeue: true, cancellationToken);
    }
}
