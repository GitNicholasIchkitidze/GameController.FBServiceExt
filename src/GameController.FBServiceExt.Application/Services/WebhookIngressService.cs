using System.Diagnostics;
using GameController.FBServiceExt.Application.Abstractions.Ingress;
using GameController.FBServiceExt.Application.Abstractions.Messaging;
using GameController.FBServiceExt.Application.Abstractions.Observability;
using GameController.FBServiceExt.Application.Contracts.Ingress;
using GameController.FBServiceExt.Application.Contracts.RawIngress;
using GameController.FBServiceExt.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameController.FBServiceExt.Application.Services;

public sealed class WebhookIngressService : IWebhookIngressService
{
    private readonly IRawIngressPublisher _publisher;
    private readonly IOptionsMonitor<WebhookIngressOptions> _optionsMonitor;
    private readonly TimeProvider _timeProvider;
    private readonly IRuntimeMetricsCollector _runtimeMetricsCollector;
    private readonly ILogger<WebhookIngressService> _logger;

    public WebhookIngressService(
        IRawIngressPublisher publisher,
        IOptionsMonitor<WebhookIngressOptions> optionsMonitor,
        TimeProvider timeProvider,
        IRuntimeMetricsCollector runtimeMetricsCollector,
        ILogger<WebhookIngressService> logger)
    {
        _publisher = publisher;
        _optionsMonitor = optionsMonitor;
        _timeProvider = timeProvider;
        _runtimeMetricsCollector = runtimeMetricsCollector;
        _logger = logger;
    }

    // API layer-იდან მიღებულ raw body-ს envelope-ად აფორმატებს და ingress queue-ში აქვეყნებს.
    // შემდეგ ამავე შეტყობინებას RawIngressNormalizerWorker ამუშავებს.
    public async ValueTask AcceptAsync(AcceptWebhookCommand command, CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;
        var publishRequest = new RawIngressPublishRequest(
            Guid.NewGuid(),
            options.Source,
            command.RequestId,
            command.ReceivedAtUtc ?? _timeProvider.GetUtcNow().UtcDateTime,
            command.BodyUtf8);

        var publishStopwatch = Stopwatch.StartNew();
        await _publisher.PublishAsync(publishRequest, cancellationToken);
        publishStopwatch.Stop();

        _runtimeMetricsCollector.Increment("api.ingress.envelopes_published");
        _runtimeMetricsCollector.Increment("api.ingress.bytes_published_total", publishRequest.BodyUtf8.Length);
        _runtimeMetricsCollector.SetGauge("api.ingress.last_body_bytes", publishRequest.BodyUtf8.Length);
        _runtimeMetricsCollector.ObserveDuration("api.ingress.accept_publish_ms", publishStopwatch.Elapsed.TotalMilliseconds);

        if (publishStopwatch.Elapsed.TotalMilliseconds >= 250)
        {
            _logger.LogWarning(
                "Ingress publish path was slow. EnvelopeId: {EnvelopeId}, RequestId: {RequestId}, BodyBytes: {BodyBytes}, PublishMs: {PublishMs}",
                publishRequest.EnvelopeId,
                publishRequest.RequestId,
                publishRequest.BodyUtf8.Length,
                publishStopwatch.Elapsed.TotalMilliseconds);
        }

        _logger.LogDebug(
            "Raw webhook body published to ingress queue. EnvelopeId: {EnvelopeId}, RequestId: {RequestId}, Source: {Source}, BodyBytes: {BodyBytes}",
            publishRequest.EnvelopeId,
            publishRequest.RequestId,
            publishRequest.Source,
            publishRequest.BodyUtf8.Length);
    }
}
