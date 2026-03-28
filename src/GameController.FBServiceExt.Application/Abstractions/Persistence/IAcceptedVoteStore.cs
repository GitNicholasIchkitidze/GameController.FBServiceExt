using GameController.FBServiceExt.Application.Contracts.Votes;

namespace GameController.FBServiceExt.Application.Abstractions.Persistence;

public interface IAcceptedVoteStore
{
    ValueTask<bool> TryAddAsync(AcceptedVote vote, CancellationToken cancellationToken);
}
