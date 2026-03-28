using GameController.FBServiceExt.Application.Abstractions.Observability;
using GameController.FBServiceExt.Application.Contracts.Observability;

namespace GameController.FBServiceExt.Application.Services.Observability;

public sealed class NullRuntimeMetricsSnapshotReader : IRuntimeMetricsSnapshotReader
{
    public ValueTask<IReadOnlyList<RuntimeMetricsSnapshot>> ListSnapshotsAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<RuntimeMetricsSnapshot>>(Array.Empty<RuntimeMetricsSnapshot>());
}
