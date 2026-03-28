using System.Net.Http.Headers;
using System.Text.Json;
using GameController.FBServiceExt.Application.Abstractions.Observability;
using GameController.FBServiceExt.Application.Contracts.Observability;
using GameController.FBServiceExt.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace GameController.FBServiceExt.Infrastructure.Observability;

internal sealed class RabbitMqQueueMetricsReader : IRabbitMqQueueMetricsReader
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<RabbitMqOptions> _optionsMonitor;

    public RabbitMqQueueMetricsReader(HttpClient httpClient, IOptionsMonitor<RabbitMqOptions> optionsMonitor)
    {
        _httpClient = httpClient;
        _optionsMonitor = optionsMonitor;
    }

    public async ValueTask<IReadOnlyList<RabbitMqQueueMetricsSnapshot>> GetQueuesAsync(CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;
        var queueNames = new[] { options.RawIngressQueueName, options.NormalizedEventQueueName };
        var snapshots = new List<RabbitMqQueueMetricsSnapshot>(queueNames.Length);

        using var request = new HttpRequestMessage();
        var auth = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{options.UserName}:{options.Password}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);

        foreach (var queueName in queueNames)
        {
            var uri = BuildQueueUri(options, queueName);
            using var response = await _httpClient.GetAsync(uri, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var messageStats = root.TryGetProperty("message_stats", out var stats) ? stats : default;

            snapshots.Add(new RabbitMqQueueMetricsSnapshot(
                Name: GetString(root, "name") ?? queueName,
                Consumers: (int)GetLong(root, "consumers"),
                Messages: GetLong(root, "messages"),
                Ready: GetLong(root, "messages_ready"),
                Unacknowledged: GetLong(root, "messages_unacknowledged"),
                PublishCount: GetLong(messageStats, "publish"),
                DeliverGetCount: GetLong(messageStats, "deliver_get"),
                AckCount: GetLong(messageStats, "ack"),
                PublishRate: GetRate(messageStats, "publish_details"),
                DeliverGetRate: GetRate(messageStats, "deliver_get_details"),
                AckRate: GetRate(messageStats, "ack_details")));
        }

        return snapshots;
    }

    private static Uri BuildQueueUri(RabbitMqOptions options, string queueName)
    {
        var baseUrl = string.IsNullOrWhiteSpace(options.ManagementApiBaseUrl)
            ? "http://127.0.0.1:15672/api"
            : options.ManagementApiBaseUrl.TrimEnd('/');
        var vhost = Uri.EscapeDataString(options.VirtualHost);
        var queue = Uri.EscapeDataString(queueName);
        return new Uri($"{baseUrl}/queues/{vhost}/{queue}", UriKind.Absolute);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.ValueKind != JsonValueKind.Undefined &&
               element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static long GetLong(JsonElement element, string propertyName)
    {
        return element.ValueKind != JsonValueKind.Undefined &&
               element.TryGetProperty(propertyName, out var property) &&
               property.TryGetInt64(out var value)
            ? value
            : 0;
    }

    private static double GetRate(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Undefined ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        return property.TryGetProperty("rate", out var rate) && rate.TryGetDouble(out var value)
            ? value
            : 0;
    }
}
