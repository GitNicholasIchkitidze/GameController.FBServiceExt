namespace GameController.FBServiceExt.Infrastructure.Data.Entities;

internal sealed class AcceptedVoteEntity
{
    public Guid VoteId { get; set; }

    public string CorrelationId { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public string RecipientId { get; set; } = string.Empty;

    public string CandidateId { get; set; } = string.Empty;

    public string CandidateDisplayName { get; set; } = string.Empty;

    public string SourceEventId { get; set; } = string.Empty;

    public DateTime ConfirmedAtUtc { get; set; }

    public DateTime CooldownUntilUtc { get; set; }

    public string Channel { get; set; } = string.Empty;

    public string? MetadataJson { get; set; }

    public DateTime RecordedAtUtc { get; set; }
}
