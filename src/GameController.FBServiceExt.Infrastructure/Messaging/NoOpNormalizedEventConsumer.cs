using GameController.FBServiceExt.Application.Abstractions.Messaging;
using GameController.FBServiceExt.Application.Contracts.Normalization;

namespace GameController.FBServiceExt.Infrastructure.Messaging;

public sealed class NoOpNormalizedEventConsumer : INormalizedEventConsumer
{
    public async ValueTask<IMessageLease<NormalizedMessengerEvent>?> ReceiveAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        return null;
    }
}
