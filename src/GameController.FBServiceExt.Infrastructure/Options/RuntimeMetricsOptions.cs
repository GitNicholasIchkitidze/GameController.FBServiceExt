namespace GameController.FBServiceExt.Infrastructure.Options;

public sealed class RuntimeMetricsOptions
{
    public const string SectionName = "RuntimeMetrics";

    public bool Enabled { get; set; }

    public int FlushIntervalMilliseconds { get; set; } = 2000;

    public int SnapshotTtlSeconds { get; set; } = 15;
}
