using GameController.FBServiceExt.Application.Contracts.RawIngress;

namespace GameController.FBServiceExt.Application.Abstractions.Messaging;

public interface IRawIngressConsumer
{
    ValueTask<IMessageLease<RawWebhookEnvelope>?> ReceiveAsync(CancellationToken cancellationToken);
}
