using GameController.FBServiceExt.Application.Contracts.Normalization;

namespace GameController.FBServiceExt.Application.Abstractions.Messaging;

public interface INormalizedEventPublisher
{
    ValueTask PublishBatchAsync(IReadOnlyCollection<NormalizedMessengerEvent> events, CancellationToken cancellationToken);
}
