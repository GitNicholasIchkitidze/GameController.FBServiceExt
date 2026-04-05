using GameController.FBServiceExt.Application.Contracts.RawIngress;

namespace GameController.FBServiceExt.Application.Abstractions.Messaging;

public interface IRawIngressPublisher
{
    ValueTask PublishAsync(RawIngressPublishRequest publishRequest, CancellationToken cancellationToken);
}
