using System.Diagnostics;
using GameController.FBServiceExt.Application.Abstractions.Messaging;
using GameController.FBServiceExt.Application.Abstractions.Observability;
using GameController.FBServiceExt.Application.Abstractions.Processing;
using GameController.FBServiceExt.Application.Contracts.Normalization;
using GameController.FBServiceExt.Application.Options;
using GameController.FBServiceExt.Application.Services;
using GameController.FBServiceExt.Worker.Options;
using Microsoft.Extensions.Options;

namespace GameController.FBServiceExt.Worker.Services;

public sealed class RawIngressNormalizerWorker : BackgroundService
{
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(2);

    private readonly IRawIngressConsumer _rawIngressConsumer;
    private readonly IRawWebhookNormalizer _rawWebhookNormalizer;
    private readonly INormalizedEventPublisher _normalizedEventPublisher;
    private readonly IOptionsMonitor<WorkerExecutionOptions> _optionsMonitor;
    private readonly IOptionsMonitor<VotingWorkflowOptions> _workflowOptionsMonitor;
    private readonly IOptionsMonitor<MessengerContentOptions> _messengerContentOptionsMonitor;
    private readonly IRuntimeMetricsCollector _runtimeMetricsCollector;
    private readonly ILogger<RawIngressNormalizerWorker> _logger;

    public RawIngressNormalizerWorker(
        IRawIngressConsumer rawIngressConsumer,
        IRawWebhookNormalizer rawWebhookNormalizer,
        INormalizedEventPublisher normalizedEventPublisher,
        IOptionsMonitor<WorkerExecutionOptions> optionsMonitor,
        IOptionsMonitor<VotingWorkflowOptions> workflowOptionsMonitor,
        IOptionsMonitor<MessengerContentOptions> messengerContentOptionsMonitor,
        IRuntimeMetricsCollector runtimeMetricsCollector,
        ILogger<RawIngressNormalizerWorker> logger)
    {
        _rawIngressConsumer = rawIngressConsumer;
        _rawWebhookNormalizer = rawWebhookNormalizer;
        _normalizedEventPublisher = normalizedEventPublisher;
        _optionsMonitor = optionsMonitor;
        _workflowOptionsMonitor = workflowOptionsMonitor;
        _messengerContentOptionsMonitor = messengerContentOptionsMonitor;
        _runtimeMetricsCollector = runtimeMetricsCollector;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var parallelism = Math.Max(1, _optionsMonitor.CurrentValue.RawIngressParallelism);
        _runtimeMetricsCollector.SetGauge("worker.raw.parallelism", parallelism);
        _logger.LogInformation("Raw ingress normalizer worker started. Parallelism: {Parallelism}", parallelism);

        var tasks = Enumerable.Range(0, parallelism)
            .Select(loopId => RunLoopAsync(loopId, stoppingToken))
            .ToArray();

        await Task.WhenAll(tasks);

        _logger.LogInformation("Raw ingress normalizer worker stopped.");
    }

    private async Task RunLoopAsync(int loopId, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            IMessageLease<Application.Contracts.RawIngress.RawWebhookEnvelope>? lease = null;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                lease = await _rawIngressConsumer.ReceiveAsync(stoppingToken);
                if (lease is null)
                {
                    continue;
                }

                _runtimeMetricsCollector.Increment("worker.raw.envelopes_received");

                var events = await _rawWebhookNormalizer.NormalizeAsync(lease.Payload, stoppingToken);
                var publishableEvents = FilterBusinessEvents(events);
                var garbageDropped = Math.Max(0, events.Count - publishableEvents.Count);

                _runtimeMetricsCollector.Increment("worker.raw.events_normalized", events.Count);
                _runtimeMetricsCollector.Increment("worker.raw.business_events_published", publishableEvents.Count);
                _runtimeMetricsCollector.ObserveValue("worker.raw.batch_size", events.Count);
                _runtimeMetricsCollector.ObserveValue("worker.raw.publish_batch_size", publishableEvents.Count);
                _runtimeMetricsCollector.SetGauge("worker.raw.last_batch_size", events.Count);
                _runtimeMetricsCollector.SetGauge("worker.raw.last_publish_batch_size", publishableEvents.Count);

                if (garbageDropped > 0)
                {
                    _runtimeMetricsCollector.Increment("worker.raw.garbage_messages_dropped", garbageDropped);
                    _runtimeMetricsCollector.SetGauge("worker.raw.last_garbage_dropped", garbageDropped);
                }

                _logger.LogDebug(
                    "Webhook envelope normalized. LoopId: {LoopId}, EnvelopeId: {EnvelopeId}, RequestId: {RequestId}, EventCount: {EventCount}, PublishableEventCount: {PublishableEventCount}, GarbageDropped: {GarbageDropped}",
                    loopId,
                    lease.Payload.EnvelopeId,
                    lease.Payload.RequestId,
                    events.Count,
                    publishableEvents.Count,
                    garbageDropped);

                if (publishableEvents.Count > 0)
                {
                    await _normalizedEventPublisher.PublishBatchAsync(publishableEvents, stoppingToken);
                }

                await lease.CompleteAsync(stoppingToken);
                stopwatch.Stop();
                _runtimeMetricsCollector.ObserveDuration("worker.raw.cycle_ms", stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _runtimeMetricsCollector.Increment("worker.raw.failures");
                _runtimeMetricsCollector.ObserveDuration("worker.raw.cycle_ms", stopwatch.Elapsed.TotalMilliseconds);
                _logger.LogError(ex, "Raw ingress normalization cycle failed. LoopId: {LoopId}", loopId);
                await SafeAbandonAsync(lease, ex);
                await DelayBeforeRetryAsync(stoppingToken);
            }
        }
    }

    private IReadOnlyList<NormalizedMessengerEvent> FilterBusinessEvents(IReadOnlyList<NormalizedMessengerEvent> events)
    {
        if (events.Count == 0)
        {
            return events;
        }

        var forgetMeTokens = _messengerContentOptionsMonitor.CurrentValue.ForgetMeTokens;
        var voteStartTokens = _workflowOptionsMonitor.CurrentValue.VoteStartTokens;
        var filtered = new List<NormalizedMessengerEvent>(events.Count);

        foreach (var normalizedEvent in events)
        {
            if (MessengerInboundClassifier.IsBusinessRelevantEvent(normalizedEvent, forgetMeTokens, voteStartTokens))
            {
                filtered.Add(normalizedEvent);
            }
        }

        return filtered;
    }

    private async Task SafeAbandonAsync(IMessageLease<Application.Contracts.RawIngress.RawWebhookEnvelope>? lease, Exception exception)
    {
        if (lease is null)
        {
            return;
        }

        try
        {
            await lease.AbandonAsync(exception, CancellationToken.None);
        }
        catch (Exception abandonEx)
        {
            _logger.LogError(abandonEx, "Failed to abandon raw ingress message after a processing error.");
        }
    }

    private static async Task DelayBeforeRetryAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FailureBackoff, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
