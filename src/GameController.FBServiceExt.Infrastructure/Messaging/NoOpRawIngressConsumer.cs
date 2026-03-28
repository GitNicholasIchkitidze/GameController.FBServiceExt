using GameController.FBServiceExt.Application.Abstractions.Messaging;
using GameController.FBServiceExt.Application.Contracts.RawIngress;

namespace GameController.FBServiceExt.Infrastructure.Messaging;

public sealed class NoOpRawIngressConsumer : IRawIngressConsumer
{
    public async ValueTask<IMessageLease<RawWebhookEnvelope>?> ReceiveAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        return null;
    }
}
