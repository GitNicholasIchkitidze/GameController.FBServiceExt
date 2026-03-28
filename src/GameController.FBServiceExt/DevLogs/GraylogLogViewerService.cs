using System.Net.Http.Headers;
using System.Text;
using GameController.FBServiceExt.Options;
using Microsoft.Extensions.Options;

namespace GameController.FBServiceExt.DevLogs;

public sealed class GraylogLogViewerService
{
    private const string Fields = "timestamp,message,level,source,Application,ServiceRole,SourceContext,RequestPath,CallerTypeName,CallerMemberName,CallerLineNumber";

    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<DevLogViewerOptions> _optionsMonitor;
    private readonly ILogger<GraylogLogViewerService> _logger;

    public GraylogLogViewerService(
        HttpClient httpClient,
        IOptionsMonitor<DevLogViewerOptions> optionsMonitor,
        ILogger<GraylogLogViewerService> logger)
    {
        _httpClient = httpClient;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    public async Task<DevLogSearchResult> SearchAsync(string? query, int? limit, CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;
        var effectiveQuery = string.IsNullOrWhiteSpace(query) ? options.DefaultQuery : query.Trim();
        var effectiveLimit = Math.Clamp(limit ?? options.DefaultLimit, 1, Math.Max(1, options.MaxLimit));

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildRequestUri(options.GraylogBaseUrl, effectiveQuery, effectiveLimit));
        var authBytes = Encoding.ASCII.GetBytes($"{options.Username}:{options.Password}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
        request.Headers.Add("X-Requested-By", "fbserviceext-dev-log-viewer");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Dev log viewer Graylog query failed. StatusCode: {StatusCode}, Query: {Query}, Payload: {Payload}",
                (int)response.StatusCode,
                effectiveQuery,
                payload);

            response.EnsureSuccessStatusCode();
        }

        var entries = GraylogSearchResponseParser.Parse(payload);
        return new DevLogSearchResult(effectiveQuery, effectiveLimit, DateTime.UtcNow, entries);
    }

    private static string BuildRequestUri(string baseUrl, string query, int limit)
    {
        var trimmedBaseUrl = baseUrl.TrimEnd('/');
        return $"{trimmedBaseUrl}/api/search/messages?query={Uri.EscapeDataString(query)}&limit={limit}&offset=0&fields={Uri.EscapeDataString(Fields)}";
    }
}