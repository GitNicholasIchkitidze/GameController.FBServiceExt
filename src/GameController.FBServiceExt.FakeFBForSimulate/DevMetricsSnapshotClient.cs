using System.Globalization;
using System.Text.Json;

namespace GameController.FBServiceExt.FakeFBForSimulate;

internal sealed class DevMetricsSnapshotClient : IDisposable
{
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public async Task<IReadOnlyList<WorkerInstanceLoadSnapshot>> GetWorkerSnapshotsAsync(string webhookUrl, IReadOnlySet<int> managedWorkerProcessIds, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var webhookUri))
        {
            return Array.Empty<WorkerInstanceLoadSnapshot>();
        }

        var metricsUri = new Uri($"{webhookUri.Scheme}://{webhookUri.Authority}/dev/metrics/api");
        await using var stream = await _httpClient.GetStreamAsync(metricsUri, cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("workerInstances", out var workerInstances) || workerInstances.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<WorkerInstanceLoadSnapshot>();
        }

        var results = new List<WorkerInstanceLoadSnapshot>();
        foreach (var worker in workerInstances.EnumerateArray())
        {
            var processId = GetInt(worker, "processId");
            results.Add(new WorkerInstanceLoadSnapshot(
                GetString(worker, "instanceId"),
                processId,
                GetString(worker, "environmentName"),
                GetDateTimeOffset(worker, "updatedAtUtc"),
                GetCounter(worker, "worker.raw.envelopes_received"),
                GetCounter(worker, "worker.processor.events_seen"),
                GetCounter(worker, "worker.processor.options_sent"),
                GetCounter(worker, "worker.processor.vote_accepted"),
                GetCounter(worker, "worker.processor.ignored"),
                GetDistribution(worker, "worker.normalized.cycle_ms", "p95"),
                GetDistribution(worker, "worker.outbound.messenger.http_ms", "p95"),
                managedWorkerProcessIds.Contains(processId)));
        }

        return results
            .OrderByDescending(static worker => worker.IsManaged)
            .ThenBy(static worker => worker.ProcessId)
            .ToArray();
    }

    public void Dispose() => _httpClient.Dispose();

    private static long GetCounter(JsonElement worker, string key)
    {
        if (worker.TryGetProperty("counters", out var counters) && counters.ValueKind == JsonValueKind.Object && counters.TryGetProperty(key, out var value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.Number => value.TryGetInt64(out var result) ? result : (long)value.GetDouble(),
                JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => 0
            };
        }

        return 0;
    }

    private static double GetDistribution(JsonElement worker, string key, string field)
    {
        if (worker.TryGetProperty("distributions", out var distributions) && distributions.ValueKind == JsonValueKind.Object &&
            distributions.TryGetProperty(key, out var distribution) && distribution.ValueKind == JsonValueKind.Object &&
            distribution.TryGetProperty(field, out var value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.Number => value.GetDouble(),
                JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => 0d
            };
        }

        return 0d;
    }

    private static string GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : 0;

    private static DateTimeOffset? GetDateTimeOffset(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }
}

internal sealed record WorkerInstanceLoadSnapshot(
    string InstanceId,
    int ProcessId,
    string EnvironmentName,
    DateTimeOffset? UpdatedAtUtc,
    long RawEnvelopesReceived,
    long EventsSeen,
    long OptionsSent,
    long VotesAccepted,
    long Ignored,
    double NormalizedCycleP95Milliseconds,
    double OutboundHttpP95Milliseconds,
    bool IsManaged);

