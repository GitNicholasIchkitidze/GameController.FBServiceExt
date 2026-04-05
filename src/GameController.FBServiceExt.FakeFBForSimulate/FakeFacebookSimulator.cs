using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameController.FBServiceExt.FakeFBForSimulate;

internal sealed class FakeFacebookSimulatorEngine : IAsyncDisposable
{
    private readonly HttpClient _httpClient = new();
    private readonly SimulatorMessageHub _messageHub = new();
    private readonly ConcurrentDictionary<string, ThrottledLogState> _throttledLogs = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly RedisFakeMetaOutboundSubscription _outboundSubscription;

    private CancellationTokenSource? _runCts;
    private Task? _runMonitorTask;
    private List<Task> _workerTasks = new();
    private SimulationCounters _counters = new();
    private SimulatorRunSettings? _currentSettings;
    private DateTimeOffset? _startedAtUtc;

    public FakeFacebookSimulatorEngine(SimulatorDefaults defaults)
    {
        _outboundSubscription = new RedisFakeMetaOutboundSubscription(defaults, CaptureOutboundMessage, WriteLog);
    }

    public event Action<string>? LogProduced;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _runCts is not null;
            }
        }
    }

    public async Task StartAsync(SimulatorRunSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.WebhookUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.PageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.StartToken);

        lock (_gate)
        {
            if (_runCts is not null)
            {
                throw new InvalidOperationException("Simulation is already running.");
            }

            _messageHub.Clear();
            _counters = new SimulationCounters();
            _currentSettings = settings;
            _startedAtUtc = DateTimeOffset.UtcNow;
            _runCts = new CancellationTokenSource();
            _workerTasks = new List<Task>(settings.UserCount);
        }

        await EnsureListenerAsync(settings.ListenerUrl).ConfigureAwait(false);

        var stopAtUtc = DateTimeOffset.UtcNow.AddSeconds(settings.DurationSeconds);
        for (var index = 1; index <= settings.UserCount; index++)
        {
            var userId = $"simulate-user-{index:D6}";
            _workerTasks.Add(Task.Run(() => RunUserLoopAsync(userId, settings, stopAtUtc, _runCts.Token)));
        }

        _runMonitorTask = MonitorRunCompletionAsync(_runCts, _workerTasks);

        WriteLog($"Simulation started with {settings.UserCount} fake users. FakeMeta=RedisStore, Webhook={settings.WebhookUrl}");
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        List<Task> tasks;

        lock (_gate)
        {
            cts = _runCts;
            tasks = _workerTasks;
            _runCts = null;
            _workerTasks = new List<Task>();
            _runMonitorTask = null;
        }

        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
            WriteLog("Worker loops did not stop within 10 seconds.");
        }
        catch (Exception exception)
        {
            WriteLog($"Stop warning: {exception.Message}");
        }
        finally
        {
            cts.Dispose();
        }

        FlushThrottledLogs();
        WriteLog("Simulation stopped.");
    }

    private async Task MonitorRunCompletionAsync(CancellationTokenSource runCts, IReadOnlyCollection<Task> tasks)
    {
        try
        {
            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            WriteLog($"Simulation completion monitor warning: {exception.Message}");
        }
        finally
        {
            var shouldFinalize = false;
            lock (_gate)
            {
                if (ReferenceEquals(_runCts, runCts))
                {
                    _runCts = null;
                    _workerTasks = new List<Task>();
                    _runMonitorTask = null;
                    shouldFinalize = true;
                }
            }

            if (shouldFinalize)
            {
                runCts.Dispose();
                FlushThrottledLogs();
                WriteLog("Simulation completed.");
            }
        }
    }

    public async Task EnsureListenerAsync(string listenerUrl)
    {
        await _outboundSubscription.StartAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public async Task StopListenerAsync()
    {
        await _outboundSubscription.DisposeAsync().ConfigureAwait(false);
    }

    public SimulationSnapshot GetSnapshot()
    {
        var counters = _counters;
        var completed = Interlocked.Read(ref counters.CyclesCompleted);
        var durationTotal = Interlocked.Read(ref counters.CompletedCycleDurationMilliseconds);

        return new SimulationSnapshot(
            IsRunning,
            _startedAtUtc,
            _currentSettings?.UserCount ?? 0,
            Interlocked.Read(ref counters.ActiveUsers),
            Interlocked.Read(ref counters.CyclesStarted),
            completed,
            Interlocked.Read(ref counters.CyclesFailed),
            Interlocked.Read(ref counters.WebhookAttempts),
            Interlocked.Read(ref counters.WebhookSuccesses),
            Interlocked.Read(ref counters.WebhookFailures),
            Interlocked.Read(ref counters.OutboundMessagesReceived),
            Interlocked.Read(ref counters.CarouselsReceived),
            Interlocked.Read(ref counters.ConfirmationsReceived),
            Interlocked.Read(ref counters.AcceptedTextsReceived),
            Interlocked.Read(ref counters.CooldownTextsReceived),
            Interlocked.Read(ref counters.RejectedTextsReceived),
            Interlocked.Read(ref counters.ExpiredTextsReceived),
            Interlocked.Read(ref counters.OtherTextsReceived),
            Interlocked.Read(ref counters.UnexpectedOutboundShapes),
            completed > 0 ? (double)durationTotal / completed : 0d);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await StopListenerAsync().ConfigureAwait(false);
        _httpClient.Dispose();
    }

    private async Task RunUserLoopAsync(string userId, SimulatorRunSettings settings, DateTimeOffset stopAtUtc, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _counters.ActiveUsers);
        try
        {
            var random = new Random(HashCode.Combine(userId, Environment.TickCount));
            if (settings.StartupJitterSeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(random.NextDouble() * settings.StartupJitterSeconds), cancellationToken).ConfigureAwait(false);
            }

            while (!cancellationToken.IsCancellationRequested && DateTimeOffset.UtcNow < stopAtUtc)
            {
                var cycleStopwatch = Stopwatch.StartNew();
                Interlocked.Increment(ref _counters.CyclesStarted);
                _messageHub.ClearRecipient(userId);

                var completed = await ExecuteCycleAsync(userId, settings, random, cancellationToken).ConfigureAwait(false);
                if (completed)
                {
                    Interlocked.Add(ref _counters.CompletedCycleDurationMilliseconds, cycleStopwatch.ElapsedMilliseconds);
                    Interlocked.Increment(ref _counters.CyclesCompleted);
                    await Task.Delay(TimeSpan.FromSeconds(settings.CooldownSeconds), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    Interlocked.Increment(ref _counters.CyclesFailed);
                    await DelayFailureBackoffAsync(settings, random, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Interlocked.Decrement(ref _counters.ActiveUsers);
        }
    }

    private async Task<bool> ExecuteCycleAsync(string userId, SimulatorRunSettings settings, Random random, CancellationToken cancellationToken)
    {
        if (!await SendTextWebhookAsync(userId, settings.StartToken, settings, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var carouselOrText = await _messageHub.WaitForMessageAsync(
            userId,
            static message => message.IsCarouselMessage || message.IsTextMessage,
            TimeSpan.FromSeconds(settings.OutboundWaitSeconds),
            cancellationToken).ConfigureAwait(false);

        if (carouselOrText is null)
        {
            Interlocked.Increment(ref _counters.UnexpectedOutboundShapes);
            WriteThrottledLog("carousel-timeout", $"{userId}: carousel was not received in time.");
            return false;
        }

        if (carouselOrText.IsTextMessage)
        {
            return HandleStageTextOutcome(userId, carouselOrText, settings.TextPatterns, "carousel");
        }

        var carousel = carouselOrText;
        Interlocked.Increment(ref _counters.CarouselsReceived);
        var candidateButton = carousel.TryPickRandomCandidateButton(random);
        if (candidateButton is null || string.IsNullOrWhiteSpace(candidateButton.Payload))
        {
            Interlocked.Increment(ref _counters.UnexpectedOutboundShapes);
            WriteThrottledLog("carousel-shape", $"{userId}: carousel did not contain a usable candidate button.");
            return false;
        }

        await DelayThinkAsync(settings, random, cancellationToken).ConfigureAwait(false);

        if (!await SendPostbackWebhookAsync(userId, candidateButton.Payload!, candidateButton.Title ?? string.Empty, settings, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var confirmationOrText = await _messageHub.WaitForMessageAsync(
            userId,
            static message => message.IsConfirmationMessage || message.IsTextMessage,
            TimeSpan.FromSeconds(settings.OutboundWaitSeconds),
            cancellationToken).ConfigureAwait(false);

        if (confirmationOrText is null)
        {
            Interlocked.Increment(ref _counters.UnexpectedOutboundShapes);
            WriteThrottledLog("confirmation-timeout", $"{userId}: confirmation challenge was not received in time.");
            return false;
        }

        if (confirmationOrText.IsTextMessage)
        {
            return HandleStageTextOutcome(userId, confirmationOrText, settings.TextPatterns, "confirmation challenge");
        }

        var confirmation = confirmationOrText;
        Interlocked.Increment(ref _counters.ConfirmationsReceived);
        var confirmButton = confirmation.FindConfirmationAcceptButton();
        if (confirmButton is null || string.IsNullOrWhiteSpace(confirmButton.Payload))
        {
            Interlocked.Increment(ref _counters.UnexpectedOutboundShapes);
            WriteThrottledLog("confirmation-shape", $"{userId}: confirmation challenge did not expose a YES button.");
            return false;
        }

        await DelayThinkAsync(settings, random, cancellationToken).ConfigureAwait(false);

        if (!await SendPostbackWebhookAsync(userId, confirmButton.Payload!, confirmButton.Title ?? string.Empty, settings, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var finalText = await _messageHub.WaitForMessageAsync(userId, static message => message.IsTextMessage, TimeSpan.FromSeconds(settings.OutboundWaitSeconds), cancellationToken).ConfigureAwait(false);
        if (finalText is null)
        {
            Interlocked.Increment(ref _counters.UnexpectedOutboundShapes);
            WriteThrottledLog("final-timeout", $"{userId}: final text message was not received in time.");
            return false;
        }

        switch (SimulatorTextClassifier.Classify(finalText, settings.TextPatterns))
        {
            case SimulatorTextOutcome.Accepted:
                Interlocked.Increment(ref _counters.AcceptedTextsReceived);
                return true;
            case SimulatorTextOutcome.Cooldown:
                Interlocked.Increment(ref _counters.CooldownTextsReceived);
                WriteThrottledLog("final-cooldown", $"{userId}: cooldown text received instead of acceptance.");
                return false;
            case SimulatorTextOutcome.Rejected:
                Interlocked.Increment(ref _counters.RejectedTextsReceived);
                WriteThrottledLog("final-rejected", $"{userId}: confirmation was rejected.");
                return false;
            case SimulatorTextOutcome.Expired:
                Interlocked.Increment(ref _counters.ExpiredTextsReceived);
                WriteThrottledLog("final-expired", $"{userId}: confirmation expired.");
                return false;
            default:
                Interlocked.Increment(ref _counters.OtherTextsReceived);
                WriteThrottledLog("final-other", $"{userId}: unexpected final text: {finalText.Text}");
                return false;
        }
    }

    private bool HandleStageTextOutcome(string userId, FakeOutboundMessage message, SimulatorTextPatterns patterns, string stageName)
    {
        switch (SimulatorTextClassifier.Classify(message, patterns))
        {
            case SimulatorTextOutcome.Cooldown:
                Interlocked.Increment(ref _counters.CooldownTextsReceived);
                WriteThrottledLog($"stage-{stageName}-cooldown", $"{userId}: cooldown text received instead of {stageName}.");
                return false;
            case SimulatorTextOutcome.Rejected:
                Interlocked.Increment(ref _counters.RejectedTextsReceived);
                WriteThrottledLog($"stage-{stageName}-rejected", $"{userId}: rejection text received while waiting for {stageName}.");
                return false;
            case SimulatorTextOutcome.Expired:
                Interlocked.Increment(ref _counters.ExpiredTextsReceived);
                WriteThrottledLog($"stage-{stageName}-expired", $"{userId}: expired text received while waiting for {stageName}.");
                return false;
            default:
                Interlocked.Increment(ref _counters.OtherTextsReceived);
                Interlocked.Increment(ref _counters.UnexpectedOutboundShapes);
                WriteThrottledLog($"stage-{stageName}-other", $"{userId}: unexpected text received while waiting for {stageName}: {message.Text}");
                return false;
        }
    }

    private async Task<bool> SendTextWebhookAsync(string senderId, string messageText, SimulatorRunSettings settings, CancellationToken cancellationToken)
    {
        var payload = WebhookPayloadFactory.CreateTextPayload(senderId, settings.PageId, messageText);
        return await SendWebhookAsync(payload, settings, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> SendPostbackWebhookAsync(string senderId, string payloadValue, string title, SimulatorRunSettings settings, CancellationToken cancellationToken)
    {
        var payload = WebhookPayloadFactory.CreatePostbackPayload(senderId, settings.PageId, payloadValue, title);
        return await SendWebhookAsync(payload, settings, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> SendWebhookAsync(string body, SimulatorRunSettings settings, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.WebhookUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(settings.AppSecret))
        {
            request.Headers.Add("X-Hub-Signature-256", WebhookPayloadFactory.SignBody(settings.AppSecret, body));
        }

        Interlocked.Increment(ref _counters.WebhookAttempts);
        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                Interlocked.Increment(ref _counters.WebhookSuccesses);
                return true;
            }

            Interlocked.Increment(ref _counters.WebhookFailures);
            WriteThrottledLog("inbound-http-failure", $"Inbound webhook failed with status {(int)response.StatusCode}.");
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Interlocked.Increment(ref _counters.WebhookFailures);
            WriteThrottledLog("inbound-transport-failure", $"Inbound webhook transport failure: {exception.Message}");
            return false;
        }
    }

    private static async Task DelayThinkAsync(SimulatorRunSettings settings, Random random, CancellationToken cancellationToken)
    {
        var min = Math.Min(settings.MinThinkMilliseconds, settings.MaxThinkMilliseconds);
        var max = Math.Max(settings.MinThinkMilliseconds, settings.MaxThinkMilliseconds);
        if (max <= 0)
        {
            return;
        }

        var delay = max == min ? max : random.Next(min, max + 1);
        if (delay > 0)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task DelayFailureBackoffAsync(SimulatorRunSettings settings, Random random, CancellationToken cancellationToken)
    {
        var minSeconds = Math.Min(settings.FailureBackoffMinSeconds, settings.FailureBackoffMaxSeconds);
        var maxSeconds = Math.Max(settings.FailureBackoffMinSeconds, settings.FailureBackoffMaxSeconds);
        if (maxSeconds <= 0)
        {
            return;
        }

        var delaySeconds = maxSeconds == minSeconds ? maxSeconds : random.Next(minSeconds, maxSeconds + 1);
        if (delaySeconds > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
        }
    }

    private void CaptureOutboundMessage(FakeOutboundMessage message)
    {
        Interlocked.Increment(ref _counters.OutboundMessagesReceived);
        _messageHub.Append(message);
    }

    private Task CaptureOutboundMessageAsync(FakeOutboundMessage message)
    {
        CaptureOutboundMessage(message);
        return Task.CompletedTask;
    }

    private void WriteLog(string message)
    {
        LogProduced?.Invoke(message);
    }

    private void WriteThrottledLog(string key, string message)
    {
        var state = _throttledLogs.GetOrAdd(key, static _ => new ThrottledLogState());
        string? summaryMessage = null;
        string? immediateMessage = null;

        lock (state)
        {
            var now = DateTimeOffset.UtcNow;
            if (state.WindowStartedUtc == DateTimeOffset.MinValue || now - state.WindowStartedUtc >= TimeSpan.FromSeconds(2))
            {
                if (state.SuppressedCount > 0 && !string.IsNullOrWhiteSpace(state.LastMessage))
                {
                    summaryMessage = $"{state.LastMessage} [suppressed similar events: {state.SuppressedCount:N0}]";
                }

                state.WindowStartedUtc = now;
                state.SuppressedCount = 0;
                state.LastMessage = message;
                immediateMessage = message;
            }
            else
            {
                state.SuppressedCount++;
                state.LastMessage = message;
            }
        }

        if (summaryMessage is not null)
        {
            WriteLog(summaryMessage);
        }

        if (immediateMessage is not null)
        {
            WriteLog(immediateMessage);
        }
    }

    private void FlushThrottledLogs()
    {
        foreach (var (_, state) in _throttledLogs)
        {
            string? summaryMessage = null;
            lock (state)
            {
                if (state.SuppressedCount > 0 && !string.IsNullOrWhiteSpace(state.LastMessage))
                {
                    summaryMessage = $"{state.LastMessage} [suppressed similar events: {state.SuppressedCount:N0}]";
                    state.SuppressedCount = 0;
                    state.WindowStartedUtc = DateTimeOffset.UtcNow;
                }
            }

            if (summaryMessage is not null)
            {
                WriteLog(summaryMessage);
            }
        }
    }

    private sealed class ThrottledLogState
    {
        public DateTimeOffset WindowStartedUtc;
        public int SuppressedCount;
        public string? LastMessage;
    }

    private sealed class SimulationCounters
    {
        public long ActiveUsers;
        public long CyclesStarted;
        public long CyclesCompleted;
        public long CyclesFailed;
        public long CompletedCycleDurationMilliseconds;
        public long WebhookAttempts;
        public long WebhookSuccesses;
        public long WebhookFailures;
        public long OutboundMessagesReceived;
        public long CarouselsReceived;
        public long ConfirmationsReceived;
        public long AcceptedTextsReceived;
        public long CooldownTextsReceived;
        public long RejectedTextsReceived;
        public long ExpiredTextsReceived;
        public long OtherTextsReceived;
        public long UnexpectedOutboundShapes;
    }
}

internal sealed class FakeFacebookCallbackHost : IAsyncDisposable
{
    private readonly string _listenerUrl;
    private readonly Func<FakeOutboundMessage, Task> _captureAsync;
    private readonly Action<string> _log;
    private WebApplication? _app;
    private long _sequence;

    public FakeFacebookCallbackHost(string listenerUrl, Func<FakeOutboundMessage, Task> captureAsync, Action<string> log)
    {
        _listenerUrl = listenerUrl.TrimEnd('/');
        _captureAsync = captureAsync;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.Configure<JsonOptions>(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });

        var app = builder.Build();
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapPost("/{version}/me/messages", async (string version, HttpRequest request) =>
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            try
            {
                var outbound = FakeOutboundMessageParser.Parse(Interlocked.Increment(ref _sequence), version, document.RootElement);
                await _captureAsync(outbound).ConfigureAwait(false);
                return Results.Ok(new
                {
                    recipient_id = outbound.RecipientId,
                    message_id = $"fakefb-sim.{outbound.Sequence}"
                });
            }
            catch (Exception exception)
            {
                _log($"Callback parse failure: {exception.Message}");
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        app.Urls.Add(_listenerUrl);
        _app = app;
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _log($"Fake Facebook callback host is listening on {_listenerUrl}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null)
        {
            return;
        }

        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        _app = null;
        _log("Fake Facebook callback host stopped.");
    }
}

internal sealed class SimulatorMessageHub
{
    private readonly ConcurrentDictionary<string, RecipientInbox> _inboxes = new(StringComparer.Ordinal);

    public void Append(FakeOutboundMessage message)
    {
        var inbox = _inboxes.GetOrAdd(message.RecipientId, static _ => new RecipientInbox());
        inbox.Enqueue(message);
    }

    public void Clear()
    {
        foreach (var (_, inbox) in _inboxes)
        {
            inbox.Dispose();
        }

        _inboxes.Clear();
    }

    public void ClearRecipient(string recipientId)
    {
        if (_inboxes.TryRemove(recipientId, out var inbox))
        {
            inbox.Dispose();
        }
    }

    public async Task<FakeOutboundMessage?> WaitForMessageAsync(string recipientId, Func<FakeOutboundMessage, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var inbox = _inboxes.GetOrAdd(recipientId, static _ => new RecipientInbox());
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        while (!timeoutCts.IsCancellationRequested)
        {
            while (inbox.TryDequeue(out var message))
            {
                if (predicate(message))
                {
                    return message;
                }
            }

            try
            {
                await inbox.Signal.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return null;
    }

    private sealed class RecipientInbox : IDisposable
    {
        private readonly ConcurrentQueue<FakeOutboundMessage> _messages = new();
        public SemaphoreSlim Signal { get; } = new(0);

        public void Enqueue(FakeOutboundMessage message)
        {
            _messages.Enqueue(message);
            Signal.Release();
        }

        public bool TryDequeue(out FakeOutboundMessage message) => _messages.TryDequeue(out message!);

        public void Dispose() => Signal.Dispose();
    }
}

internal static class WebhookPayloadFactory
{
    public static string CreateTextPayload(string senderId, string pageId, string messageText)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return JsonSerializer.Serialize(new
        {
            @object = "page",
            entry = new[]
            {
                new
                {
                    id = pageId,
                    time = now,
                    messaging = new[]
                    {
                        new
                        {
                            sender = new { id = senderId },
                            recipient = new { id = pageId },
                            timestamp = now,
                            message = new
                            {
                                mid = $"mid.sim.{senderId}.{Guid.NewGuid():N}",
                                text = messageText
                            }
                        }
                    }
                }
            }
        });
    }

    public static string CreatePostbackPayload(string senderId, string pageId, string payloadValue, string title)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return JsonSerializer.Serialize(new
        {
            @object = "page",
            entry = new[]
            {
                new
                {
                    id = pageId,
                    time = now,
                    messaging = new[]
                    {
                        new
                        {
                            sender = new { id = senderId },
                            recipient = new { id = pageId },
                            timestamp = now,
                            postback = new
                            {
                                payload = payloadValue,
                                title
                            }
                        }
                    }
                }
            }
        });
    }

    public static string SignBody(string appSecret, string body)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
        var hash = hmac.ComputeHash(bodyBytes);
        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}

internal static class FakeOutboundMessageParser
{
    public static FakeOutboundMessage Parse(long sequence, string version, JsonElement root)
    {
        if (!root.TryGetProperty("recipient", out var recipient) ||
            recipient.ValueKind != JsonValueKind.Object ||
            !recipient.TryGetProperty("id", out var recipientIdNode) ||
            recipientIdNode.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("recipient.id is required.");
        }

        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("message object is required.");
        }

        return new FakeOutboundMessage(
            sequence,
            recipientIdNode.GetString() ?? string.Empty,
            version,
            ResolveKind(message),
            TryGetText(message),
            TryGetTemplateType(message),
            ParseElements(message),
            ParseButtons(message));
    }

    private static string ResolveKind(JsonElement message)
    {
        if (!string.IsNullOrWhiteSpace(TryGetTemplateType(message)))
        {
            return "template";
        }

        return TryGetText(message) is not null ? "text" : "unknown";
    }

    private static string? TryGetText(JsonElement message)
        => message.TryGetProperty("text", out var textNode) && textNode.ValueKind == JsonValueKind.String
            ? textNode.GetString()
            : null;

    private static string? TryGetTemplateType(JsonElement message)
    {
        if (!message.TryGetProperty("attachment", out var attachment) || attachment.ValueKind != JsonValueKind.Object ||
            !attachment.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("template_type", out var templateType) || templateType.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return templateType.GetString();
    }

    private static IReadOnlyList<FakeTemplateElement> ParseElements(JsonElement message)
    {
        if (!message.TryGetProperty("attachment", out var attachment) || attachment.ValueKind != JsonValueKind.Object ||
            !attachment.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<FakeTemplateElement>();
        }

        var result = new List<FakeTemplateElement>();
        foreach (var element in elements.EnumerateArray())
        {
            result.Add(new FakeTemplateElement(
                TryGetString(element, "title"),
                TryGetString(element, "subtitle"),
                TryGetString(element, "image_url"),
                ParseButtons(element)));
        }

        return result;
    }

    private static IReadOnlyList<FakeButton> ParseButtons(JsonElement node)
    {
        if (!node.TryGetProperty("buttons", out var buttons) || buttons.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<FakeButton>();
        }

        var result = new List<FakeButton>();
        foreach (var button in buttons.EnumerateArray())
        {
            result.Add(new FakeButton(
                TryGetString(button, "title"),
                TryGetString(button, "payload"),
                TryGetString(button, "type")));
        }

        return result;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;
}

internal static class SimulatorTextClassifier
{
    public static SimulatorTextOutcome Classify(FakeOutboundMessage message, SimulatorTextPatterns patterns)
    {
        if (!message.IsTextMessage || string.IsNullOrWhiteSpace(message.Text))
        {
            return SimulatorTextOutcome.Other;
        }

        var text = Normalize(message.Text);
        if (ContainsFragments(text, patterns.CooldownFragments))
        {
            return SimulatorTextOutcome.Cooldown;
        }

        if (ContainsFragments(text, patterns.RejectedFragments))
        {
            return SimulatorTextOutcome.Rejected;
        }

        if (ContainsFragments(text, patterns.ExpiredFragments))
        {
            return SimulatorTextOutcome.Expired;
        }

        return SimulatorTextOutcome.Accepted;
    }

    private static bool ContainsFragments(string text, IReadOnlyList<string> fragments)
    {
        if (fragments.Count == 0)
        {
            return false;
        }

        var cursor = 0;
        foreach (var fragment in fragments)
        {
            var normalized = Normalize(fragment);
            if (normalized.Length == 0)
            {
                continue;
            }

            var index = text.IndexOf(normalized, cursor, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            cursor = index + normalized.Length;
        }

        return true;
    }

    private static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

internal sealed record SimulatorRunSettings(
    string WebhookUrl,
    string ListenerUrl,
    string PageId,
    string AppSecret,
    string StartToken,
    int UserCount,
    int DurationSeconds,
    int CooldownSeconds,
    int StartupJitterSeconds,
    int MinThinkMilliseconds,
    int MaxThinkMilliseconds,
    int OutboundWaitSeconds,
    int FailureBackoffMinSeconds,
    int FailureBackoffMaxSeconds,
    SimulatorTextPatterns TextPatterns);

internal sealed record SimulatorTextPatterns(
    IReadOnlyList<string> CooldownFragments,
    IReadOnlyList<string> RejectedFragments,
    IReadOnlyList<string> ExpiredFragments);

internal sealed record SimulationSnapshot(
    bool IsRunning,
    DateTimeOffset? StartedAtUtc,
    int ConfiguredUsers,
    long ActiveUsers,
    long CyclesStarted,
    long CyclesCompleted,
    long CyclesFailed,
    long WebhookAttempts,
    long WebhookSuccesses,
    long WebhookFailures,
    long OutboundMessagesReceived,
    long CarouselsReceived,
    long ConfirmationsReceived,
    long AcceptedTextsReceived,
    long CooldownTextsReceived,
    long RejectedTextsReceived,
    long ExpiredTextsReceived,
    long OtherTextsReceived,
    long UnexpectedOutboundShapes,
    double AverageCompletedCycleMilliseconds);

internal enum SimulatorTextOutcome
{
    Accepted,
    Cooldown,
    Rejected,
    Expired,
    Other
}

internal sealed record FakeOutboundMessage(
    long Sequence,
    string RecipientId,
    string Version,
    string Kind,
    string? Text,
    string? TemplateType,
    IReadOnlyList<FakeTemplateElement> Elements,
    IReadOnlyList<FakeButton> Buttons)
{
    public bool IsCarouselMessage => string.Equals(TemplateType, "generic", StringComparison.OrdinalIgnoreCase) && Elements.Count > 1;
    public bool IsConfirmationMessage => string.Equals(TemplateType, "generic", StringComparison.OrdinalIgnoreCase) && Elements.Count == 1 && Elements[0].Buttons.Any(static button => !string.IsNullOrWhiteSpace(button.Payload) && button.Payload.Contains(":YES", StringComparison.OrdinalIgnoreCase));
    public bool IsTextMessage => string.Equals(Kind, "text", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(Text);

    public FakeButton? TryPickRandomCandidateButton(Random random)
    {
        var available = Elements
            .SelectMany(static element => element.Buttons)
            .Where(static button => !string.IsNullOrWhiteSpace(button.Payload))
            .ToArray();

        return available.Length == 0 ? null : available[random.Next(available.Length)];
    }

    public FakeButton? FindConfirmationAcceptButton()
    {
        return Elements[0].Buttons.FirstOrDefault(static button => !string.IsNullOrWhiteSpace(button.Payload) && button.Payload.Contains(":YES", StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed record FakeTemplateElement(
    string? Title,
    string? Subtitle,
    string? ImageUrl,
    IReadOnlyList<FakeButton> Buttons);

internal sealed record FakeButton(
    string? Title,
    string? Payload,
    string? Type);
















