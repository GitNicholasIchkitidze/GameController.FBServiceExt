using System.Diagnostics;
using GameController.FBServiceExt.Application.Exceptions;
using GameController.FBServiceExt.Application.Abstractions.Messaging;
using GameController.FBServiceExt.Application.Abstractions.Observability;
using GameController.FBServiceExt.Application.Abstractions.Processing;
using GameController.FBServiceExt.Worker.Options;
using Microsoft.Extensions.Options;

namespace GameController.FBServiceExt.Worker.Services;

public sealed class NormalizedEventProcessorWorker : BackgroundService
{
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(2);

    private readonly INormalizedEventConsumer _normalizedEventConsumer;
    private readonly INormalizedEventProcessor _normalizedEventProcessor;
    private readonly IOptionsMonitor<WorkerExecutionOptions> _optionsMonitor;
    private readonly IRuntimeMetricsCollector _runtimeMetricsCollector;
    private readonly ILogger<NormalizedEventProcessorWorker> _logger;

    public NormalizedEventProcessorWorker(
        INormalizedEventConsumer normalizedEventConsumer,
        INormalizedEventProcessor normalizedEventProcessor,
        IOptionsMonitor<WorkerExecutionOptions> optionsMonitor,
        IRuntimeMetricsCollector runtimeMetricsCollector,
        ILogger<NormalizedEventProcessorWorker> logger)
    {
        _normalizedEventConsumer = normalizedEventConsumer;
        _normalizedEventProcessor = normalizedEventProcessor;
        _optionsMonitor = optionsMonitor;
        _runtimeMetricsCollector = runtimeMetricsCollector;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var parallelism = Math.Max(1, _optionsMonitor.CurrentValue.NormalizedProcessingParallelism);
        _runtimeMetricsCollector.SetGauge("worker.normalized.parallelism", parallelism);
        _logger.LogInformation("Normalized event processor worker started. Parallelism: {Parallelism}", parallelism);

        var tasks = Enumerable.Range(0, parallelism)
            .Select(loopId => RunLoopAsync(loopId, stoppingToken))
            .ToArray();

        await Task.WhenAll(tasks);

        _logger.LogInformation("Normalized event processor worker stopped.");
    }

    private async Task RunLoopAsync(int loopId, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            IMessageLease<Application.Contracts.Normalization.NormalizedMessengerEvent>? lease = null;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                lease = await _normalizedEventConsumer.ReceiveAsync(stoppingToken);
                if (lease is null)
                {
                    continue;
                }

                _runtimeMetricsCollector.Increment("worker.normalized.events_received");
                await _normalizedEventProcessor.ProcessAsync(lease.Payload, stoppingToken);
                await lease.CompleteAsync(stoppingToken);
                stopwatch.Stop();
                _runtimeMetricsCollector.ObserveDuration("worker.normalized.cycle_ms", stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (RetryableProcessingException ex)
            {
                stopwatch.Stop();
                _runtimeMetricsCollector.Increment("worker.normalized.failures");
                _runtimeMetricsCollector.ObserveDuration("worker.normalized.cycle_ms", stopwatch.Elapsed.TotalMilliseconds);
                _logger.LogWarning(ex, "Normalized event processing will retry after transient contention. LoopId: {LoopId}", loopId);
                await SafeAbandonAsync(lease, ex);
                await DelayBeforeRetryAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _runtimeMetricsCollector.Increment("worker.normalized.failures");
                _runtimeMetricsCollector.ObserveDuration("worker.normalized.cycle_ms", stopwatch.Elapsed.TotalMilliseconds);
                _logger.LogError(ex, "Normalized event processing cycle failed. LoopId: {LoopId}", loopId);
                await SafeAbandonAsync(lease, ex);
                await DelayBeforeRetryAsync(stoppingToken);
            }
        }
    }

    private async Task SafeAbandonAsync(IMessageLease<Application.Contracts.Normalization.NormalizedMessengerEvent>? lease, Exception exception)
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
            _logger.LogError(abandonEx, "Failed to abandon normalized event message after a processing error.");
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


