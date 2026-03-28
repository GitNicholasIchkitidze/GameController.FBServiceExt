namespace GameController.FBServiceExt.Application.Contracts.Observability;

public sealed record MetricDistributionSnapshot(
    long SampleCount,
    double Average,
    double P50,
    double P95,
    double P99,
    double Max);

public sealed record RuntimeMetricsSnapshot(
    string ServiceRole,
    string InstanceId,
    string MachineName,
    string EnvironmentName,
    int ProcessId,
    DateTime UpdatedAtUtc,
    Dictionary<string, long> Counters,
    Dictionary<string, double> Gauges,
    Dictionary<string, MetricDistributionSnapshot> Distributions);

public sealed record RabbitMqQueueMetricsSnapshot(
    string Name,
    int Consumers,
    long Messages,
    long Ready,
    long Unacknowledged,
    long PublishCount,
    long DeliverGetCount,
    long AckCount,
    double PublishRate,
    double DeliverGetRate,
    double AckRate);

public sealed record RuntimeMetricsDashboardSnapshot(
    DateTime GeneratedAtUtc,
    IReadOnlyList<RuntimeMetricsSnapshot> ApiInstances,
    IReadOnlyList<RuntimeMetricsSnapshot> WorkerInstances,
    IReadOnlyList<RabbitMqQueueMetricsSnapshot> Queues);
