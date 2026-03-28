namespace GameController.FBServiceExt.Application.Abstractions.Messaging;

public interface IMessageLease<out T>
{
    T Payload { get; }

    ValueTask CompleteAsync(CancellationToken cancellationToken);

    ValueTask AbandonAsync(Exception? exception, CancellationToken cancellationToken);
}
