using GameController.FBServiceExt.Application.Contracts.Runtime;

namespace GameController.FBServiceExt.Application.Abstractions.State;

public interface IVoteCooldownStore
{
    ValueTask<VoteCooldownSnapshot?> GetAsync(string showId, string userId, string recipientId, CancellationToken cancellationToken);

    ValueTask SaveAsync(VoteCooldownSnapshot snapshot, TimeSpan retention, CancellationToken cancellationToken);

    ValueTask RemoveAsync(string showId, string userId, string recipientId, CancellationToken cancellationToken);
}
