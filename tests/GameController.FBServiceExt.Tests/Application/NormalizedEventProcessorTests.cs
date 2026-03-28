using GameController.FBServiceExt.Application.Abstractions.Persistence;
using GameController.FBServiceExt.Application.Abstractions.State;
using GameController.FBServiceExt.Application.Contracts.Normalization;
using GameController.FBServiceExt.Application.Contracts.Runtime;
using GameController.FBServiceExt.Application.Contracts.Votes;
using GameController.FBServiceExt.Application.Options;
using GameController.FBServiceExt.Application.Services;
using GameController.FBServiceExt.Application.Services.Observability;
using GameController.FBServiceExt.Domain.Messaging;
using GameController.FBServiceExt.Domain.Voting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GameController.FBServiceExt.Tests.Application;

public sealed class NormalizedEventProcessorTests
{
    [Fact]
    public async Task ProcessAsync_MessageVoteStart_CreatesOptionsSentSession()
    {
        var dedupe = new InMemoryDedupeStore();
        var sessions = new InMemoryVoteSessionStore();
        var normalizedEvents = new InMemoryNormalizedEventStore();
        var votes = new InMemoryAcceptedVoteStore();
        var processor = CreateProcessor(dedupe, sessions, normalizedEvents, votes);

        var normalizedEvent = new NormalizedMessengerEvent(
            EventId: "mid-1",
            EventType: MessengerEventType.Message,
            MessageId: "mid-1",
            SenderId: "user-1",
            RecipientId: "page-1",
            OccurredAtUtc: new DateTime(2026, 3, 28, 0, 0, 0, DateTimeKind.Utc),
            PayloadJson: "{\"message\":{\"text\":\"GET_STARTED\"}}",
            RawEnvelopeId: Guid.NewGuid());

        await processor.ProcessAsync(normalizedEvent, CancellationToken.None);

        var snapshot = await sessions.GetAsync("user-1", "page-1", CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.Equal(VoteState.OptionsSent, snapshot!.State);
        Assert.True(await dedupe.IsProcessedAsync("mid-1", CancellationToken.None));
        Assert.Contains(normalizedEvents.Items, item => item.EventId == "mid-1");
        Assert.Empty(votes.Items);
    }

    [Fact]
    public async Task ProcessAsync_PostbackCandidateSelection_MovesSessionToConfirmationPending()
    {
        var dedupe = new InMemoryDedupeStore();
        var sessions = new InMemoryVoteSessionStore();
        var normalizedEvents = new InMemoryNormalizedEventStore();
        var votes = new InMemoryAcceptedVoteStore();
        var now = new DateTime(2026, 3, 28, 0, 0, 0, DateTimeKind.Utc);
        await sessions.SaveAsync(new VoteSessionSnapshot(
            "user-1",
            "page-1",
            VoteState.OptionsSent,
            CandidateId: null,
            CandidateDisplayName: null,
            LastEventId: "prev",
            CreatedAtUtc: now,
            UpdatedAtUtc: now,
            ExpiresAtUtc: now.AddMinutes(10),
            CooldownUntilUtc: null), CancellationToken.None);

        var processor = CreateProcessor(dedupe, sessions, normalizedEvents, votes);
        var normalizedEvent = new NormalizedMessengerEvent(
            EventId: "pb-1",
            EventType: MessengerEventType.Postback,
            MessageId: null,
            SenderId: "user-1",
            RecipientId: "page-1",
            OccurredAtUtc: now.AddSeconds(5),
            PayloadJson: "{\"postback\":{\"payload\":\"candidate-1\"}}",
            RawEnvelopeId: Guid.NewGuid());

        await processor.ProcessAsync(normalizedEvent, CancellationToken.None);

        var snapshot = await sessions.GetAsync("user-1", "page-1", CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.Equal(VoteState.ConfirmationPending, snapshot!.State);
        Assert.Equal("candidate-1", snapshot.CandidateId);
        Assert.Equal("Candidate 1", snapshot.CandidateDisplayName);
        Assert.Contains(normalizedEvents.Items, item => item.EventId == "pb-1");
    }

    [Fact]
    public async Task ProcessAsync_ConfirmationAccepted_PersistsVoteAndActivatesCooldown()
    {
        var dedupe = new InMemoryDedupeStore();
        var sessions = new InMemoryVoteSessionStore();
        var normalizedEvents = new InMemoryNormalizedEventStore();
        var votes = new InMemoryAcceptedVoteStore();
        var now = new DateTime(2026, 3, 28, 0, 0, 0, DateTimeKind.Utc);
        await sessions.SaveAsync(new VoteSessionSnapshot(
            "user-1",
            "page-1",
            VoteState.ConfirmationPending,
            CandidateId: "candidate-1",
            CandidateDisplayName: "Candidate 1",
            LastEventId: "prev",
            CreatedAtUtc: now.AddMinutes(-1),
            UpdatedAtUtc: now.AddSeconds(-10),
            ExpiresAtUtc: now.AddMinutes(2),
            CooldownUntilUtc: null), CancellationToken.None);

        var processor = CreateProcessor(dedupe, sessions, normalizedEvents, votes);
        var normalizedEvent = new NormalizedMessengerEvent(
            EventId: "pb-confirm-1",
            EventType: MessengerEventType.Postback,
            MessageId: null,
            SenderId: "user-1",
            RecipientId: "page-1",
            OccurredAtUtc: now.AddSeconds(15),
            PayloadJson: "{\"postback\":{\"payload\":\"YES\"}}",
            RawEnvelopeId: Guid.Parse("11111111-1111-1111-1111-111111111111"));

        await processor.ProcessAsync(normalizedEvent, CancellationToken.None);

        var snapshot = await sessions.GetAsync("user-1", "page-1", CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.Equal(VoteState.CooldownActive, snapshot!.State);
        Assert.Equal(now.AddMinutes(5), snapshot.CooldownUntilUtc);
        Assert.Equal(now.AddMinutes(5), snapshot.ExpiresAtUtc);

        var vote = Assert.Single(votes.Items);
        Assert.Equal("user-1", vote.UserId);
        Assert.Equal("page-1", vote.RecipientId);
        Assert.Equal("candidate-1", vote.CandidateId);
        Assert.Equal("Candidate 1", vote.CandidateDisplayName);
        Assert.Equal("pb-confirm-1", vote.SourceEventId);
        Assert.Equal("11111111111111111111111111111111", vote.CorrelationId);
        Assert.Contains(normalizedEvents.Items, item => item.EventId == "pb-confirm-1");
        Assert.True(await dedupe.IsProcessedAsync("pb-confirm-1", CancellationToken.None));
    }

    private static NormalizedEventProcessor CreateProcessor(
        InMemoryDedupeStore dedupe,
        InMemoryVoteSessionStore sessions,
        InMemoryNormalizedEventStore normalizedEvents,
        InMemoryAcceptedVoteStore votes)
    {
        return new NormalizedEventProcessor(
            dedupe,
            new SingleUseLockManager(),
            sessions,
            normalizedEvents,
            votes,
            new NullRuntimeMetricsCollector(),
            new StaticOptionsMonitor<VotingWorkflowOptions>(new VotingWorkflowOptions()),
            new StaticOptionsMonitor<CandidatesOptions>(new CandidatesOptions
            {
                Items = new List<CandidateDefinition>
                {
                    new() { Id = "candidate-1", DisplayName = "Candidate 1", Enabled = true },
                    new() { Id = "candidate-2", DisplayName = "Candidate 2", Enabled = true }
                }
            }),
            new FakeTimeProvider(new DateTimeOffset(new DateTime(2026, 3, 28, 0, 0, 0, DateTimeKind.Utc))),
            NullLogger<NormalizedEventProcessor>.Instance);
    }

    private sealed class InMemoryDedupeStore : IEventDeduplicationStore
    {
        private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);

        public ValueTask<bool> IsProcessedAsync(string eventId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return ValueTask.FromResult(_keys.Contains(eventId));
        }

        public ValueTask MarkProcessedAsync(string eventId, TimeSpan retention, CancellationToken cancellationToken)
        {
            _ = retention;
            _ = cancellationToken;
            _keys.Add(eventId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InMemoryVoteSessionStore : IVoteSessionStore
    {
        private readonly Dictionary<string, VoteSessionSnapshot> _store = new(StringComparer.OrdinalIgnoreCase);

        public ValueTask<VoteSessionSnapshot?> GetAsync(string userId, string recipientId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            _store.TryGetValue(Key(userId, recipientId), out var snapshot);
            return ValueTask.FromResult(snapshot);
        }

        public ValueTask SaveAsync(VoteSessionSnapshot snapshot, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            _store[Key(snapshot.UserId, snapshot.RecipientId)] = snapshot;
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveAsync(string userId, string recipientId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            _store.Remove(Key(userId, recipientId));
            return ValueTask.CompletedTask;
        }

        private static string Key(string userId, string recipientId) => $"{recipientId}:{userId}";
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
    }

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

        public FakeTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}


