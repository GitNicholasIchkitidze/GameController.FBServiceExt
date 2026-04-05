using GameController.FBServiceExt.Application.Contracts.Runtime;

namespace GameController.FBServiceExt.Application.Abstractions.State;

public interface IVotingGateService
{
    ValueTask<VotingRuntimeState> GetStateAsync(CancellationToken cancellationToken);

    ValueTask SetStateAsync(VotingRuntimeState state, CancellationToken cancellationToken);

    ValueTask<bool> IsVotingStartedAsync(CancellationToken cancellationToken);

    ValueTask SetVotingStartedAsync(bool started, CancellationToken cancellationToken);

    ValueTask<string?> GetActiveShowIdAsync(CancellationToken cancellationToken);

    ValueTask SetActiveShowIdAsync(string? activeShowId, CancellationToken cancellationToken);
}
