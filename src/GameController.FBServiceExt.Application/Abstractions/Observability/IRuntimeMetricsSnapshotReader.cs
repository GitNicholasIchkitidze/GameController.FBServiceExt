using GameController.FBServiceExt.Application.Contracts.Observability;

namespace GameController.FBServiceExt.Application.Abstractions.Observability;

public interface IRuntimeMetricsSnapshotReader
{
    ValueTask<IReadOnlyList<RuntimeMetricsSnapshot>> ListSnapshotsAsync(CancellationToken cancellationToken);
}
