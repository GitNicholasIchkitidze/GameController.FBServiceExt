using System.Collections.Concurrent;
using System.Diagnostics;
using GameController.FBServiceExt.Application.Abstractions.Observability;
using GameController.FBServiceExt.Application.Contracts.Observability;
using Microsoft.Extensions.Hosting;

namespace GameController.FBServiceExt.Infrastructure.Observability;

internal sealed class InMemoryRuntimeMetricsCollector : IRuntimeMetricsCollector
{
    private readonly ConcurrentDictionary<string, long> _counters = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, double> _gauges = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DistributionWindow> _distributions = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _serviceRole;
    private readonly string _instanceId;
    private readonly string _machineName;
    private readonly string _environmentName;
    private readonly int _processId;

    public InMemoryRuntimeMetricsCollector(IHostEnvironment hostEnvironment)
    {
        _environmentName = hostEnvironment.EnvironmentName;
        _machineName = Environment.MachineName;
        _processId = Environment.ProcessId;
        var processName = Process.GetCurrentProcess().ProcessName;
        _serviceRole = processName.EndsWith(".Worker", StringComparison.OrdinalIgnoreCase)
            ? "Worker"
            : "Api";
        _instanceId = $"{_machineName}:{_serviceRole}:{_processId}";
    }

    public void Increment(string counterName, long delta = 1)
    {
        _counters.AddOrUpdate(counterName, delta, (_, current) => current + delta);
    }

    public void ObserveDuration(string metricName, double milliseconds)
    {
        ObserveValue(metricName, milliseconds);
    }

    public void ObserveValue(string metricName, double value)
    {
        var window = _distributions.GetOrAdd(metricName, static _ => new DistributionWindow());
        window.Add(value);
    }

    public void SetGauge(string gaugeName, double value)
    {
        _gauges.AddOrUpdate(gaugeName, value, (_, _) => value);
    }

    public RuntimeMetricsSnapshot CreateSnapshot()
    {
        var counters = _counters.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var gauges = _gauges.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var distributions = _distributions.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToSnapshot(),
            StringComparer.OrdinalIgnoreCase);

        return new RuntimeMetricsSnapshot(
            ServiceRole: _serviceRole,
            InstanceId: _instanceId,
            MachineName: _machineName,
            EnvironmentName: _environmentName,
            ProcessId: _processId,
            UpdatedAtUtc: DateTime.UtcNow,
            Counters: counters,
            Gauges: gauges,
            Distributions: distributions);
    }

    private sealed class DistributionWindow
    {
        private const int Capacity = 512;
        private readonly object _gate = new();
        private readonly Queue<double> _samples = new(Capacity);

        public void Add(double value)
        {
            lock (_gate)
            {
                if (_samples.Count >= Capacity)
                {
                    _samples.Dequeue();
                }

                _samples.Enqueue(value);
            }
        }

        public MetricDistributionSnapshot ToSnapshot()
        {
            lock (_gate)
            {
                if (_samples.Count == 0)
                {
                    return new MetricDistributionSnapshot(0, 0, 0, 0, 0, 0);
                }

                var values = _samples.ToArray();
                Array.Sort(values);

                return new MetricDistributionSnapshot(
                    SampleCount: values.Length,
                    Average: values.Average(),
                    P50: Percentile(values, 0.50),
                    P95: Percentile(values, 0.95),
                    P99: Percentile(values, 0.99),
                    Max: values[^1]);
            }
        }

        private static double Percentile(double[] sortedValues, double percentile)
        {
            if (sortedValues.Length == 0)
            {
                return 0;
            }

            var index = (int)Math.Ceiling(percentile * sortedValues.Length) - 1;
            index = Math.Clamp(index, 0, sortedValues.Length - 1);
            return sortedValues[index];
        }
    }
}
