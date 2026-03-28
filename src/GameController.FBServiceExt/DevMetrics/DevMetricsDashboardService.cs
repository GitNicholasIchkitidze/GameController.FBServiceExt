using GameController.FBServiceExt.Application.Abstractions.Observability;
using GameController.FBServiceExt.Application.Contracts.Observability;

namespace GameController.FBServiceExt.DevMetrics;

public sealed class DevMetricsDashboardService
{
    private readonly IRuntimeMetricsSnapshotReader _runtimeMetricsSnapshotReader;
    private readonly IRabbitMqQueueMetricsReader _rabbitMqQueueMetricsReader;

    public DevMetricsDashboardService(
        IRuntimeMetricsSnapshotReader runtimeMetricsSnapshotReader,
        IRabbitMqQueueMetricsReader rabbitMqQueueMetricsReader)
    {
        _runtimeMetricsSnapshotReader = runtimeMetricsSnapshotReader;
        _rabbitMqQueueMetricsReader = rabbitMqQueueMetricsReader;
    }

    public async ValueTask<RuntimeMetricsDashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshots = await _runtimeMetricsSnapshotReader.ListSnapshotsAsync(cancellationToken);
        var queues = await _rabbitMqQueueMetricsReader.GetQueuesAsync(cancellationToken);

        return new RuntimeMetricsDashboardSnapshot(
            GeneratedAtUtc: DateTime.UtcNow,
            ApiInstances: snapshots.Where(static snapshot => string.Equals(snapshot.ServiceRole, "Api", StringComparison.OrdinalIgnoreCase)).OrderBy(static snapshot => snapshot.InstanceId).ToArray(),
            WorkerInstances: snapshots.Where(static snapshot => string.Equals(snapshot.ServiceRole, "Worker", StringComparison.OrdinalIgnoreCase)).OrderBy(static snapshot => snapshot.InstanceId).ToArray(),
            Queues: queues.OrderBy(static queue => queue.Name).ToArray());
    }
}
