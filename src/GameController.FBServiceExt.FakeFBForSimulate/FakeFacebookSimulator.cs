using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace GameController.FBServiceExt.FakeFBForSimulate;

internal sealed class FakeFacebookSimulatorEngine : IAsyncDisposable
{
	internal const string FakeMetaTransportMode = "RedisStore";

	readonly HttpClient _http = new();
	readonly SimulatorMessageHub _hub = new();
	readonly ConcurrentDictionary<string, ThrottledLogState> _logs = new(StringComparer.Ordinal);
	readonly object _gate = new();
	readonly RedisFakeMetaOutboundSubscription _sub;

	CancellationTokenSource? _cts;
	List<Task> _tasks = new();
	SimulationCounters _c = new();
	SimulatorRunSettings? _settings;
	DateTimeOffset? _started;

	public FakeFacebookSimulatorEngine(SimulatorDefaults d)
	{
		_sub = new(d, Capture, WriteLog);
	}

	public event Action<string>? LogProduced;

	public bool IsRunning
	{
		get
		{
			lock (_gate)
			{
				return _cts is not null;
			}
		}
	}

	public async Task StartAsync(SimulatorRunSettings s)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(s.WebhookUrl);
		ArgumentException.ThrowIfNullOrWhiteSpace(s.PageId);
		ArgumentException.ThrowIfNullOrWhiteSpace(s.StartToken);

		lock (_gate)
		{
			if (_cts is not null)
				throw new InvalidOperationException("Simulation is already running.");

			_hub.Clear();
			_c = new();
			_settings = s;
			_started = DateTimeOffset.UtcNow;
			_cts = new();
			_tasks = new(s.UserCount);
		}

		await EnsureListenerAsync(s.ListenerUrl);

		if (s.ConfigureVotingGateOnStart && !string.IsNullOrWhiteSpace(s.ActiveShowId))
		{
			var u = new Uri(s.WebhookUrl);
			using var r = new HttpRequestMessage(HttpMethod.Put, new Uri($"{u.Scheme}://{u.Authority}/dev/admin/api/voting"))
			{
				Content = JsonContent.Create(new { votingStarted = true, activeShowId = s.ActiveShowId })
			};

			using var x = await _http.SendAsync(r);
			x.EnsureSuccessStatusCode();
		}

		var stop = DateTimeOffset.UtcNow.AddSeconds(s.DurationSeconds);

		for (var i = 1; i <= s.UserCount; i++)
		{
			var id = $"simulate-user-{i:D6}";
			_tasks.Add(Task.Run(() => Loop(id, s, stop, _cts!.Token)));
		}

		WriteLog($"Simulation started with {s.UserCount} fake users. FakeMeta={FakeMetaTransportMode}, Webhook={s.WebhookUrl}");
	}

	public async Task StopAsync()
	{
		CancellationTokenSource? c;
		List<Task> t;

		lock (_gate)
		{
			c = _cts;
			t = _tasks;
			_cts = null;
			_tasks = new();
		}

		if (c is null)
			return;

		try
		{
			c.Cancel();
			if (t.Count > 0)
				await Task.WhenAll(t).WaitAsync(TimeSpan.FromSeconds(10));
		}
		catch
		{
		}
		finally
		{
			c.Dispose();
		}

		Flush();
		WriteLog("Simulation stopped.");
	}

	public Task EnsureListenerAsync(string listenerUrl) => _sub.StartAsync(CancellationToken.None);

	public ValueTask StopListenerAsync() => _sub.DisposeAsync();

	public async ValueTask DisposeAsync()
	{
		await StopAsync();
		await StopListenerAsync();
		_http.Dispose();
	}

	public SimulationSnapshot GetSnapshot()
	{
		var c = _c;
		var done = Interlocked.Read(ref c.CyclesCompleted);
		var total = Interlocked.Read(ref c.CompletedCycleDurationMilliseconds);

		return new(
			IsRunning,
			_started,
			FakeMetaTransportMode,
			_settings?.UserCount ?? 0,
			Interlocked.Read(ref c.ActiveUsers),
			Interlocked.Read(ref c.CyclesStarted),
			done,
			Interlocked.Read(ref c.CyclesFailed),
			Interlocked.Read(ref c.WebhookAttempts),
			Interlocked.Read(ref c.WebhookSuccesses),
			Interlocked.Read(ref c.WebhookFailures),
			Interlocked.Read(ref c.OutboundMessagesReceived),
			Interlocked.Read(ref c.CarouselsReceived),
			Interlocked.Read(ref c.ConfirmationsReceived),
			Interlocked.Read(ref c.AcceptedTextsReceived),
			Interlocked.Read(ref c.CooldownTextsReceived),
			Interlocked.Read(ref c.RejectedTextsReceived),
			Interlocked.Read(ref c.ExpiredTextsReceived),
			Interlocked.Read(ref c.InactiveVotingTextsReceived),
			Interlocked.Read(ref c.OtherTextsReceived),
			Interlocked.Read(ref c.CarouselTimeouts),
			Interlocked.Read(ref c.ConfirmationTimeouts),
			Interlocked.Read(ref c.FinalTextTimeouts),
			Interlocked.Read(ref c.CarouselShapeFailures),
			Interlocked.Read(ref c.ConfirmationShapeFailures),
			Interlocked.Read(ref c.StageUnexpectedTexts),
			Interlocked.Read(ref c.LateAcceptedTexts),
			Interlocked.Read(ref c.UnexpectedOutboundShapes),
			done > 0 ? (double)total / done : 0d);
	}

	async Task Loop(string id, SimulatorRunSettings s, DateTimeOffset stop, CancellationToken ct)
	{
		Interlocked.Increment(ref _c.ActiveUsers);

		try
		{
			var rnd = new Random(HashCode.Combine(id, Environment.TickCount));

			if (s.StartupJitterSeconds > 0)
				await Task.Delay(TimeSpan.FromSeconds(rnd.NextDouble() * s.StartupJitterSeconds), ct);

			while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow < stop)
			{
				var sw = Stopwatch.StartNew();
				Interlocked.Increment(ref _c.CyclesStarted);
				_hub.DiscardPendingNonTextMessages(id);

				var ok = await Cycle(id, s, rnd, ct);

				if (ok)
				{
					Interlocked.Add(ref _c.CompletedCycleDurationMilliseconds, sw.ElapsedMilliseconds);
					Interlocked.Increment(ref _c.CyclesCompleted);

					await Task.Delay(TimeSpan.FromSeconds(s.CooldownSeconds), ct);
				}
				else
				{
					Interlocked.Increment(ref _c.CyclesFailed);

					var min = Math.Max(0, s.FailureBackoffMinSeconds);
					var max = Math.Max(min, s.FailureBackoffMaxSeconds);

					await Task.Delay(TimeSpan.FromSeconds(max == 0 ? 0 : rnd.Next(min, max + 1)), ct);
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			Interlocked.Decrement(ref _c.ActiveUsers);
		}
	}

	async Task<bool> Cycle(string id, SimulatorRunSettings s, Random rnd, CancellationToken ct)
	{
		if (!await SendText(id, s.StartToken, s, ct))
			return false;

		var a = await _hub.WaitForMessageAsync(
			id,
			static m => m.IsCarouselMessage || m.IsTextMessage,
			TimeSpan.FromSeconds(s.OutboundWaitSeconds),
			ct);

		if (a is null)
		{
			Interlocked.Increment(ref _c.CarouselTimeouts);
			Interlocked.Increment(ref _c.UnexpectedOutboundShapes);
			WriteT("carousel-timeout", $"{id}: carousel was not received in time.");
			return false;
		}

		if (a.IsTextMessage)
			return Stage(id, a, s.TextPatterns, "carousel");

		Interlocked.Increment(ref _c.CarouselsReceived);

		var b = a.TryPickRandomCandidateButton(rnd);
		if (b?.Payload is null)
		{
			Interlocked.Increment(ref _c.CarouselShapeFailures);
			Interlocked.Increment(ref _c.UnexpectedOutboundShapes);
			WriteT("carousel-shape", $"{id}: carousel did not contain a usable candidate button.");
			return false;
		}

		await DelayThink(s, rnd, ct);

		if (!await SendPostback(id, b.Payload, b.Title ?? string.Empty, s, ct))
			return false;

		var c = await _hub.WaitForMessageAsync(
			id,
			static m => m.IsConfirmationMessage || m.IsTextMessage,
			TimeSpan.FromSeconds(s.OutboundWaitSeconds),
			ct);

		if (c is null)
		{
			Interlocked.Increment(ref _c.ConfirmationTimeouts);
			Interlocked.Increment(ref _c.UnexpectedOutboundShapes);
			WriteT("confirmation-timeout", $"{id}: confirmation challenge was not received in time.");
			return false;
		}

		if (c.IsTextMessage)
			return Stage(id, c, s.TextPatterns, "confirmation challenge");

		Interlocked.Increment(ref _c.ConfirmationsReceived);

		var d = c.FindConfirmationAcceptButton();
		if (d?.Payload is null)
		{
			Interlocked.Increment(ref _c.ConfirmationShapeFailures);
			Interlocked.Increment(ref _c.UnexpectedOutboundShapes);
			WriteT("confirmation-shape", $"{id}: confirmation challenge did not expose a YES button.");
			return false;
		}

		await DelayThink(s, rnd, ct);

		if (!await SendPostback(id, d.Payload, d.Title ?? string.Empty, s, ct))
			return false;

		var f = await _hub.WaitForMessageAsync(
			id,
			static m => m.IsTextMessage,
			TimeSpan.FromSeconds(s.OutboundWaitSeconds),
			ct);

		if (f is null)
		{
			Interlocked.Increment(ref _c.FinalTextTimeouts);
			Interlocked.Increment(ref _c.UnexpectedOutboundShapes);
			WriteT("final-timeout", $"{id}: final text message was not received in time.");
			return false;
		}

		switch (SimulatorTextClassifier.Classify(f, s.TextPatterns))
		{
			case SimulatorTextOutcome.Accepted:
				Interlocked.Increment(ref _c.AcceptedTextsReceived);
				return true;

			case SimulatorTextOutcome.Cooldown:
				Interlocked.Increment(ref _c.CooldownTextsReceived);
				return false;

			case SimulatorTextOutcome.Rejected:
				Interlocked.Increment(ref _c.RejectedTextsReceived);
				return false;

			case SimulatorTextOutcome.Expired:
				Interlocked.Increment(ref _c.ExpiredTextsReceived);
				return false;

			case SimulatorTextOutcome.Inactive:
				Interlocked.Increment(ref _c.InactiveVotingTextsReceived);
				return false;

			default:
				Interlocked.Increment(ref _c.OtherTextsReceived);
				return false;
		}
	}

	bool Stage(string id, FakeOutboundMessage m, SimulatorTextPatterns p, string stage)
	{
		switch (SimulatorTextClassifier.Classify(m, p))
		{
			case SimulatorTextOutcome.Accepted:
				Interlocked.Increment(ref _c.LateAcceptedTexts);
				WriteT($"stage-{stage}-accepted", $"{id}: accepted text arrived while waiting for {stage}.");
				return false;

			case SimulatorTextOutcome.Cooldown:
				Interlocked.Increment(ref _c.CooldownTextsReceived);
				return false;

			case SimulatorTextOutcome.Rejected:
				Interlocked.Increment(ref _c.RejectedTextsReceived);
				return false;

			case SimulatorTextOutcome.Expired:
				Interlocked.Increment(ref _c.ExpiredTextsReceived);
				return false;

			case SimulatorTextOutcome.Inactive:
				Interlocked.Increment(ref _c.InactiveVotingTextsReceived);
				return false;

			default:
				Interlocked.Increment(ref _c.OtherTextsReceived);
				Interlocked.Increment(ref _c.StageUnexpectedTexts);
				Interlocked.Increment(ref _c.UnexpectedOutboundShapes);
				WriteT($"stage-{stage}-other", $"{id}: unexpected text received while waiting for {stage}: {m.Text}");
				return false;
		}
	}

	Task DelayThink(SimulatorRunSettings s, Random rnd, CancellationToken ct)
	{
		var min = Math.Max(0, s.MinThinkMilliseconds);
		var max = Math.Max(min, s.MaxThinkMilliseconds);
		return Task.Delay(max == 0 ? 0 : rnd.Next(min, max + 1), ct);
	}

	Task<bool> SendText(string senderId, string txt, SimulatorRunSettings s, CancellationToken ct) =>
		Send(WebhookPayloadFactory.CreateTextPayload(senderId, s.PageId, txt), s, ct);

	Task<bool> SendPostback(string senderId, string payload, string title, SimulatorRunSettings s, CancellationToken ct) =>
		Send(WebhookPayloadFactory.CreatePostbackPayload(senderId, s.PageId, payload, title), s, ct);

	async Task<bool> Send(string payload, SimulatorRunSettings s, CancellationToken ct)
	{
		Interlocked.Increment(ref _c.WebhookAttempts);

		using var req = new HttpRequestMessage(HttpMethod.Post, s.WebhookUrl)
		{
			Content = new StringContent(payload, Encoding.UTF8, "application/json")
		};

		if (!string.IsNullOrWhiteSpace(s.AppSecret))
			req.Headers.TryAddWithoutValidation("X-Hub-Signature-256", WebhookPayloadFactory.SignBody(s.AppSecret, payload));

		try
		{
			using var resp = await _http.SendAsync(req, ct);
			if (resp.IsSuccessStatusCode)
			{
				Interlocked.Increment(ref _c.WebhookSuccesses);
				return true;
			}

			Interlocked.Increment(ref _c.WebhookFailures);
			var body = await resp.Content.ReadAsStringAsync(ct);
			WriteT(
				$"webhook-http-{(int)resp.StatusCode}",
				$"Webhook send failed. Status={(int)resp.StatusCode}, Reason={resp.ReasonPhrase}, Url={s.WebhookUrl}, Body={TrimForLog(body)}");
			return false;
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			Interlocked.Increment(ref _c.WebhookFailures);
			WriteT("webhook-exception", $"Webhook send exception for {s.WebhookUrl}: {exception.Message}");
			return false;
		}
	}

	void Capture(FakeOutboundMessage m)
	{
		Interlocked.Increment(ref _c.OutboundMessagesReceived);

		try
		{
			var buttonsFromElements = m.Elements?
				.SelectMany(e => e.Buttons ?? Array.Empty<FakeButton>())
				.Select(b => new
				{
					b.Title,
					b.Payload,
					b.Type,
					IsVote = b.IsVoteButton,
					IsConfirmation = b.IsConfirmationButton,
					IsAccept = b.IsConfirmationAcceptButton
				})
				.ToArray() ?? Array.Empty<object>();

			var rootButtons = m.Buttons?
				.Select(b => new
				{
					b.Title,
					b.Payload,
					b.Type,
					IsVote = b.IsVoteButton,
					IsConfirmation = b.IsConfirmationButton,
					IsAccept = b.IsConfirmationAcceptButton
				})
				.ToArray() ?? Array.Empty<object>();

			var debug = JsonSerializer.Serialize(
				new
				{
					m.Sequence,
					m.RecipientId,
					m.Version,
					m.Kind,
					m.Text,
					m.TemplateType,
					IsCarouselMessage = m.IsCarouselMessage,
					IsConfirmationMessage = m.IsConfirmationMessage,
					IsTextMessage = m.IsTextMessage,
					ElementCount = m.Elements?.Count ?? 0,
					RootButtonCount = m.Buttons?.Count ?? 0,
					ElementButtons = buttonsFromElements,
					RootButtons = rootButtons
				},
				new JsonSerializerOptions
				{
					Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
					WriteIndented = false
				});
			//WriteLog($"OUTBOUND DEBUG: {debug}");
		}
		catch (Exception ex)
		{
			WriteLog($"OUTBOUND DEBUG ERROR: {ex.Message}");
		}

		_hub.Append(m);
	}

	void WriteLog(string m) => LogProduced?.Invoke(m);

	void WriteT(string k, string m)
	{
		var s = _logs.GetOrAdd(k, _ => new());
		string? immediate = null;
		string? flush = null;

		lock (s)
		{
			var now = DateTimeOffset.UtcNow;
			if (s.WindowStartedUtc == DateTimeOffset.MinValue || now - s.WindowStartedUtc >= TimeSpan.FromSeconds(2))
			{
				if (s.SuppressedCount > 0 && !string.IsNullOrWhiteSpace(s.LastMessage))
					flush = $"{s.LastMessage} [suppressed similar events: {s.SuppressedCount:N0}]";

				s.WindowStartedUtc = now;
				s.SuppressedCount = 0;
				s.LastMessage = m;
				immediate = m;
			}
			else
			{
				s.SuppressedCount++;
				s.LastMessage = m;
			}
		}

		if (flush is not null)
			WriteLog(flush);

		if (immediate is not null)
			WriteLog(immediate);
	}

	static string TrimForLog(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return "<empty>";

		var compact = value.ReplaceLineEndings(" ").Trim();
		return compact.Length <= 240 ? compact : compact[..240];
	}

	void Flush()
	{
		foreach (var kv in _logs)
		{
			string? flush = null;
			lock (kv.Value)
			{
				if (kv.Value.SuppressedCount > 0 && !string.IsNullOrWhiteSpace(kv.Value.LastMessage))
				{
					flush = $"{kv.Value.LastMessage} [suppressed similar events: {kv.Value.SuppressedCount:N0}]";
					kv.Value.SuppressedCount = 0;
				}
			}

			if (flush is not null)
				WriteLog(flush);
		}
	}

	sealed class ThrottledLogState
	{
		public DateTimeOffset WindowStartedUtc;
		public int SuppressedCount;
		public string? LastMessage;
	}

	sealed class SimulationCounters
	{
		public long ActiveUsers, CyclesStarted, CyclesCompleted, CyclesFailed, CompletedCycleDurationMilliseconds,
			WebhookAttempts, WebhookSuccesses, WebhookFailures, OutboundMessagesReceived, CarouselsReceived, ConfirmationsReceived,
			AcceptedTextsReceived, CooldownTextsReceived, RejectedTextsReceived, ExpiredTextsReceived,
			InactiveVotingTextsReceived, OtherTextsReceived, CarouselTimeouts, ConfirmationTimeouts, FinalTextTimeouts,
			CarouselShapeFailures, ConfirmationShapeFailures, StageUnexpectedTexts, LateAcceptedTexts, UnexpectedOutboundShapes;
	}
}

internal sealed class SimulatorMessageHub
{
	readonly ConcurrentDictionary<string, RecipientInbox> _inboxes = new(StringComparer.Ordinal);

	public void Append(FakeOutboundMessage m) => _inboxes.GetOrAdd(m.RecipientId, _ => new()).Enqueue(m);

	public void Clear()
	{
		foreach (var kv in _inboxes)
			kv.Value.Dispose();

		_inboxes.Clear();
	}

	public void ClearRecipient(string id)
	{
		if (_inboxes.TryRemove(id, out var i))
			i.Dispose();
	}

	public void DiscardPendingNonTextMessages(string id)
	{
		if (_inboxes.TryGetValue(id, out var i))
			i.RemoveWhere(static m => !m.IsTextMessage);
	}

	public async Task<FakeOutboundMessage?> WaitForMessageAsync(string id, Func<FakeOutboundMessage, bool> p, TimeSpan t, CancellationToken ct)
	{
		var i = _inboxes.GetOrAdd(id, _ => new());

		using var c = CancellationTokenSource.CreateLinkedTokenSource(ct);
		c.CancelAfter(t);

		while (!c.IsCancellationRequested)
		{
			if (i.TryTakeFirstMatching(p, out var m))
				return m;

			try
			{
				await i.Signal.WaitAsync(c.Token);
			}
			catch (OperationCanceledException)
			{
				break;
			}
		}

		return null;
	}

	sealed class RecipientInbox : IDisposable
	{
		readonly List<FakeOutboundMessage> _messages = new();
		readonly object _sync = new();

		public SemaphoreSlim Signal { get; } = new(0);

		public void Enqueue(FakeOutboundMessage m)
		{
			lock (_sync)
				_messages.Add(m);

			Signal.Release();
		}

		public bool TryTakeFirstMatching(Func<FakeOutboundMessage, bool> p, out FakeOutboundMessage? m)
		{
			lock (_sync)
			{
				for (var x = 0; x < _messages.Count; x++)
				{
					var c = _messages[x];
					if (!p(c))
						continue;

					_messages.RemoveAt(x);
					m = c;
					return true;
				}
			}

			m = null;
			return false;
		}

		public void RemoveWhere(Predicate<FakeOutboundMessage> p)
		{
			lock (_sync)
				_messages.RemoveAll(p);
		}

		public void Dispose() => Signal.Dispose();
	}
}

internal static class WebhookPayloadFactory
{
	public static string CreateTextPayload(string senderId, string pageId, string text)
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
								text
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

	public static string SignBody(string secret, string body)
	{
		using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
		return $"sha256={Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant()}";
	}
}

internal static class SimulatorTextClassifier
{
	public static SimulatorTextOutcome Classify(FakeOutboundMessage m, SimulatorTextPatterns p)
	{
		if (!m.IsTextMessage || string.IsNullOrWhiteSpace(m.Text))
			return SimulatorTextOutcome.Other;

		var t = Norm(m.Text);

		if (Has(t, p.CooldownFragments))
			return SimulatorTextOutcome.Cooldown;

		if (Has(t, p.RejectedFragments))
			return SimulatorTextOutcome.Rejected;

		if (Has(t, p.ExpiredFragments))
			return SimulatorTextOutcome.Expired;

		if (Has(t, p.InactiveVotingFragments))
			return SimulatorTextOutcome.Inactive;

		return t.Contains("მადლობა", StringComparison.OrdinalIgnoreCase) ||
			   t.Contains("თქვენ ხმა მიეცით", StringComparison.OrdinalIgnoreCase)
			? SimulatorTextOutcome.Accepted
			: SimulatorTextOutcome.Other;
	}

	static bool Has(string text, IReadOnlyList<string> f)
	{
		if (f.Count == 0)
			return false;

		var c = 0;
		foreach (var x in f)
		{
			var n = Norm(x);
			if (n.Length == 0)
				continue;

			var i = text.IndexOf(n, c, StringComparison.OrdinalIgnoreCase);
			if (i < 0)
				return false;

			c = i + n.Length;
		}

		return true;
	}

	static string Norm(string v) =>
		string.Join(' ', v.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
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
	string ActiveShowId,
	bool ConfigureVotingGateOnStart,
	SimulatorTextPatterns TextPatterns);

internal sealed record SimulatorTextPatterns(
	IReadOnlyList<string> CooldownFragments,
	IReadOnlyList<string> RejectedFragments,
	IReadOnlyList<string> ExpiredFragments,
	IReadOnlyList<string> InactiveVotingFragments);

internal sealed record SimulationSnapshot(
	bool IsRunning,
	DateTimeOffset? StartedAtUtc,
	string OutboundTransportMode,
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
	long InactiveVotingTextsReceived,
	long OtherTextsReceived,
	long CarouselTimeouts,
	long ConfirmationTimeouts,
	long FinalTextTimeouts,
	long CarouselShapeFailures,
	long ConfirmationShapeFailures,
	long StageUnexpectedTexts,
	long LateAcceptedTexts,
	long UnexpectedOutboundShapes,
	double AverageCompletedCycleMilliseconds);

internal enum SimulatorTextOutcome
{
	Accepted,
	Cooldown,
	Rejected,
	Expired,
	Inactive,
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
	public bool IsCarouselMessage =>
		string.Equals(TemplateType, "generic", StringComparison.OrdinalIgnoreCase) &&
		Elements.Count > 1;

	public bool IsConfirmationMessage =>
		Elements.SelectMany(static e => e.Buttons)
				.Any(static b => b.IsConfirmationButton);

	public bool IsTextMessage =>
		string.Equals(Kind, "text", StringComparison.OrdinalIgnoreCase) &&
		!string.IsNullOrWhiteSpace(Text);

	public FakeButton? TryPickRandomCandidateButton(Random r)
	{
		var a = Elements
			.SelectMany(static e => e.Buttons)
			.Where(static b => b.IsVoteButton)
			.ToArray();

		return a.Length == 0 ? null : a[r.Next(a.Length)];
	}

	public FakeButton? FindConfirmationAcceptButton() =>
		Elements.SelectMany(static e => e.Buttons)
				.FirstOrDefault(static b => b.IsConfirmationAcceptButton);
}

internal sealed record FakeTemplateElement(
	string? Title,
	string? Subtitle,
	string? ImageUrl,
	IReadOnlyList<FakeButton> Buttons);

internal sealed record FakeButton(string? Title, string? Payload, string? Type)
{
	public bool IsVoteButton =>
		!string.IsNullOrWhiteSpace(Payload) &&
		Payload.StartsWith("VOTE1:", StringComparison.Ordinal);

	public bool IsConfirmationButton =>
		TryParseConfirmationAction(Payload, out _);

	public bool IsConfirmationAcceptButton =>
		TryParseConfirmationAction(Payload, out var action) &&
		action == FakeConfirmationAction.Accept;

	static bool TryParseConfirmationAction(string? payload, out FakeConfirmationAction action)
	{
		action = FakeConfirmationAction.Invalid;

		if (string.IsNullOrWhiteSpace(payload) ||
			!payload.StartsWith("CONFIRM1:", StringComparison.Ordinal))
		{
			return false;
		}

		var parts = payload.Split(':', 3, StringSplitOptions.None);
		if (parts.Length != 3 || string.IsNullOrWhiteSpace(parts[1]))
		{
			return false;
		}

		try
		{
			using var document = JsonDocument.Parse(DecodeBase64Url(parts[1]));

			JsonElement actionNode;
			if (!(document.RootElement.TryGetProperty("action", out actionNode) ||
				  document.RootElement.TryGetProperty("Action", out actionNode)) ||
				actionNode.ValueKind != JsonValueKind.String)
			{
				return false;
			}

			var value = actionNode.GetString()?.Trim().ToUpperInvariant();

			action = value switch
			{
				"YES" => FakeConfirmationAction.Accept,
				"ACCEPT" => FakeConfirmationAction.Accept,
				"DECOYA" => FakeConfirmationAction.Decoy,
				"DECOYB" => FakeConfirmationAction.Decoy,
				_ => FakeConfirmationAction.Invalid
			};

			return action != FakeConfirmationAction.Invalid;
		}
		catch
		{
			return false;
		}
	}

	static byte[] DecodeBase64Url(string value)
	{
		var normalized = value.Replace('-', '+').Replace('_', '/');
		var padding = normalized.Length % 4;
		if (padding > 0)
		{
			normalized = normalized.PadRight(normalized.Length + (4 - padding), '=');
		}

		return Convert.FromBase64String(normalized);
	}
}

internal enum FakeConfirmationAction
{
	Invalid,
	Accept,
	Decoy
}



