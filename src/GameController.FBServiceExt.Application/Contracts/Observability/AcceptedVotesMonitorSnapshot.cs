namespace GameController.FBServiceExt.Application.Contracts.Observability;

public sealed record AcceptedVotesMonitorSnapshot(
    DateTime GeneratedAtUtc,
    DateTime? FromUtc,
    DateTime? ToUtc,
    string? ShowId,
    int TotalVotes,
    int TotalUniqueUsers,
    IReadOnlyList<AcceptedVotesMonitorCandidateSummary> Candidates,
    AcceptedVotesMonitorRecentVotesPage RecentVotesPage);

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

public sealed record AcceptedVotesMonitorRecentVotesPage(
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages,
    IReadOnlyList<AcceptedVotesMonitorRecentVote> Items);
