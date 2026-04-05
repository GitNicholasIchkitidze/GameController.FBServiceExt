namespace GameController.FBServiceExt.Application.Contracts.Runtime;

public sealed record VotingRuntimeState(
    bool VotingStarted,
    string? ActiveShowId);
