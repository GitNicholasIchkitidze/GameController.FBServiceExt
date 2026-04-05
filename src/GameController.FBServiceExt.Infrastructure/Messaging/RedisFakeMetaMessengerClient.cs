using System.Diagnostics;
using GameController.FBServiceExt.Application.Abstractions.Messaging;
using GameController.FBServiceExt.Application.Abstractions.Observability;
using GameController.FBServiceExt.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameController.FBServiceExt.Infrastructure.Messaging;

public sealed class RedisFakeMetaMessengerClient : IOutboundMessengerClient
{
    private readonly RedisFakeMetaMessengerStore _store;
    private readonly IOptionsMonitor<MetaMessengerOptions> _optionsMonitor;
    private readonly IRuntimeMetricsCollector _runtimeMetricsCollector;
    private readonly ILogger<RedisFakeMetaMessengerClient> _logger;

    public RedisFakeMetaMessengerClient(
        RedisFakeMetaMessengerStore store,
        IOptionsMonitor<MetaMessengerOptions> optionsMonitor,
        IRuntimeMetricsCollector runtimeMetricsCollector,
        ILogger<RedisFakeMetaMessengerClient> logger)
    {
        _store = store;
        _optionsMonitor = optionsMonitor;
        _runtimeMetricsCollector = runtimeMetricsCollector;
        _logger = logger;
    }

    public ValueTask<bool> SendTextAsync(string recipientId, string messageText, CancellationToken cancellationToken)
    {
        return SendAsync(
            recipientId,
            static (store, version, id, ct, state) => store.CaptureTextAsync(id, version, state.MessageText, ct),
            new { MessageText = messageText },
            cancellationToken);
    }

    public ValueTask<bool> SendButtonTemplateAsync(
        string recipientId,
        string promptText,
        IReadOnlyCollection<MessengerPostbackButton> buttons,
        CancellationToken cancellationToken)
    {
        return SendAsync(
            recipientId,
            static (store, version, id, ct, state) => store.CaptureButtonTemplateAsync(id, version, state.PromptText, state.Buttons, ct),
            (PromptText: promptText, Buttons: buttons),
            cancellationToken);
    }

    public ValueTask<bool> SendGenericTemplateAsync(
        string recipientId,
        IReadOnlyCollection<MessengerGenericTemplateElement> elements,
        CancellationToken cancellationToken)
    {
        return SendAsync(
            recipientId,
            static (store, version, id, ct, state) => store.CaptureGenericTemplateAsync(id, version, state.Elements, ct),
            new { Elements = elements },
            cancellationToken);
    }

    private async ValueTask<bool> SendAsync<TState>(
        string recipientId,
        Func<RedisFakeMetaMessengerStore, string, string, CancellationToken, TState, ValueTask<FakeMetaOutboundMessage>> captureAsync,
        TState state,
        CancellationToken cancellationToken)
    {
        _runtimeMetricsCollector.Increment("worker.outbound.messenger.attempts");

        var options = _optionsMonitor.CurrentValue;
        if (!options.Enabled)
        {
            _runtimeMetricsCollector.Increment("worker.outbound.messenger.skipped_disabled");
            _logger.LogDebug(
                "Fake Meta outbound send skipped because MetaMessenger is disabled. RecipientId: {RecipientId}",
                recipientId);
            return false;
        }

        var version = string.IsNullOrWhiteSpace(options.GraphApiVersion)
            ? "v24.0"
            : options.GraphApiVersion.Trim();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await captureAsync(_store, version, recipientId, cancellationToken, state);
            RecordSuccess(stopwatch.Elapsed.TotalMilliseconds);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            RecordTimeout(stopwatch.Elapsed.TotalMilliseconds);
            _logger.LogWarning(
                exception,
                "Fake Meta outbound send timed out. RecipientId: {RecipientId}",
                recipientId);
            return false;
        }
        catch (Exception exception)
        {
            RecordException(stopwatch.Elapsed.TotalMilliseconds, exception);
            _logger.LogWarning(
                exception,
                "Fake Meta outbound send failed. RecipientId: {RecipientId}",
                recipientId);
            return false;
        }
    }

    private void RecordSuccess(double elapsedMs)
    {
        _runtimeMetricsCollector.ObserveDuration("worker.outbound.messenger.http_ms", elapsedMs);
        _runtimeMetricsCollector.SetGauge("worker.outbound.messenger.last_http_status", 200);
        _runtimeMetricsCollector.Increment("worker.outbound.messenger.http_status.200");
        _runtimeMetricsCollector.Increment("worker.outbound.messenger.http_2xx");
        _runtimeMetricsCollector.Increment("worker.outbound.messenger.transport_success");
    }

    private void RecordTimeout(double elapsedMs)
    {
        _runtimeMetricsCollector.ObserveDuration("worker.outbound.messenger.http_ms", elapsedMs);
        _runtimeMetricsCollector.Increment("worker.outbound.messenger.timeouts");
        _runtimeMetricsCollector.Increment("worker.outbound.messenger.transport_failures");
    }

    private void RecordException(double elapsedMs, Exception exception)
    {
        _runtimeMetricsCollector.ObserveDuration("worker.outbound.messenger.http_ms", elapsedMs);
        _runtimeMetricsCollector.Increment("worker.outbound.messenger.transport_failures");
        _runtimeMetricsCollector.Increment("worker.outbound.messenger.transport_exceptions");
        _runtimeMetricsCollector.Increment($"worker.outbound.messenger.exception_type.{SanitizeMetricSegment(exception.GetType().Name)}");
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
}

