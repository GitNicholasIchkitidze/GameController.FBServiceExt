using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GameController.FBServiceExt.Options;
using Microsoft.Extensions.Options;

namespace GameController.FBServiceExt.DevLogs;

public sealed class GraylogLogCleanupService
{
    private const int IndexSetListLimit = 500;
    private static readonly TimeSpan DeflectorCycleTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DeflectorPollDelay = TimeSpan.FromMilliseconds(250);

    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<DevLogViewerOptions> _optionsMonitor;
    private readonly ILogger<GraylogLogCleanupService> _logger;

    public GraylogLogCleanupService(
        HttpClient httpClient,
        IOptionsMonitor<DevLogViewerOptions> optionsMonitor,
        ILogger<GraylogLogCleanupService> logger)
    {
        _httpClient = httpClient;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    public async Task<GraylogLogCleanupSummary> ClearLogsAsync(CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;
        var indexSetIds = await GetIndexSetIdsAsync(options, cancellationToken);
        var cycledIndexSets = 0;
        var deletedIndices = 0;

        foreach (var indexSetId in indexSetIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var previousTarget = await GetCurrentTargetAsync(options, indexSetId, cancellationToken);
            await CycleDeflectorAsync(options, indexSetId, cancellationToken);
            cycledIndexSets++;

            var currentTarget = await WaitForCurrentTargetAsync(options, indexSetId, previousTarget, cancellationToken);
            var openIndices = await GetOpenIndexNamesAsync(options, indexSetId, cancellationToken);

            foreach (var indexName in openIndices)
            {
                if (string.Equals(indexName, currentTarget, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (await TryDeleteIndexAsync(options, indexName, cancellationToken))
                {
                    deletedIndices++;
                }
            }
        }

        return new GraylogLogCleanupSummary(indexSetIds.Count, cycledIndexSets, deletedIndices);
    }

    private async Task<IReadOnlyList<string>> GetIndexSetIdsAsync(DevLogViewerOptions options, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, BuildRequestUri(options.GraylogBaseUrl, $"/api/system/indices/index_sets?skip=0&limit={IndexSetListLimit}&stats=false"), options);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken, "listing Graylog index sets");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("index_sets", out var indexSetsElement) || indexSetsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        foreach (var indexSetElement in indexSetsElement.EnumerateArray())
        {
            if (!indexSetElement.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var id = idElement.GetString();
            if (!string.IsNullOrWhiteSpace(id))
            {
                result.Add(id);
            }
        }

        return result;
    }

    private async Task<string> GetCurrentTargetAsync(DevLogViewerOptions options, string indexSetId, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, BuildRequestUri(options.GraylogBaseUrl, $"/api/system/deflector/{Uri.EscapeDataString(indexSetId)}"), options);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken, $"reading Graylog deflector for index set '{indexSetId}'");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("current_target", out var currentTargetElement) || currentTargetElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"Graylog deflector response did not include current_target for index set '{indexSetId}'.");
        }

        return currentTargetElement.GetString() ?? string.Empty;
    }

    private async Task CycleDeflectorAsync(DevLogViewerOptions options, string indexSetId, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, BuildRequestUri(options.GraylogBaseUrl, $"/api/system/deflector/{Uri.EscapeDataString(indexSetId)}/cycle"), options);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken, $"cycling Graylog deflector for index set '{indexSetId}'");
    }

    private async Task<string> WaitForCurrentTargetAsync(DevLogViewerOptions options, string indexSetId, string previousTarget, CancellationToken cancellationToken)
    {
        var deadlineUtc = DateTime.UtcNow + DeflectorCycleTimeout;

        while (DateTime.UtcNow <= deadlineUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentTarget = await GetCurrentTargetAsync(options, indexSetId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(currentTarget) && !string.Equals(currentTarget, previousTarget, StringComparison.OrdinalIgnoreCase))
            {
                return currentTarget;
            }

            await Task.Delay(DeflectorPollDelay, cancellationToken);
        }

        throw new TimeoutException($"Graylog deflector did not rotate within {DeflectorCycleTimeout.TotalSeconds:0} seconds for index set '{indexSetId}'.");
    }

    private async Task<IReadOnlyList<string>> GetOpenIndexNamesAsync(DevLogViewerOptions options, string indexSetId, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, BuildRequestUri(options.GraylogBaseUrl, $"/api/system/indexer/indices/{Uri.EscapeDataString(indexSetId)}/open"), options);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken, $"listing Graylog indices for index set '{indexSetId}'");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("indices", out var indicesElement) || indicesElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        foreach (var indexElement in indicesElement.EnumerateArray())
        {
            if (!indexElement.TryGetProperty("index_name", out var indexNameElement) || indexNameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var indexName = indexNameElement.GetString();
            if (!string.IsNullOrWhiteSpace(indexName))
            {
                result.Add(indexName);
            }
        }

        return result;
    }

    private async Task<bool> TryDeleteIndexAsync(DevLogViewerOptions options, string indexName, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Delete, BuildRequestUri(options.GraylogBaseUrl, $"/api/system/indexer/indices/{Uri.EscapeDataString(indexName)}"), options);
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, cancellationToken, $"deleting Graylog index '{indexName}'");
        return true;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string requestUri, DevLogViewerOptions options)
    {
        var request = new HttpRequestMessage(method, requestUri);
        var authBytes = Encoding.ASCII.GetBytes($"{options.Username}:{options.Password}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
        request.Headers.Add("X-Requested-By", "fbserviceext-graylog-cleanup");
        return request;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken, string operation)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogWarning(
            "Graylog cleanup request failed while {Operation}. StatusCode: {StatusCode}, Payload: {Payload}",
            operation,
            (int)response.StatusCode,
            payload);

        response.EnsureSuccessStatusCode();
    }

    private static string BuildRequestUri(string baseUrl, string relativePath)
    {
        var trimmedBaseUrl = baseUrl.TrimEnd('/');
        return $"{trimmedBaseUrl}{relativePath}";
    }
}

public sealed record GraylogLogCleanupSummary(int IndexSetsDiscovered, int IndexSetsCycled, int DeletedIndices);
