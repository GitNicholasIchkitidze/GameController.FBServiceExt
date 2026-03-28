using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GameController.FBServiceExt.Application.Abstractions.Ingress;
using GameController.FBServiceExt.Application.Abstractions.Observability;
using GameController.FBServiceExt.Application.Abstractions.Security;
using GameController.FBServiceExt.Application.Contracts.Ingress;
using GameController.FBServiceExt.Application.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GameController.FBServiceExt.Controllers;

[ApiController]
[Route("api/facebook/webhooks")]
public sealed class FacebookWebhooksController : ControllerBase
{
    private static int _inflightRequests;

    private readonly IWebhookIngressService _webhookIngressService;
    private readonly IWebhookSignatureValidator _webhookSignatureValidator;
    private readonly IOptionsMonitor<WebhookIngressOptions> _ingressOptionsMonitor;
    private readonly IOptionsMonitor<MetaWebhookOptions> _metaWebhookOptionsMonitor;
    private readonly IRuntimeMetricsCollector _runtimeMetricsCollector;
    private readonly ILogger<FacebookWebhooksController> _logger;

    public FacebookWebhooksController(
        IWebhookIngressService webhookIngressService,
        IWebhookSignatureValidator webhookSignatureValidator,
        IOptionsMonitor<WebhookIngressOptions> ingressOptionsMonitor,
        IOptionsMonitor<MetaWebhookOptions> metaWebhookOptionsMonitor,
        IRuntimeMetricsCollector runtimeMetricsCollector,
        ILogger<FacebookWebhooksController> logger)
    {
        _webhookIngressService = webhookIngressService;
        _webhookSignatureValidator = webhookSignatureValidator;
        _ingressOptionsMonitor = ingressOptionsMonitor;
        _metaWebhookOptionsMonitor = metaWebhookOptionsMonitor;
        _runtimeMetricsCollector = runtimeMetricsCollector;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Verify(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        var options = _metaWebhookOptionsMonitor.CurrentValue;

        if (!string.Equals(mode, "subscribe", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Webhook verification rejected because mode was invalid. Mode: {Mode}", mode);
            return BadRequest();
        }

        if (!string.Equals(verifyToken, options.VerifyToken, StringComparison.Ordinal))
        {
            _logger.LogWarning("Webhook verification rejected because verify token did not match. Mode: {Mode}", mode);
            return Forbid();
        }

        _logger.LogInformation("Webhook verification succeeded.");
        return Content(challenge ?? string.Empty, "text/plain", Encoding.UTF8);
    }

    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var statusCode = StatusCodes.Status500InternalServerError;
        var bodyLength = 0;
        var bodyReadMs = 0d;
        var signatureValidationMs = 0d;
        var acceptMs = 0d;
        var stage = "request_start";
        var inflight = Interlocked.Increment(ref _inflightRequests);
        var summary = WebhookPayloadSummary.Empty;

        _runtimeMetricsCollector.SetGauge("api.webhook.inflight", inflight);
        _runtimeMetricsCollector.ObserveValue("api.webhook.inflight_samples", inflight);

        try
        {
            var ingressOptions = _ingressOptionsMonitor.CurrentValue;
            if (Request.ContentLength.HasValue && Request.ContentLength.Value > ingressOptions.MaxRequestBodySizeBytes)
            {
                statusCode = StatusCodes.Status413PayloadTooLarge;
                bodyLength = (int)Math.Min(Request.ContentLength.Value, int.MaxValue);

                _logger.LogWarning(
                    "Webhook request rejected because payload exceeded the configured limit. RequestId: {RequestId}, ContentLength: {ContentLength}, Limit: {Limit}",
                    HttpContext.TraceIdentifier,
                    Request.ContentLength.Value,
                    ingressOptions.MaxRequestBodySizeBytes);

                return StatusCode(statusCode);
            }

            stage = "body_read";
            using var reader = new StreamReader(Request.Body, Encoding.UTF8);
            var bodyReadStopwatch = Stopwatch.StartNew();
            var body = await reader.ReadToEndAsync(cancellationToken);
            bodyReadStopwatch.Stop();
            bodyReadMs = bodyReadStopwatch.Elapsed.TotalMilliseconds;
            _runtimeMetricsCollector.ObserveDuration("api.webhook.body_read_ms", bodyReadMs);

            bodyLength = body.Length;
            summary = SummarizePayload(body);

            stage = "signature_validation";
            var signatureHeader = Request.Headers["X-Hub-Signature-256"].FirstOrDefault();
            var signatureStopwatch = Stopwatch.StartNew();
            var signatureValid = _webhookSignatureValidator.IsValid(body, signatureHeader);
            signatureStopwatch.Stop();
            signatureValidationMs = signatureStopwatch.Elapsed.TotalMilliseconds;
            _runtimeMetricsCollector.ObserveDuration("api.webhook.signature_validation_ms", signatureValidationMs);

            if (!signatureValid)
            {
                statusCode = StatusCodes.Status401Unauthorized;
                _runtimeMetricsCollector.Increment("api.webhook.signature_failures");

                _logger.LogWarning(
                    "Webhook request rejected because signature validation failed. RequestId: {RequestId}, Object: {ObjectType}, Entries: {EntryCount}, MessagingEvents: {MessagingCount}, StandbyEvents: {StandbyCount}",
                    HttpContext.TraceIdentifier,
                    summary.ObjectType,
                    summary.EntryCount,
                    summary.MessagingCount,
                    summary.StandbyCount);
                return Unauthorized();
            }

            _logger.LogDebug(
                "Webhook request accepted. RequestId: {RequestId}, Object: {ObjectType}, Entries: {EntryCount}, MessagingEvents: {MessagingCount}, StandbyEvents: {StandbyCount}, ContentLength: {ContentLength}",
                HttpContext.TraceIdentifier,
                summary.ObjectType,
                summary.EntryCount,
                summary.MessagingCount,
                summary.StandbyCount,
                body.Length);

            var headers = Request.Headers.ToDictionary(
                header => header.Key,
                header => header.Value
                    .Where(static value => value is not null)
                    .Select(static value => value!)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

            var command = new AcceptWebhookCommand(
                HttpContext.TraceIdentifier,
                headers,
                body,
                DateTime.UtcNow);

            stage = "accept_publish";
            var acceptStopwatch = Stopwatch.StartNew();
            await _webhookIngressService.AcceptAsync(command, cancellationToken);
            acceptStopwatch.Stop();
            acceptMs = acceptStopwatch.Elapsed.TotalMilliseconds;
            _runtimeMetricsCollector.ObserveDuration("api.webhook.accept_ms", acceptMs);

            statusCode = StatusCodes.Status200OK;
            return Ok();
        }
        catch (BadHttpRequestException exception)
        {
            var bodyReadInterrupted = string.Equals(stage, "body_read", StringComparison.Ordinal);
            var treatAsClientAbort = bodyReadInterrupted || HttpContext.RequestAborted.IsCancellationRequested;

            statusCode = treatAsClientAbort
                ? 499
                : StatusCodes.Status400BadRequest;

            _runtimeMetricsCollector.Increment("api.webhook.bad_requests");
            _runtimeMetricsCollector.Increment($"api.webhook.stage.{SanitizeMetricSegment(stage)}.bad_request");
            _runtimeMetricsCollector.Increment($"api.webhook.bad_request_type.{SanitizeMetricSegment(exception.GetType().Name)}");

            if (treatAsClientAbort)
            {
                _runtimeMetricsCollector.Increment("api.webhook.client_aborts");

                _logger.LogWarning(
                    exception,
                    "Webhook request body was interrupted before completion. Stage: {Stage}, RequestId: {RequestId}, ElapsedMs: {ElapsedMs}, Inflight: {Inflight}, RequestAborted: {RequestAborted}",
                    stage,
                    HttpContext.TraceIdentifier,
                    stopwatch.Elapsed.TotalMilliseconds,
                    inflight,
                    HttpContext.RequestAborted.IsCancellationRequested);

                return new EmptyResult();
            }

            _logger.LogWarning(
                exception,
                "Webhook request was rejected as bad input. Stage: {Stage}, RequestId: {RequestId}, ElapsedMs: {ElapsedMs}, Inflight: {Inflight}",
                stage,
                HttpContext.TraceIdentifier,
                stopwatch.Elapsed.TotalMilliseconds,
                inflight);

            return BadRequest();
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested || HttpContext.RequestAborted.IsCancellationRequested)
        {
            statusCode = 499;
            _runtimeMetricsCollector.Increment("api.webhook.client_aborts");
            _runtimeMetricsCollector.Increment($"api.webhook.stage.{SanitizeMetricSegment(stage)}.canceled");

            _logger.LogWarning(
                exception,
                "Webhook request canceled. Stage: {Stage}, RequestId: {RequestId}, ElapsedMs: {ElapsedMs}, BodyReadMs: {BodyReadMs}, SignatureValidationMs: {SignatureValidationMs}, AcceptMs: {AcceptMs}, Inflight: {Inflight}, RequestAborted: {RequestAborted}",
                stage,
                HttpContext.TraceIdentifier,
                stopwatch.Elapsed.TotalMilliseconds,
                bodyReadMs,
                signatureValidationMs,
                acceptMs,
                inflight,
                HttpContext.RequestAborted.IsCancellationRequested);

            return new EmptyResult();
        }
        catch (Exception exception)
        {
            _runtimeMetricsCollector.Increment("api.webhook.exceptions");
            _runtimeMetricsCollector.Increment($"api.webhook.stage.{SanitizeMetricSegment(stage)}.exceptions");
            _runtimeMetricsCollector.Increment($"api.webhook.exception_type.{SanitizeMetricSegment(exception.GetType().Name)}");

            _logger.LogError(
                exception,
                "Webhook request failed. Stage: {Stage}, RequestId: {RequestId}, ElapsedMs: {ElapsedMs}, BodyReadMs: {BodyReadMs}, SignatureValidationMs: {SignatureValidationMs}, AcceptMs: {AcceptMs}, BodyBytes: {BodyBytes}, MessagingEvents: {MessagingEvents}, StandbyEvents: {StandbyEvents}, Inflight: {Inflight}",
                stage,
                HttpContext.TraceIdentifier,
                stopwatch.Elapsed.TotalMilliseconds,
                bodyReadMs,
                signatureValidationMs,
                acceptMs,
                bodyLength,
                summary.MessagingCount,
                summary.StandbyCount,
                inflight);

            throw;
        }
        finally
        {
            stopwatch.Stop();
            if (statusCode == StatusCodes.Status200OK && stopwatch.Elapsed.TotalMilliseconds >= 500)
            {
                _logger.LogWarning(
                    "Slow webhook ACK detected. RequestId: {RequestId}, ElapsedMs: {ElapsedMs}, BodyReadMs: {BodyReadMs}, SignatureValidationMs: {SignatureValidationMs}, AcceptMs: {AcceptMs}, MessagingEvents: {MessagingEvents}, Inflight: {Inflight}",
                    HttpContext.TraceIdentifier,
                    stopwatch.Elapsed.TotalMilliseconds,
                    bodyReadMs,
                    signatureValidationMs,
                    acceptMs,
                    summary.MessagingCount,
                    inflight);
            }

            _runtimeMetricsCollector.Increment("api.webhook.requests_total");
            _runtimeMetricsCollector.Increment($"api.webhook.status.{statusCode}");
            _runtimeMetricsCollector.Increment("api.webhook.body_bytes_total", bodyLength);
            _runtimeMetricsCollector.Increment("api.webhook.messaging_events_total", summary.MessagingCount);
            _runtimeMetricsCollector.Increment("api.webhook.standby_events_total", summary.StandbyCount);
            _runtimeMetricsCollector.ObserveDuration("api.webhook.ack_ms", stopwatch.Elapsed.TotalMilliseconds);
            _runtimeMetricsCollector.SetGauge("api.webhook.last_body_bytes", bodyLength);
            _runtimeMetricsCollector.SetGauge("api.webhook.last_messaging_count", summary.MessagingCount);
            _runtimeMetricsCollector.SetGauge("api.webhook.last_standby_count", summary.StandbyCount);
            inflight = Interlocked.Decrement(ref _inflightRequests);
            _runtimeMetricsCollector.SetGauge("api.webhook.inflight", Math.Max(0, inflight));
        }
    }

    private static WebhookPayloadSummary SummarizePayload(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return WebhookPayloadSummary.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var objectType = root.TryGetProperty("object", out var objectProperty) && objectProperty.ValueKind == JsonValueKind.String
                ? objectProperty.GetString() ?? "unknown"
                : "unknown";

            if (!root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                return new WebhookPayloadSummary(objectType, 0, 0, 0);
            }

            var entryCount = 0;
            var messagingCount = 0;
            var standbyCount = 0;

            foreach (var entry in entries.EnumerateArray())
            {
                entryCount++;

                if (entry.TryGetProperty("messaging", out var messaging) && messaging.ValueKind == JsonValueKind.Array)
                {
                    messagingCount += messaging.GetArrayLength();
                }

                if (entry.TryGetProperty("standby", out var standby) && standby.ValueKind == JsonValueKind.Array)
                {
                    standbyCount += standby.GetArrayLength();
                }
            }

            return new WebhookPayloadSummary(objectType, entryCount, messagingCount, standbyCount);
        }
        catch (JsonException)
        {
            return new WebhookPayloadSummary("invalid-json", 0, 0, 0);
        }
    }

    private static string SanitizeMetricSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '_');
        }

        return builder.ToString().Trim('_');
    }

    private sealed record WebhookPayloadSummary(string ObjectType, int EntryCount, int MessagingCount, int StandbyCount)
    {
        public static WebhookPayloadSummary Empty { get; } = new("empty", 0, 0, 0);
    }
}

