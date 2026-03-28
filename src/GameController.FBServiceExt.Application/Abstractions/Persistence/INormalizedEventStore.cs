using GameController.FBServiceExt.Application.Contracts.Normalization;

namespace GameController.FBServiceExt.Application.Abstractions.Persistence;

public interface INormalizedEventStore
{
    ValueTask<bool> TryAddAsync(NormalizedMessengerEvent normalizedEvent, CancellationToken cancellationToken);
}
