using System.Diagnostics;
using System.Text;
using GameController.FBServiceExt.Application.Abstractions.Ingress;
using GameController.FBServiceExt.Application.Abstractions.Observability;
using GameController.FBServiceExt.Application.Abstractions.Security;
using GameController.FBServiceExt.Application.Abstractions.State;
using GameController.FBServiceExt.Application.Contracts.Ingress;
using GameController.FBServiceExt.Application.Options;
using GameController.FBServiceExt.Application.Services;
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
    private readonly IVotingGateService _votingGateService;
    private readonly IOptionsMonitor<WebhookIngressOptions> _ingressOptionsMonitor;
    private readonly IOptionsMonitor<MetaWebhookOptions> _metaWebhookOptionsMonitor;
    private readonly IOptionsMonitor<VotingWorkflowOptions> _votingWorkflowOptionsMonitor;
    private readonly IOptionsMonitor<MessengerContentOptions> _messengerContentOptionsMonitor;
    private readonly IRuntimeMetricsCollector _runtimeMetricsCollector;
    private readonly ILogger<FacebookWebhooksController> _logger;

    public FacebookWebhooksController(
        IWebhookIngressService webhookIngressService,
        IWebhookSignatureValidator webhookSignatureValidator,
        IVotingGateService votingGateService,
        IOptionsMonitor<WebhookIngressOptions> ingressOptionsMonitor,
        IOptionsMonitor<MetaWebhookOptions> metaWebhookOptionsMonitor,
        IOptionsMonitor<VotingWorkflowOptions> votingWorkflowOptionsMonitor,
        IOptionsMonitor<MessengerContentOptions> messengerContentOptionsMonitor,
        IRuntimeMetricsCollector runtimeMetricsCollector,
        ILogger<FacebookWebhooksController> logger)
    {
        _webhookIngressService = webhookIngressService;
        _webhookSignatureValidator = webhookSignatureValidator;
        _votingGateService = votingGateService;
        _ingressOptionsMonitor = ingressOptionsMonitor;
        _metaWebhookOptionsMonitor = metaWebhookOptionsMonitor;
        _votingWorkflowOptionsMonitor = votingWorkflowOptionsMonitor;
        _messengerContentOptionsMonitor = messengerContentOptionsMonitor;
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
        var payloadInspection = WebhookPayloadInspection.Empty;

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
            var bodyReadStopwatch = Stopwatch.StartNew();
            var bodyUtf8 = await ReadBodyUtf8Async(Request.Body, Request.ContentLength, cancellationToken);
            bodyReadStopwatch.Stop();
            bodyReadMs = bodyReadStopwatch.Elapsed.TotalMilliseconds;
            _runtimeMetricsCollector.ObserveDuration("api.webhook.body_read_ms", bodyReadMs);

            bodyLength = bodyUtf8.Length;
            payloadInspection = WebhookPayloadInspector.Inspect(
                bodyUtf8,
                _messengerContentOptionsMonitor.CurrentValue.ForgetMeTokens,
                _votingWorkflowOptionsMonitor.CurrentValue.VoteStartTokens);

            stage = "signature_validation";
            var signatureHeader = Request.Headers["X-Hub-Signature-256"].FirstOrDefault();
            var signatureStopwatch = Stopwatch.StartNew();
            var signatureValid = _webhookSignatureValidator.IsValid(bodyUtf8, signatureHeader);
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
                    payloadInspection.ObjectType,
                    payloadInspection.EntryCount,
                    payloadInspection.MessagingCount,
                    payloadInspection.StandbyCount);
                return Unauthorized();
            }

            _logger.LogDebug(
                "Webhook request accepted. RequestId: {RequestId}, Object: {ObjectType}, Entries: {EntryCount}, MessagingEvents: {MessagingCount}, StandbyEvents: {StandbyCount}, ContentLength: {ContentLength}",
                HttpContext.TraceIdentifier,
                payloadInspection.ObjectType,
                payloadInspection.EntryCount,
                payloadInspection.MessagingCount,
                payloadInspection.StandbyCount,
                bodyLength);

            if (payloadInspection.CanDropAsGarbage)
            {
                statusCode = StatusCodes.Status200OK;
                _runtimeMetricsCollector.Increment("api.webhook.garbage_requests_dropped");
                _runtimeMetricsCollector.Increment("api.webhook.garbage_messages_dropped", payloadInspection.GarbageMessageCount);

                _logger.LogInformation(
                    "Webhook request acknowledged without queue publish because it only contained garbage traffic. RequestId: {RequestId}, MessagingEvents: {MessagingEvents}, GarbageMessages: {GarbageMessages}",
                    HttpContext.TraceIdentifier,
                    payloadInspection.MessagingCount,
                    payloadInspection.GarbageMessageCount);

                return Ok();
            }

            if (payloadInspection.CanDropWhenVotingDisabled)
            {
                stage = "voting_gate";
                var votingGateStopwatch = Stopwatch.StartNew();
                var votingStarted = await _votingGateService.IsVotingStartedAsync(cancellationToken);
                votingGateStopwatch.Stop();
                _runtimeMetricsCollector.ObserveDuration("api.webhook.voting_gate_ms", votingGateStopwatch.Elapsed.TotalMilliseconds);
                _runtimeMetricsCollector.SetGauge("api.voting.started", votingStarted ? 1 : 0);

                if (!votingStarted)
                {
                    statusCode = StatusCodes.Status200OK;
                    _runtimeMetricsCollector.Increment("api.webhook.voting_disabled_dropped");
                    _runtimeMetricsCollector.Increment("api.webhook.voting_disabled_dropped.messaging_events", payloadInspection.MessagingCount);

                    _logger.LogInformation(
                        "Webhook request acknowledged without queue publish because voting is disabled. RequestId: {RequestId}, MessagingEvents: {MessagingEvents}, ContainsForgetMeBypass: {ContainsForgetMeBypass}, ContainsPostbackEvents: {ContainsPostbackEvents}",
                        HttpContext.TraceIdentifier,
                        payloadInspection.MessagingCount,
                        payloadInspection.ContainsForgetMeBypass,
                        payloadInspection.ContainsPostbackEvents);

                    return Ok();
                }
            }
            else
            {
                _runtimeMetricsCollector.Increment("api.webhook.voting_gate_skipped");
            }

            var command = new AcceptWebhookCommand(
                HttpContext.TraceIdentifier,
                bodyUtf8,
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
                "Webhook request canceled before completion. Stage: {Stage}, RequestId: {RequestId}, ElapsedMs: {ElapsedMs}, Inflight: {Inflight}, RequestAborted: {RequestAborted}",
                stage,
                HttpContext.TraceIdentifier,
                stopwatch.Elapsed.TotalMilliseconds,
                inflight,
                HttpContext.RequestAborted.IsCancellationRequested);

            return new EmptyResult();
        }
        catch (Exception exception)
        {
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
                payloadInspection.MessagingCount,
                payloadInspection.StandbyCount,
                inflight);

            throw;
        }
        finally
        {
            if (statusCode == StatusCodes.Status200OK && acceptMs >= 250)
            {
                _logger.LogWarning(
                    "Webhook ACK was slower than the target threshold. RequestId: {RequestId}, AckMs: {AckMs}, BodyReadMs: {BodyReadMs}, SignatureValidationMs: {SignatureValidationMs}, AcceptMs: {AcceptMs}, BodyBytes: {BodyBytes}, MessagingEvents: {MessagingEvents}, Inflight: {Inflight}",
                    HttpContext.TraceIdentifier,
                    stopwatch.Elapsed.TotalMilliseconds,
                    bodyReadMs,
                    signatureValidationMs,
                    acceptMs,
                    bodyLength,
                    payloadInspection.MessagingCount,
                    inflight);
            }

            _runtimeMetricsCollector.Increment("api.webhook.requests_total");
            _runtimeMetricsCollector.Increment($"api.webhook.status.{statusCode}");
            _runtimeMetricsCollector.Increment("api.webhook.body_bytes_total", bodyLength);
            _runtimeMetricsCollector.Increment("api.webhook.messaging_events_total", payloadInspection.MessagingCount);
            _runtimeMetricsCollector.Increment("api.webhook.standby_events_total", payloadInspection.StandbyCount);
            _runtimeMetricsCollector.Increment("api.webhook.postback_events_total", payloadInspection.PostbackCount);
            _runtimeMetricsCollector.Increment("api.webhook.vote_start_messages_total", payloadInspection.VoteStartMessageCount);
            _runtimeMetricsCollector.Increment("api.webhook.forgetme_messages_total", payloadInspection.ForgetMeMessageCount);
            _runtimeMetricsCollector.Increment("api.webhook.garbage_messages_total", payloadInspection.GarbageMessageCount);
            _runtimeMetricsCollector.ObserveDuration("api.webhook.ack_ms", stopwatch.Elapsed.TotalMilliseconds);
            _runtimeMetricsCollector.SetGauge("api.webhook.last_body_bytes", bodyLength);
            _runtimeMetricsCollector.SetGauge("api.webhook.last_messaging_count", payloadInspection.MessagingCount);
            _runtimeMetricsCollector.SetGauge("api.webhook.last_standby_count", payloadInspection.StandbyCount);
            _runtimeMetricsCollector.SetGauge("api.webhook.last_postback_count", payloadInspection.PostbackCount);
            _runtimeMetricsCollector.SetGauge("api.webhook.last_vote_start_count", payloadInspection.VoteStartMessageCount);
            _runtimeMetricsCollector.SetGauge("api.webhook.last_forgetme_count", payloadInspection.ForgetMeMessageCount);
            _runtimeMetricsCollector.SetGauge("api.webhook.last_garbage_count", payloadInspection.GarbageMessageCount);
            inflight = Interlocked.Decrement(ref _inflightRequests);
            _runtimeMetricsCollector.SetGauge("api.webhook.inflight", Math.Max(0, inflight));
        }
    }

    private static async Task<byte[]> ReadBodyUtf8Async(Stream body, long? contentLength, CancellationToken cancellationToken)
    {
        if (contentLength is null)
        {
            await using var memoryStream = new MemoryStream();
            await body.CopyToAsync(memoryStream, cancellationToken);
            return memoryStream.ToArray();
        }

        if (contentLength <= 0)
        {
            return Array.Empty<byte>();
        }

        if (contentLength > int.MaxValue)
        {
            throw new InvalidOperationException("Request body is too large to buffer into a single array.");
        }

        var expectedLength = (int)contentLength.Value;
        var buffer = GC.AllocateUninitializedArray<byte>(expectedLength);
        var offset = 0;

        while (offset < expectedLength)
        {
            var bytesRead = await body.ReadAsync(buffer.AsMemory(offset, expectedLength - offset), cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            offset += bytesRead;
        }

        if (offset == expectedLength)
        {
            return buffer;
        }

        return buffer.AsSpan(0, offset).ToArray();
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
}
