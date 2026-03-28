using GameController.FBServiceExt.Application.Contracts.RawIngress;

namespace GameController.FBServiceExt.Application.Abstractions.Messaging;

public interface IRawIngressPublisher
{
    ValueTask PublishAsync(RawWebhookEnvelope envelope, CancellationToken cancellationToken);
}
