namespace GameController.FBServiceExt.Application.Contracts.Runtime;

public sealed record VoteCooldownSnapshot(
    string ShowId,
    string UserId,
    string RecipientId,
    DateTime LastVotedUtc);
