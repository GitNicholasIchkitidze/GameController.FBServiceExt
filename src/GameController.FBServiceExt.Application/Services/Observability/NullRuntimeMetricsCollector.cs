using GameController.FBServiceExt.Application.Abstractions.Observability;
using GameController.FBServiceExt.Application.Contracts.Observability;

namespace GameController.FBServiceExt.Application.Services.Observability;

public sealed class NullRuntimeMetricsCollector : IRuntimeMetricsCollector
{
    private static readonly RuntimeMetricsSnapshot EmptySnapshot = new(
        ServiceRole: "None",
        InstanceId: "none",
        MachineName: Environment.MachineName,
        EnvironmentName: "Unknown",
        ProcessId: Environment.ProcessId,
        UpdatedAtUtc: DateTime.UtcNow,
        Counters: new Dictionary<string, long>(),
        Gauges: new Dictionary<string, double>(),
        Distributions: new Dictionary<string, MetricDistributionSnapshot>());

    public RuntimeMetricsSnapshot CreateSnapshot() => EmptySnapshot;

    public void Increment(string counterName, long delta = 1)
    {
    }

    public void ObserveDuration(string metricName, double milliseconds)
    {
    }

    public void ObserveValue(string metricName, double value)
    {
    }

    public void SetGauge(string gaugeName, double value)
    {
    }
}
