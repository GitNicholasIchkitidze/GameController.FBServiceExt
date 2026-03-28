using GameController.FBServiceExt.Application.Contracts.RawIngress;
using GameController.FBServiceExt.Application.Contracts.Normalization;

namespace GameController.FBServiceExt.Application.Abstractions.Processing;

public interface IRawWebhookNormalizer
{
    ValueTask<IReadOnlyList<NormalizedMessengerEvent>> NormalizeAsync(RawWebhookEnvelope envelope, CancellationToken cancellationToken);
}
