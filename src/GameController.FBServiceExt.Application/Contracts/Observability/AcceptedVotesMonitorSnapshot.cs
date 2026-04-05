namespace GameController.FBServiceExt.Application.Contracts.Observability;

public sealed record AcceptedVotesMonitorSnapshot(
    DateTime GeneratedAtUtc,
    DateTime? FromUtc,
    DateTime? ToUtc,
    string? ShowId,
    int TotalVotes,
    int TotalUniqueUsers,
    IReadOnlyList<AcceptedVotesMonitorCandidateSummary> Candidates,
    IReadOnlyList<AcceptedVotesMonitorRecentVote> RecentVotes);

public sealed record AcceptedVotesMonitorCandidateSummary(
    string CandidateId,
    string CandidateDisplayName,
    int VoteCount,
    double VotePercentage,
    int UniqueUsers,
    IReadOnlyList<AcceptedVotesMonitorTopFan> TopFans);

public sealed record AcceptedVotesMonitorTopFan(
    string UserId,
    string UserAccountName,
    int VoteCount);

public sealed record AcceptedVotesMonitorRecentVote(
    string UserId,
    string UserAccountName,
    string CandidateId,
    string CandidateDisplayName,
    string ShowId,
    DateTime ConfirmedAtUtc,
    DateTime RecordedAtUtc);
