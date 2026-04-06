using GameController.FBServiceExt.Application.Contracts.Observability;

namespace GameController.FBServiceExt.Application.Abstractions.Observability;

public interface IAcceptedVotesMonitorService
{
    ValueTask<AcceptedVotesMonitorSnapshot> GetSnapshotAsync(
        DateTime? fromUtc,
        DateTime? toUtc,
        string? showId,
        string? userFilter,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}
