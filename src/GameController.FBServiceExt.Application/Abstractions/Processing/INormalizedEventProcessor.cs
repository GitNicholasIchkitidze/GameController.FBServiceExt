using GameController.FBServiceExt.Application.Contracts.Normalization;

namespace GameController.FBServiceExt.Application.Abstractions.Processing;

public interface INormalizedEventProcessor
{
    ValueTask ProcessAsync(NormalizedMessengerEvent normalizedEvent, CancellationToken cancellationToken);
}
