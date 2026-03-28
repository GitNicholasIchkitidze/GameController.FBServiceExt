using GameController.FBServiceExt.Application.Contracts.Observability;

namespace GameController.FBServiceExt.Application.Abstractions.Observability;

public interface IRuntimeMetricsCollector
{
    void Increment(string counterName, long delta = 1);

    void ObserveDuration(string metricName, double milliseconds);

    void ObserveValue(string metricName, double value);

    void SetGauge(string gaugeName, double value);

    RuntimeMetricsSnapshot CreateSnapshot();
}
