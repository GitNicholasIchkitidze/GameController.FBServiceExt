using GameController.FBServiceExt.Application.Contracts.Normalization;

namespace GameController.FBServiceExt.Application.Abstractions.Messaging;

public interface INormalizedEventConsumer
{
    ValueTask<IMessageLease<NormalizedMessengerEvent>?> ReceiveAsync(CancellationToken cancellationToken);
}
