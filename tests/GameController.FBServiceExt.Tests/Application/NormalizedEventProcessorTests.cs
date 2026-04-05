using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameController.FBServiceExt.Application.Abstractions.Messaging;
using GameController.FBServiceExt.Application.Abstractions.Observability;
using GameController.FBServiceExt.Application.Abstractions.Persistence;
using GameController.FBServiceExt.Application.Abstractions.Processing;
using GameController.FBServiceExt.Application.Abstractions.State;
using GameController.FBServiceExt.Application.Contracts.Normalization;
using GameController.FBServiceExt.Application.Contracts.Observability;
using GameController.FBServiceExt.Application.Contracts.Persistence;
using GameController.FBServiceExt.Application.Contracts.Runtime;
using GameController.FBServiceExt.Application.Contracts.Votes;
using GameController.FBServiceExt.Application.Options;
using GameController.FBServiceExt.Application.Services;
using GameController.FBServiceExt.Domain.Messaging;
using GameController.FBServiceExt.Domain.Voting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GameController.FBServiceExt.Tests.Application;

public sealed class NormalizedEventProcessorTests
{
    private const string VoteSecret = "vote-secret-123";
    private const string ErasureSecret = "erase-secret-123";
    private const string ActiveShowId = "show-2026";

    [Fact]
    public async Task ProcessAsync_MessageVoteStart_SendsSignedCarousel()
    {
        var dedupe = new InMemoryDedupeStore();
        var cooldowns = new InMemoryVoteCooldownStore();
        var normalizedEvents = new InMemoryNormalizedEventStore();
        var votes = new InMemoryAcceptedVoteStore();
        var outbound = new InMemoryOutboundMessengerClient();
        var processor = CreateProcessor(dedupe, cooldowns, normalizedEvents, votes, outboundMessengerClient: outbound);

        await processor.ProcessAsync(CreateMessageEvent("mid-1", "user-1", "page-1", "GET_STARTED"), CancellationToken.None);

        Assert.True(await dedupe.IsProcessedAsync("mid-1", CancellationToken.None));
        Assert.Single(normalizedEvents.Items);
        Assert.Empty(votes.Items);

        var carousel = Assert.Single(outbound.GenericTemplates);
        Assert.Equal("user-1", carousel.RecipientId);
        var candidateCard = Assert.Single(carousel.Elements);
        var button = Assert.Single(candidateCard.Buttons);
        Assert.StartsWith("VOTE1:", button.Payload, StringComparison.Ordinal);
        Assert.Contains(ActiveShowId, DecodeSignedBody(button.Payload));
    }

    [Fact]
    public async Task ProcessAsync_CandidateSelection_WithConfirmationEnabled_SendsSignedConfirmationPrompt()
    {
        var dedupe = new InMemoryDedupeStore();
        var cooldowns = new InMemoryVoteCooldownStore();
        var normalizedEvents = new InMemoryNormalizedEventStore();
        var votes = new InMemoryAcceptedVoteStore();
        var outbound = new InMemoryOutboundMessengerClient();
        var now = new DateTime(2026, 4, 4, 12, 0, 0, DateTimeKind.Utc);
        var processor = CreateProcessor(
            dedupe,
            cooldowns,
            normalizedEvents,
            votes,
            outboundMessengerClient: outbound,
            timeProvider: new FakeTimeProvider(now));

        await processor.ProcessAsync(CreateMessageEvent("mid-1", "user-1", "page-1", "GET_STARTED", now), CancellationToken.None);
        var votePayload = Assert.Single(Assert.Single(outbound.GenericTemplates).Elements).Buttons.Single().Payload;

        await processor.ProcessAsync(CreatePostbackEvent("pb-1", "user-1", "page-1", votePayload, now.AddSeconds(3)), CancellationToken.None);

        Assert.Empty(votes.Items);
        Assert.Equal(2, outbound.GenericTemplates.Count);
        var confirmation = outbound.GenericTemplates.Last();
        var element = Assert.Single(confirmation.Elements);
        Assert.Equal(3, element.Buttons.Count);
        Assert.Contains(element.Buttons, button => button.Payload.StartsWith("CONFIRM1:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessAsync_ConfirmationAccepted_PersistsVote_ShowId_AndCooldown()
    {
        var dedupe = new InMemoryDedupeStore();
        var cooldowns = new InMemoryVoteCooldownStore();
        var normalizedEvents = new InMemoryNormalizedEventStore();
        var votes = new InMemoryAcceptedVoteStore();
        var outbound = new InMemoryOutboundMessengerClient();
        var now = new DateTime(2026, 4, 4, 12, 0, 0, DateTimeKind.Utc);
        var processor = CreateProcessor(
            dedupe,
            cooldowns,
            normalizedEvents,
            votes,
            outboundMessengerClient: outbound,
            timeProvider: new FakeTimeProvider(now));

        await processor.ProcessAsync(CreateMessageEvent("mid-1", "user-1", "page-1", "GET_STARTED", now), CancellationToken.None);
        var votePayload = Assert.Single(Assert.Single(outbound.GenericTemplates).Elements).Buttons.Single().Payload;
        await processor.ProcessAsync(CreatePostbackEvent("pb-1", "user-1", "page-1", votePayload, now.AddSeconds(2)), CancellationToken.None);

        var confirmationPrompt = outbound.GenericTemplates.Last();
        var acceptedPayload = Assert.Single(confirmationPrompt.Elements)
            .Buttons
            .Single(button => DecodeSignedBody(button.Payload).Contains("ACCEPT", StringComparison.OrdinalIgnoreCase));

        await processor.ProcessAsync(CreatePostbackEvent("pb-2", "user-1", "page-1", acceptedPayload.Payload, now.AddSeconds(5)), CancellationToken.None);

        var vote = Assert.Single(votes.Items);
        Assert.Equal("user-1", vote.UserId);
        Assert.Equal("page-1", vote.RecipientId);
        Assert.Equal(ActiveShowId, vote.ShowId);
        Assert.Equal("candidate-1", vote.CandidateId);
        Assert.Equal("Known user-1", vote.UserAccountName);

        var cooldown = await cooldowns.GetAsync(ActiveShowId, "user-1", "page-1", CancellationToken.None);
        Assert.NotNull(cooldown);
        Assert.Equal(now, cooldown!.LastVotedUtc);

        var acceptedText = Assert.Single(outbound.TextMessages);
        Assert.Contains("Candidate 1", acceptedText.MessageText, StringComparison.Ordinal);
        Assert.Equal(3, normalizedEvents.Items.Count);
    }

    [Fact]
    public async Task ProcessAsync_StaleShowVotePayload_RecoversWithFreshCarousel()
    {
        var dedupe = new InMemoryDedupeStore();
        var cooldowns = new InMemoryVoteCooldownStore();
        var normalizedEvents = new InMemoryNormalizedEventStore();
        var votes = new InMemoryAcceptedVoteStore();
        var outbound = new InMemoryOutboundMessengerClient();
        var gate = new InMemoryVotingGateService(true, ActiveShowId);
        var processor = CreateProcessor(dedupe, cooldowns, normalizedEvents, votes, outboundMessengerClient: outbound, votingGateService: gate);

        await processor.ProcessAsync(CreateMessageEvent("mid-1", "user-1", "page-1", "GET_STARTED"), CancellationToken.None);
        var stalePayload = Assert.Single(Assert.Single(outbound.GenericTemplates).Elements).Buttons.Single().Payload;
        await gate.SetStateAsync(new VotingRuntimeState(true, "show-2027"), CancellationToken.None);

        await processor.ProcessAsync(CreatePostbackEvent("pb-1", "user-1", "page-1", stalePayload), CancellationToken.None);

        Assert.Empty(votes.Items);
        Assert.Equal(2, outbound.GenericTemplates.Count);
        var recoveryPayload = Assert.Single(outbound.GenericTemplates.Last().Elements).Buttons.Single().Payload;
        Assert.Contains("show-2027", DecodeSignedBody(recoveryPayload));
    }

    [Fact]
    public async Task ProcessAsync_ForgetMeConfirmation_DeletesUserDataAndReplies()
    {
        var dedupe = new InMemoryDedupeStore();
        var cooldowns = new InMemoryVoteCooldownStore();
        var normalizedEvents = new InMemoryNormalizedEventStore();
        var votes = new InMemoryAcceptedVoteStore();
        var accountNames = new InMemoryUserAccountNameStore();
        await accountNames.SetAsync("user-1", "Known user-1", TimeSpan.FromDays(7), CancellationToken.None);
        await cooldowns.SaveAsync(new VoteCooldownSnapshot(ActiveShowId, "user-1", "page-1", new DateTime(2026, 4, 4, 12, 0, 0, DateTimeKind.Utc)), TimeSpan.FromMinutes(5), CancellationToken.None);
        await normalizedEvents.TryAddAsync(CreateMessageEvent("seed-mid", "user-1", "page-1", "hello"), CancellationToken.None);
        await votes.TryAddAsync(new AcceptedVote(Guid.NewGuid(), "corr", "user-1", "page-1", ActiveShowId, "candidate-1", "Candidate 1", "seed-source", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(5), "Messenger", null, "Known user-1"), CancellationToken.None);
        var erasure = new InMemoryUserDataErasureService(normalizedEvents, votes, cooldowns, accountNames);
        var outbound = new InMemoryOutboundMessengerClient();
        var processor = CreateProcessor(dedupe, cooldowns, normalizedEvents, votes, outboundMessengerClient: outbound, userDataErasureService: erasure, userAccountNameStore: accountNames);

        await processor.ProcessAsync(CreateMessageEvent("forgetme-mid", "user-1", "page-1", "#forgetme"), CancellationToken.None);
        var prompt = Assert.Single(outbound.ButtonTemplates);
        var confirmPayload = Assert.Single(prompt.Buttons.Where(button => DecodeSignedBody(button.Payload).Contains("ACCEPT", StringComparison.OrdinalIgnoreCase))).Payload;

        await processor.ProcessAsync(CreatePostbackEvent("forgetme-pb", "user-1", "page-1", confirmPayload), CancellationToken.None);

        Assert.Equal("user-1", erasure.LastErasedUserId);
        Assert.Empty(votes.Items);
        Assert.Empty(normalizedEvents.Items);
        Assert.Null(await accountNames.GetAsync("user-1", CancellationToken.None));
        Assert.Null(await cooldowns.GetAsync(ActiveShowId, "user-1", "page-1", CancellationToken.None));
        Assert.Contains(outbound.TextMessages, item => string.Equals(item.MessageText, new MessengerContentOptions().ForgetMeDeletedText, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessAsync_InvalidConfirmationPayload_RecoversWithNewCarousel()
    {
        var processor = CreateProcessor(
            new InMemoryDedupeStore(),
            new InMemoryVoteCooldownStore(),
            new InMemoryNormalizedEventStore(),
            new InMemoryAcceptedVoteStore(),
            outboundMessengerClient: new InMemoryOutboundMessengerClient());

        var outbound = (InMemoryOutboundMessengerClient)GetPrivateOutbound(processor);
        await processor.ProcessAsync(CreatePostbackEvent("pb-invalid", "user-1", "page-1", "CONFIRM1:bad:bad"), CancellationToken.None);

        Assert.Single(outbound.GenericTemplates);
    }

    private static object GetPrivateOutbound(NormalizedEventProcessor processor)
    {
        var field = typeof(NormalizedEventProcessor).GetField("_outboundMessengerClient", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return field!.GetValue(processor)!;
    }

    private static NormalizedEventProcessor CreateProcessor(
        IEventDeduplicationStore dedupe,
        InMemoryVoteCooldownStore cooldowns,
        InMemoryNormalizedEventStore normalizedEvents,
        InMemoryAcceptedVoteStore votes,
        InMemoryOutboundMessengerClient? outboundMessengerClient = null,
        InMemoryVotingGateService? votingGateService = null,
        TimeProvider? timeProvider = null,
        IUserDataErasureService? userDataErasureService = null,
        InMemoryUserAccountNameStore? userAccountNameStore = null)
    {
        outboundMessengerClient ??= new InMemoryOutboundMessengerClient();
        votingGateService ??= new InMemoryVotingGateService(true, ActiveShowId);
        userAccountNameStore ??= new InMemoryUserAccountNameStore();
        var resolver = new InMemoryUserAccountNameResolver(userAccountNameStore);

        return new NormalizedEventProcessor(
            dedupe,
            votingGateService,
            new SingleUseLockManager(),
            cooldowns,
            normalizedEvents,
            votes,
            resolver,
            userDataErasureService ?? new InMemoryUserDataErasureService(normalizedEvents, votes, cooldowns, userAccountNameStore),
            new TestRuntimeMetricsCollector(),
            outboundMessengerClient,
            new StaticOptionsMonitor<VotingWorkflowOptions>(CreateWorkflowOptions()),
            new StaticOptionsMonitor<DataErasureOptions>(new DataErasureOptions
            {
                ConfirmationPayloadSecret = ErasureSecret,
                ConfirmationTimeout = TimeSpan.FromSeconds(120)
            }),
            new StaticOptionsMonitor<CandidatesOptions>(CreateCandidatesOptions()),
            new StaticOptionsMonitor<MessengerContentOptions>(new MessengerContentOptions()),
            timeProvider ?? new FakeTimeProvider(new DateTime(2026, 4, 4, 12, 0, 0, DateTimeKind.Utc)),
            NullLogger<NormalizedEventProcessor>.Instance);
    }

    private static VotingWorkflowOptions CreateWorkflowOptions() => new()
    {
        ConfirmationTimeout = TimeSpan.FromSeconds(120),
        Cooldown = TimeSpan.FromMinutes(5),
        ProcessedEventRetention = TimeSpan.FromDays(1),
        ProcessingLockTimeout = TimeSpan.FromSeconds(30),
        RequireConfirmationForAll = true,
        PayloadSignatureSecret = VoteSecret,
        CooldownResponseMode = CooldownResponseMode.Message,
        VoteStartTokens = new List<string> { "GET_STARTED" }
    };

    private static CandidatesOptions CreateCandidatesOptions() => new()
    {
        PublicBaseUrl = "https://example.test",
        AssetBasePath = "assets/L1",
        Items = new List<CandidateDefinition>
        {
            new()
            {
                Id = "candidate-1",
                DisplayName = "Candidate 1",
                Image = "candidate-1.png",
                Phone = "903300301",
                Enabled = true
            }
        }
    };

    private static NormalizedMessengerEvent CreateMessageEvent(string eventId, string senderId, string recipientId, string text, DateTime? occurredAtUtc = null)
        => new(
            eventId,
            MessengerEventType.Message,
            eventId + "-message",
            senderId,
            recipientId,
            occurredAtUtc ?? new DateTime(2026, 4, 4, 12, 0, 0, DateTimeKind.Utc),
            JsonSerializer.Serialize(new { message = new { text } }),
            Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private static NormalizedMessengerEvent CreatePostbackEvent(string eventId, string senderId, string recipientId, string payload, DateTime? occurredAtUtc = null)
        => new(
            eventId,
            MessengerEventType.Postback,
            eventId + "-postback",
            senderId,
            recipientId,
            occurredAtUtc ?? new DateTime(2026, 4, 4, 12, 0, 5, DateTimeKind.Utc),
            JsonSerializer.Serialize(new { postback = new { payload } }),
            Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private static string DecodeSignedBody(string payload)
    {
        var parts = payload.Split(':', 3, StringSplitOptions.None);
        var normalized = parts[1].Replace('-', '+').Replace('_', '/');
        var padding = normalized.Length % 4;
        if (padding > 0)
        {
            normalized = normalized.PadRight(normalized.Length + (4 - padding), '=');
        }

        return Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
    }

    private sealed class InMemoryDedupeStore : IEventDeduplicationStore
    {
        private readonly HashSet<string> _processed = new(StringComparer.OrdinalIgnoreCase);

        public ValueTask<bool> IsProcessedAsync(string eventId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return ValueTask.FromResult(_processed.Contains(eventId));
        }

        public ValueTask MarkProcessedAsync(string eventId, TimeSpan retention, CancellationToken cancellationToken)
        {
            _ = retention;
            _ = cancellationToken;
            _processed.Add(eventId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InMemoryVoteCooldownStore : IVoteCooldownStore
    {
        private readonly Dictionary<string, VoteCooldownSnapshot> _items = new(StringComparer.OrdinalIgnoreCase);

        public ValueTask<VoteCooldownSnapshot?> GetAsync(string showId, string userId, string recipientId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            _items.TryGetValue(Key(showId, userId, recipientId), out var snapshot);
            return ValueTask.FromResult(snapshot);
        }

        public ValueTask SaveAsync(VoteCooldownSnapshot snapshot, TimeSpan retention, CancellationToken cancellationToken)
        {
            _ = retention;
            _ = cancellationToken;
            _items[Key(snapshot.ShowId, snapshot.UserId, snapshot.RecipientId)] = snapshot;
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveAsync(string showId, string userId, string recipientId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            _items.Remove(Key(showId, userId, recipientId));
            return ValueTask.CompletedTask;
        }

        public int RemoveByUserId(string userId)
        {
            var keys = _items.Where(item => string.Equals(item.Value.UserId, userId, StringComparison.OrdinalIgnoreCase)).Select(item => item.Key).ToArray();
            foreach (var key in keys)
            {
                _items.Remove(key);
            }

            return keys.Length;
        }

        private static string Key(string showId, string userId, string recipientId) => $"{showId}:{recipientId}:{userId}";
    }

    private sealed class InMemoryNormalizedEventStore : INormalizedEventStore
    {
        private readonly Dictionary<string, NormalizedMessengerEvent> _items = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<NormalizedMessengerEvent> Items => _items.Values;

        public ValueTask<bool> TryAddAsync(NormalizedMessengerEvent normalizedEvent, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            if (_items.ContainsKey(normalizedEvent.EventId))
            {
                return ValueTask.FromResult(false);
            }

            _items[normalizedEvent.EventId] = normalizedEvent;
            return ValueTask.FromResult(true);
        }

        public int DeleteBySenderId(string userId)
        {
            var keys = _items.Values
                .Where(item => string.Equals(item.SenderId, userId, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.EventId)
                .ToArray();

            foreach (var key in keys)
            {
                _items.Remove(key);
            }

            return keys.Length;
        }
    }

    private sealed class InMemoryAcceptedVoteStore : IAcceptedVoteStore
    {
        private readonly Dictionary<string, AcceptedVote> _items = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<AcceptedVote> Items => _items.Values;

        public ValueTask<bool> TryAddAsync(AcceptedVote vote, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            if (_items.ContainsKey(vote.SourceEventId))
            {
                return ValueTask.FromResult(false);
            }

            _items[vote.SourceEventId] = vote;
            return ValueTask.FromResult(true);
        }

        public int DeleteByUserId(string userId)
        {
            var keys = _items.Values
                .Where(item => string.Equals(item.UserId, userId, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.SourceEventId)
                .ToArray();

            foreach (var key in keys)
            {
                _items.Remove(key);
            }

            return keys.Length;
        }
    }

    private sealed class InMemoryUserAccountNameStore : IUserAccountNameStore
    {
        private readonly Dictionary<string, string> _items = new(StringComparer.OrdinalIgnoreCase);

        public ValueTask<string?> GetAsync(string userId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return ValueTask.FromResult<string?>(_items.TryGetValue(userId, out var value) ? value : null);
        }

        public ValueTask SetAsync(string userId, string accountName, TimeSpan retention, CancellationToken cancellationToken)
        {
            _ = retention;
            _ = cancellationToken;
            _items[userId] = accountName;
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveAsync(string userId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            _items.Remove(userId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InMemoryUserAccountNameResolver : IUserAccountNameResolver
    {
        private readonly InMemoryUserAccountNameStore _store;

        public InMemoryUserAccountNameResolver(InMemoryUserAccountNameStore store)
        {
            _store = store;
        }

        public int Calls { get; private set; }

        public async ValueTask<string?> GetOrResolveAsync(string userId, CancellationToken cancellationToken)
        {
            Calls++;
            var cached = await _store.GetAsync(userId, cancellationToken);
            if (cached is not null)
            {
                return cached;
            }

            var value = $"Known {userId}";
            await _store.SetAsync(userId, value, TimeSpan.FromDays(7), cancellationToken);
            return value;
        }
    }

    private sealed class InMemoryUserDataErasureService : IUserDataErasureService
    {
        private readonly InMemoryNormalizedEventStore _normalizedEvents;
        private readonly InMemoryAcceptedVoteStore _acceptedVotes;
        private readonly InMemoryVoteCooldownStore _cooldowns;
        private readonly InMemoryUserAccountNameStore _accountNames;

        public InMemoryUserDataErasureService(InMemoryNormalizedEventStore normalizedEvents, InMemoryAcceptedVoteStore acceptedVotes, InMemoryVoteCooldownStore cooldowns, InMemoryUserAccountNameStore accountNames)
        {
            _normalizedEvents = normalizedEvents;
            _acceptedVotes = acceptedVotes;
            _cooldowns = cooldowns;
            _accountNames = accountNames;
        }

        public string? LastErasedUserId { get; private set; }

        public async ValueTask<UserDataErasureResult> EraseUserDataAsync(string userId, CancellationToken cancellationToken)
        {
            LastErasedUserId = userId;
            var normalizedDeleted = _normalizedEvents.DeleteBySenderId(userId);
            var votesDeleted = _acceptedVotes.DeleteByUserId(userId);
            _cooldowns.RemoveByUserId(userId);
            await _accountNames.RemoveAsync(userId, cancellationToken);
            return new UserDataErasureResult(normalizedDeleted, votesDeleted);
        }
    }

    private sealed class InMemoryVotingGateService : IVotingGateService
    {
        private VotingRuntimeState _state;

        public InMemoryVotingGateService(bool votingStarted, string? activeShowId)
        {
            _state = new VotingRuntimeState(votingStarted, activeShowId);
        }

        public ValueTask<VotingRuntimeState> GetStateAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return ValueTask.FromResult(_state);
        }

        public ValueTask SetStateAsync(VotingRuntimeState state, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            _state = state;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> IsVotingStartedAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return ValueTask.FromResult(_state.VotingStarted);
        }

        public ValueTask SetVotingStartedAsync(bool started, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            _state = _state with { VotingStarted = started };
            return ValueTask.CompletedTask;
        }

        public ValueTask<string?> GetActiveShowIdAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return ValueTask.FromResult(_state.ActiveShowId);
        }

        public ValueTask SetActiveShowIdAsync(string? activeShowId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            _state = _state with { ActiveShowId = activeShowId };
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InMemoryOutboundMessengerClient : IOutboundMessengerClient
    {
        public List<TextMessageSend> TextMessages { get; } = new();
        public List<ButtonTemplateSend> ButtonTemplates { get; } = new();
        public List<GenericTemplateSend> GenericTemplates { get; } = new();
        public bool ResultToReturn { get; set; } = true;

        public ValueTask<bool> SendTextAsync(string recipientId, string messageText, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            TextMessages.Add(new TextMessageSend(recipientId, messageText));
            return ValueTask.FromResult(ResultToReturn);
        }

        public ValueTask<bool> SendButtonTemplateAsync(string recipientId, string promptText, IReadOnlyCollection<MessengerPostbackButton> buttons, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            ButtonTemplates.Add(new ButtonTemplateSend(recipientId, promptText, buttons.ToArray()));
            return ValueTask.FromResult(ResultToReturn);
        }

        public ValueTask<bool> SendGenericTemplateAsync(string recipientId, IReadOnlyCollection<MessengerGenericTemplateElement> elements, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            GenericTemplates.Add(new GenericTemplateSend(recipientId, elements.ToArray()));
            return ValueTask.FromResult(ResultToReturn);
        }
    }

    private sealed record TextMessageSend(string RecipientId, string MessageText);
    private sealed record ButtonTemplateSend(string RecipientId, string PromptText, IReadOnlyCollection<MessengerPostbackButton> Buttons);
    private sealed record GenericTemplateSend(string RecipientId, IReadOnlyCollection<MessengerGenericTemplateElement> Elements);

    private sealed class SingleUseLockManager : IUserProcessingLockManager
    {
        public ValueTask<IDistributedLockHandle?> TryAcquireAsync(string scope, TimeSpan ttl, CancellationToken cancellationToken)
        {
            _ = scope;
            _ = ttl;
            _ = cancellationToken;
            return ValueTask.FromResult<IDistributedLockHandle?>(new NoOpLockHandle());
        }
    }

    private sealed class NoOpLockHandle : IDistributedLockHandle
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T currentValue)
        {
            CurrentValue = currentValue;
        }

        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener)
        {
            _ = listener;
            return null;
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FakeTimeProvider(DateTime utcNow)
        {
            _utcNow = new DateTimeOffset(utcNow, TimeSpan.Zero);
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class TestRuntimeMetricsCollector : IRuntimeMetricsCollector
    {
        public void Increment(string metricName, long value = 1) { }
        public void ObserveDuration(string metricName, double milliseconds) { }
        public void ObserveValue(string metricName, double value) { }
        public void SetGauge(string metricName, double value) { }
        public RuntimeMetricsSnapshot CreateSnapshot() => new("Test", "instance", "machine", "Test", 0, DateTime.UtcNow, new(), new(), new());
    }
}

