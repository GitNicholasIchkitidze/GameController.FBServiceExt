using GameController.FBServiceExt.Domain.Voting;

namespace GameController.FBServiceExt.Application.Contracts.Runtime;

public sealed record VoteSessionSnapshot(
    string UserId,
    string RecipientId,
    VoteState State,
    string? CandidateId,
    string? CandidateDisplayName,
    string? LastEventId,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? ExpiresAtUtc,
    DateTime? CooldownUntilUtc);
