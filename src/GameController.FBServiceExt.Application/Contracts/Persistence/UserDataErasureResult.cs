namespace GameController.FBServiceExt.Application.Contracts.Persistence;

public sealed record UserDataErasureResult(
    int NormalizedEventsDeleted,
    int AcceptedVotesDeleted);
