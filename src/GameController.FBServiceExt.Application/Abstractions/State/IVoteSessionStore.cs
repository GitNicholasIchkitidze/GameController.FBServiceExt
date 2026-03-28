using GameController.FBServiceExt.Application.Contracts.Runtime;

namespace GameController.FBServiceExt.Application.Abstractions.State;

public interface IVoteSessionStore
{
    ValueTask<VoteSessionSnapshot?> GetAsync(string userId, string recipientId, CancellationToken cancellationToken);

    ValueTask SaveAsync(VoteSessionSnapshot snapshot, CancellationToken cancellationToken);

    ValueTask RemoveAsync(string userId, string recipientId, CancellationToken cancellationToken);
}
