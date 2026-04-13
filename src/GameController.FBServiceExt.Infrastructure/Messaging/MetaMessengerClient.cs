using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameController.FBServiceExt.Application.Abstractions.Messaging;
using GameController.FBServiceExt.Application.Abstractions.Observability;
using GameController.FBServiceExt.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameController.FBServiceExt.Infrastructure.Messaging;

internal sealed class MetaMessengerClient : IOutboundMessengerClient
{
    private const string DefaultGraphApiBaseUrl = "https://graph.facebook.com";
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<MetaMessengerOptions> _optionsMonitor;
    private readonly IRuntimeMetricsCollector _runtimeMetricsCollector;
    private readonly ILogger<MetaMessengerClient> _logger;

    public MetaMessengerClient(
        HttpClient httpClient,
        IOptionsMonitor<MetaMessengerOptions> optionsMonitor,
        IRuntimeMetricsCollector runtimeMetricsCollector,
        ILogger<MetaMessengerClient> logger)
    {
        _httpClient = httpClient;
        _optionsMonitor = optionsMonitor;
        _runtimeMetricsCollector = runtimeMetricsCollector;
        _logger = logger;
    }

    public ValueTask<bool> SendTextAsync(string recipientId, string messageText, CancellationToken cancellationToken)
    {
        var payload = new
        {
            messaging_type = "RESPONSE",
            recipient = new { id = recipientId },
            message = new { text = messageText }
        };

        return SendAsync(recipientId, payload, cancellationToken);
    }

    public ValueTask<bool> SendButtonTemplateAsync(
        string recipientId,
        string promptText,
        IReadOnlyCollection<MessengerPostbackButton> buttons,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            messaging_type = "RESPONSE",
            recipient = new { id = recipientId },
            message = new
            {
                attachment = new
                {
                    type = "template",
                    payload = new
                    {
                        template_type = "button",
                        text = promptText,
                        buttons = buttons.Select(ToButtonPayload).ToArray()
                    }
                }
            }
        };

        return SendAsync(recipientId, payload, cancellationToken);
    }

    public ValueTask<bool> SendGenericTemplateAsync(
        string recipientId,
        IReadOnlyCollection<MessengerGenericTemplateElement> elements,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            messaging_type = "RESPONSE",
            recipient = new { id = recipientId },
            message = new
            {
                attachment = new
                {
                    type = "template",
                    payload = new
                    {
                        template_type = "generic",
                        elements = elements.Select(ToGenericElementPayload).ToArray()
                    }
                }
            }
        };

        return SendAsync(recipientId, payload, cancellationToken);
    }

    // outbound transport-ის ბოლო წერტილი.
    // აქ ტექსტი ან template რეალურად იგზავნება Meta Graph API-ზე ან simulator fake-meta endpoint-ზე.
    private async ValueTask<bool> SendAsync(string recipientId, object payload, CancellationToken cancellationToken)
    {
        _runtimeMetricsCollector.Increment("worker.outbound.messenger.attempts");

        var options = _optionsMonitor.CurrentValue;
        if (!options.Enabled)
        {
            _runtimeMetricsCollector.Increment("worker.outbound.messenger.skipped_disabled");
            _logger.LogDebug(
                "Outbound Messenger send skipped because MetaMessenger is disabled. RecipientId: {RecipientId}",
                recipientId);
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.PageAccessToken))
        {
            _runtimeMetricsCollector.Increment("worker.outbound.messenger.skipped_missing_token");
            _logger.LogWarning(
                "Outbound Messenger send skipped because PageAccessToken is not configured. RecipientId: {RecipientId}",
                recipientId);
            return false;
        }

        var version = string.IsNullOrWhiteSpace(options.GraphApiVersion)
            ? "v24.0"
            : options.GraphApiVersion.Trim();
        var resolvedBaseUrl = ResolveBaseUrl(options, recipientId);
        if (IsSimulatorRecipientId(recipientId) && !string.Equals(resolvedBaseUrl, NormalizeBaseUrl(options.GraphApiBaseUrl), StringComparison.OrdinalIgnoreCase))
        {
            _runtimeMetricsCollector.Increment("worker.outbound.messenger.simulator_recipient_local_reroute");
            _logger.LogInformation(
                "Rerouting simulated recipient to local fake Messenger endpoint. RecipientId: {RecipientId}, TargetBaseUrl: {TargetBaseUrl}",
                recipientId,
                resolvedBaseUrl);
        }

        var requestUri = $"{resolvedBaseUrl}/{version}/me/messages?access_token={Uri.EscapeDataString(options.PageAccessToken)}";
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await PostWithSimulatorRetryAsync(requestUri, payload, IsSimulatorRecipientId(recipientId) && IsLoopbackBaseUrl(resolvedBaseUrl), cancellationToken);
            var elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
            RecordHttpMetrics(response.StatusCode, elapsedMs);

            if (response.IsSuccessStatusCode)
            {
                _runtimeMetricsCollector.Increment("worker.outbound.messenger.transport_success");
                return true;
            }

            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            RecordFailureMetrics(response.StatusCode, responseText);
            _logger.LogWarning(
                "Messenger send failed. RecipientId: {RecipientId}, StatusCode: {StatusCode}, Response: {Response}",
                recipientId,
                (int)response.StatusCode,
                responseText);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            RecordTimeoutMetrics(stopwatch.Elapsed.TotalMilliseconds);
            _logger.LogWarning(
                exception,
                "Messenger send timed out. RecipientId: {RecipientId}",
                recipientId);
            return false;
        }
        catch (Exception exception)
        {
            RecordExceptionMetrics(stopwatch.Elapsed.TotalMilliseconds, exception);
            _logger.LogWarning(
                exception,
                "Messenger send threw an exception. RecipientId: {RecipientId}",
                recipientId);
            return false;
        }
    }


    private async Task<HttpResponseMessage> PostWithSimulatorRetryAsync(string requestUri, object payload, bool useSimulatorRetry, CancellationToken cancellationToken)
    {
        if (!useSimulatorRetry)
        {
            return await _httpClient.PostAsJsonAsync(requestUri, payload, cancellationToken).ConfigureAwait(false);
        }

        const int maxAttempts = 6;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await _httpClient.PostAsJsonAsync(requestUri, payload, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                _runtimeMetricsCollector.Increment("worker.outbound.messenger.simulator_local_retry");
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
        }

        return await _httpClient.PostAsJsonAsync(requestUri, payload, cancellationToken).ConfigureAwait(false);
    }
    internal static bool IsSimulatorRecipientId(string recipientId)
        => !string.IsNullOrWhiteSpace(recipientId) && recipientId.StartsWith("simulate-user-", StringComparison.OrdinalIgnoreCase);

    internal static string ResolveBaseUrl(MetaMessengerOptions options, string recipientId)
    {
        ArgumentNullException.ThrowIfNull(options);

        var configuredBaseUrl = NormalizeBaseUrl(options.GraphApiBaseUrl);
        if (!IsSimulatorRecipientId(recipientId))
        {
            return configuredBaseUrl;
        }

        if (IsLoopbackBaseUrl(configuredBaseUrl))
        {
            return configuredBaseUrl;
        }

        return NormalizeSimulatorBaseUrl(options.SimulatorGraphApiBaseUrl);
    }

    private static string NormalizeBaseUrl(string? baseUrl)
    {
        return string.IsNullOrWhiteSpace(baseUrl)
            ? DefaultGraphApiBaseUrl
            : baseUrl.Trim().TrimEnd('/');
    }

    private static string NormalizeSimulatorBaseUrl(string? baseUrl)
    {
        return string.IsNullOrWhiteSpace(baseUrl)
            ? MetaMessengerOptions.DefaultSimulatorGraphApiBaseUrl
            : baseUrl.Trim().TrimEnd('/');
    }

    private static bool IsLoopbackBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(uri.Host, out var ipAddress) && IPAddress.IsLoopback(ipAddress);
    }

    private void RecordHttpMetrics(HttpStatusCode statusCode, double elapsedMs)
    {
        var status = (int)statusCode;
        _runtimeMetricsCollector.ObserveDuration("worker.outbound.messenger.http_ms", elapsedMs);
        _runtimeMetricsCollector.SetGauge("worker.outbound.messenger.last_http_status", status);
        _runtimeMetricsCollector.Increment($"worker.outbound.messenger.http_status.{status}");

        if (status >= 200 && status < 300)
        {
            _runtimeMetricsCollector.Increment("worker.outbound.messenger.http_2xx");
            return;
        }

        if (status >= 400 && status < 500)
        {
            _runtimeMetricsCollector.Increment("worker.outbound.messenger.http_4xx");
            if (status == 429)
            {
                _runtimeMetricsCollector.Increment("worker.outbound.messenger.http_429");
                _runtimeMetricsCollector.Increment("worker.outbound.messenger.rate_limited");
            }

            return;
        }

        if (status >= 500)
        {
            _runtimeMetricsCollector.Increment("worker.outbound.messenger.http_5xx");
        }
    }

    private void RecordFailureMetrics(HttpStatusCode statusCode, string responseText)
    {
        _runtimeMetricsCollector.Increment("worker.outbound.messenger.transport_failures");

        if (TryParseGraphError(responseText, out var graphError))
        {
            if (graphError.Code.HasValue)
            {
                _runtimeMetricsCollector.SetGauge("worker.outbound.messenger.last_graph_error_code", graphError.Code.Value);
            }

            if (graphError.Subcode.HasValue)
            {
                _runtimeMetricsCollector.SetGauge("worker.outbound.messenger.last_graph_error_subcode", graphError.Subcode.Value);
            }

            if (LooksLikePolicyDenial(statusCode, graphError))
            {
                _runtimeMetricsCollector.Increment("worker.outbound.messenger.policy_denied");
            }
        }
    }

    private void RecordTimeoutMetrics(double elapsedMs)
    {
        _runtimeMetricsCollector.ObserveDuration("worker.outbound.messenger.http_ms", elapsedMs);
        _runtimeMetricsCollector.Increment("worker.outbound.messenger.timeouts");
        _runtimeMetricsCollector.Increment("worker.outbound.messenger.transport_failures");
    }

    private void RecordExceptionMetrics(double elapsedMs, Exception exception)
    {
        _runtimeMetricsCollector.ObserveDuration("worker.outbound.messenger.http_ms", elapsedMs);
        _runtimeMetricsCollector.Increment("worker.outbound.messenger.transport_failures");
        _runtimeMetricsCollector.Increment("worker.outbound.messenger.transport_exceptions");
        _runtimeMetricsCollector.Increment($"worker.outbound.messenger.exception_type.{SanitizeMetricSegment(exception.GetType().Name)}");
    }

    private static bool TryParseGraphError(string responseText, out GraphErrorInfo error)
    {
        error = default;
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(responseText);
            if (!document.RootElement.TryGetProperty("error", out var errorElement))
            {
                return false;
            }

            int? code = null;
            if (errorElement.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsedCode))
            {
                code = parsedCode;
            }

            int? subcode = null;
            if (errorElement.TryGetProperty("error_subcode", out var subcodeElement) && subcodeElement.TryGetInt32(out var parsedSubcode))
            {
                subcode = parsedSubcode;
            }

            string? message = null;
            if (errorElement.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String)
            {
                message = messageElement.GetString();
            }

            string? type = null;
            if (errorElement.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String)
            {
                type = typeElement.GetString();
            }

            error = new GraphErrorInfo(code, subcode, type, message);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool LooksLikePolicyDenial(HttpStatusCode statusCode, GraphErrorInfo error)
    {
        if (statusCode != HttpStatusCode.BadRequest && statusCode != HttpStatusCode.Forbidden)
        {
            return false;
        }

        var type = error.Type ?? string.Empty;
        var message = error.Message ?? string.Empty;
        return type.Contains("OAuth", StringComparison.OrdinalIgnoreCase)
            || message.Contains("policy", StringComparison.OrdinalIgnoreCase)
            || message.Contains("window", StringComparison.OrdinalIgnoreCase)
            || message.Contains("permission", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeMetricSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        Span<char> buffer = stackalloc char[value.Length];
        var index = 0;
        foreach (var character in value)
        {
            buffer[index++] = char.IsLetterOrDigit(character)
                ? char.ToLowerInvariant(character)
                : '_';
        }

        return new string(buffer[..index]);
    }

    private static MessengerButtonPayload ToButtonPayload(MessengerPostbackButton button)
    {
        return new MessengerButtonPayload
        {
            Type = "postback",
            Title = button.Title,
            Payload = button.Payload
        };
    }

    private static MessengerGenericElementPayload ToGenericElementPayload(MessengerGenericTemplateElement element)
    {
        return new MessengerGenericElementPayload
        {
            Title = element.Title,
            Subtitle = string.IsNullOrWhiteSpace(element.Subtitle) ? null : element.Subtitle,
            ImageUrl = string.IsNullOrWhiteSpace(element.ImageUrl) ? null : element.ImageUrl,
            Buttons = element.Buttons.Select(ToButtonPayload).ToArray()
        };
    }

    private readonly record struct GraphErrorInfo(int? Code, int? Subcode, string? Type, string? Message);

    private sealed class MessengerButtonPayload
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; init; } = string.Empty;

        [JsonPropertyName("payload")]
        public string Payload { get; init; } = string.Empty;
    }

    private sealed class MessengerGenericElementPayload
    {
        [JsonPropertyName("title")]
        public string Title { get; init; } = string.Empty;

        [JsonPropertyName("subtitle")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Subtitle { get; init; }

        [JsonPropertyName("image_url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ImageUrl { get; init; }

        [JsonPropertyName("buttons")]
        public MessengerButtonPayload[] Buttons { get; init; } = Array.Empty<MessengerButtonPayload>();
    }
}

