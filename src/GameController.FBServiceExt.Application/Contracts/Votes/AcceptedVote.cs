namespace GameController.FBServiceExt.Application.Contracts.Votes;

public sealed record AcceptedVote(
    Guid VoteId,
    string CorrelationId,
    string UserId,
    string RecipientId,
    string CandidateId,
    string CandidateDisplayName,
    string SourceEventId,
    DateTime ConfirmedAtUtc,
    DateTime CooldownUntilUtc,
    string Channel,
    string? MetadataJson);
