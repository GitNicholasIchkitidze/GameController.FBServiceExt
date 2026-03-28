using System.Text.Json;
using GameController.FBServiceExt.Application.Abstractions.Observability;
using GameController.FBServiceExt.Application.Abstractions.Persistence;
using GameController.FBServiceExt.Application.Abstractions.Processing;
using GameController.FBServiceExt.Application.Abstractions.State;
using GameController.FBServiceExt.Application.Contracts.Normalization;
using GameController.FBServiceExt.Application.Contracts.Runtime;
using GameController.FBServiceExt.Application.Contracts.Votes;
using GameController.FBServiceExt.Application.Exceptions;
using GameController.FBServiceExt.Application.Options;
using GameController.FBServiceExt.Domain.Messaging;
using GameController.FBServiceExt.Domain.Voting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameController.FBServiceExt.Application.Services;

public sealed class NormalizedEventProcessor : INormalizedEventProcessor
{
    private const string VoteChannel = "Messenger";

    private readonly IEventDeduplicationStore _eventDeduplicationStore;
    private readonly IUserProcessingLockManager _userProcessingLockManager;
    private readonly IVoteSessionStore _voteSessionStore;
    private readonly INormalizedEventStore _normalizedEventStore;
    private readonly IAcceptedVoteStore _acceptedVoteStore;
    private readonly IRuntimeMetricsCollector _runtimeMetricsCollector;
    private readonly IOptionsMonitor<VotingWorkflowOptions> _workflowOptionsMonitor;
    private readonly IOptionsMonitor<CandidatesOptions> _candidateOptionsMonitor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NormalizedEventProcessor> _logger;

    public NormalizedEventProcessor(
        IEventDeduplicationStore eventDeduplicationStore,
        IUserProcessingLockManager userProcessingLockManager,
        IVoteSessionStore voteSessionStore,
        INormalizedEventStore normalizedEventStore,
        IAcceptedVoteStore acceptedVoteStore,
        IRuntimeMetricsCollector runtimeMetricsCollector,
        IOptionsMonitor<VotingWorkflowOptions> workflowOptionsMonitor,
        IOptionsMonitor<CandidatesOptions> candidateOptionsMonitor,
        TimeProvider timeProvider,
        ILogger<NormalizedEventProcessor> logger)
    {
        _eventDeduplicationStore = eventDeduplicationStore;
        _userProcessingLockManager = userProcessingLockManager;
        _voteSessionStore = voteSessionStore;
        _normalizedEventStore = normalizedEventStore;
        _acceptedVoteStore = acceptedVoteStore;
        _runtimeMetricsCollector = runtimeMetricsCollector;
        _workflowOptionsMonitor = workflowOptionsMonitor;
        _candidateOptionsMonitor = candidateOptionsMonitor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async ValueTask ProcessAsync(NormalizedMessengerEvent normalizedEvent, CancellationToken cancellationToken)
    {
        if (normalizedEvent.EventType is not (MessengerEventType.Message or MessengerEventType.Postback))
        {
            _runtimeMetricsCollector.Increment("worker.processor.ignored");
            return;
        }

        if (string.IsNullOrWhiteSpace(normalizedEvent.SenderId) || string.IsNullOrWhiteSpace(normalizedEvent.RecipientId))
        {
            _runtimeMetricsCollector.Increment("worker.processor.invalid");
            _logger.LogWarning(
                "Skipping normalized event because sender or recipient is missing. EventId: {EventId}, EventType: {EventType}",
                normalizedEvent.EventId,
                normalizedEvent.EventType);
            return;
        }

        if (await _eventDeduplicationStore.IsProcessedAsync(normalizedEvent.EventId, cancellationToken))
        {
            _runtimeMetricsCollector.Increment("worker.processor.duplicates");
            _logger.LogDebug("Skipping duplicate normalized event. EventId: {EventId}", normalizedEvent.EventId);
            return;
        }

        var workflow = _workflowOptionsMonitor.CurrentValue;
        var lockScope = $"{normalizedEvent.RecipientId}:{normalizedEvent.SenderId}";
        await using var processingLock = await _userProcessingLockManager.TryAcquireAsync(lockScope, workflow.ProcessingLockTimeout, cancellationToken);
        if (processingLock is null)
        {
            _runtimeMetricsCollector.Increment("worker.processor.lock_retry");
            throw new RetryableProcessingException($"Failed to acquire vote-processing lock for scope '{lockScope}'.");
        }

        if (await _eventDeduplicationStore.IsProcessedAsync(normalizedEvent.EventId, cancellationToken))
        {
            _runtimeMetricsCollector.Increment("worker.processor.duplicates");
            _logger.LogDebug("Skipping duplicate normalized event after lock acquisition. EventId: {EventId}", normalizedEvent.EventId);
            return;
        }

        _runtimeMetricsCollector.Increment("worker.processor.events_seen");

        _logger.LogDebug(
            "Processing Messenger event. EventId: {EventId}, EventType: {EventType}, SenderId: {SenderId}, RecipientId: {RecipientId}, MessageId: {MessageId}, Summary: {Summary}",
            normalizedEvent.EventId,
            normalizedEvent.EventType,
            normalizedEvent.SenderId,
            normalizedEvent.RecipientId,
            normalizedEvent.MessageId,
            BuildEventSummary(normalizedEvent));

        switch (normalizedEvent.EventType)
        {
            case MessengerEventType.Message:
                await ProcessMessageAsync(normalizedEvent, workflow, cancellationToken);
                break;
            case MessengerEventType.Postback:
                await ProcessPostbackAsync(normalizedEvent, workflow, cancellationToken);
                break;
        }

        var inserted = await _normalizedEventStore.TryAddAsync(normalizedEvent, cancellationToken);
        if (!inserted)
        {
            _logger.LogDebug("Normalized event was already present in SQL storage. EventId: {EventId}", normalizedEvent.EventId);
        }

        await _eventDeduplicationStore.MarkProcessedAsync(normalizedEvent.EventId, workflow.ProcessedEventRetention, cancellationToken);
    }

    private async ValueTask ProcessMessageAsync(
        NormalizedMessengerEvent normalizedEvent,
        VotingWorkflowOptions workflow,
        CancellationToken cancellationToken)
    {
        var text = NormalizedEventPayloadReader.GetMessageText(normalizedEvent.PayloadJson);
        if (string.IsNullOrWhiteSpace(text))
        {
            _runtimeMetricsCollector.Increment("worker.processor.ignored");
            _logger.LogDebug("Ignoring message event without text. EventId: {EventId}", normalizedEvent.EventId);
            return;
        }

        if (!workflow.VoteStartTokens.Any(token => string.Equals(token, text, StringComparison.OrdinalIgnoreCase)))
        {
            _runtimeMetricsCollector.Increment("worker.processor.ignored");
            _logger.LogDebug(
                "Message ignored because it is not a vote-start token. EventId: {EventId}, UserId: {UserId}, Text: {Text}",
                normalizedEvent.EventId,
                normalizedEvent.SenderId,
                TruncateForLog(text));
            return;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var currentSession = await _voteSessionStore.GetAsync(normalizedEvent.SenderId!, normalizedEvent.RecipientId!, cancellationToken);
        if (IsCooldownActive(currentSession, now))
        {
            _runtimeMetricsCollector.Increment("worker.processor.cooldown_ignored");
            _logger.LogDebug(
                "Vote start ignored because cooldown is active. EventId: {EventId}, UserId: {UserId}",
                normalizedEvent.EventId,
                normalizedEvent.SenderId);
            return;
        }

        _ = VoteStateMachine.Transition(VoteState.Idle, VoteState.VoteRequested);
        _ = VoteStateMachine.Transition(VoteState.VoteRequested, VoteState.OptionsSent);

        var expiresAtUtc = now.Add(workflow.OptionsSessionTtl);
        var snapshot = new VoteSessionSnapshot(
            normalizedEvent.SenderId!,
            normalizedEvent.RecipientId!,
            VoteState.OptionsSent,
            CandidateId: null,
            CandidateDisplayName: null,
            LastEventId: normalizedEvent.EventId,
            CreatedAtUtc: currentSession?.CreatedAtUtc ?? now,
            UpdatedAtUtc: now,
            ExpiresAtUtc: expiresAtUtc,
            CooldownUntilUtc: null);

        await _voteSessionStore.SaveAsync(snapshot, cancellationToken);
        _runtimeMetricsCollector.Increment("worker.processor.options_sent");

        _logger.LogDebug(
            "Vote session created and moved to OptionsSent. EventId: {EventId}, UserId: {UserId}, ExpiresAtUtc: {ExpiresAtUtc}",
            normalizedEvent.EventId,
            normalizedEvent.SenderId,
            expiresAtUtc);
    }

    private async ValueTask ProcessPostbackAsync(
        NormalizedMessengerEvent normalizedEvent,
        VotingWorkflowOptions workflow,
        CancellationToken cancellationToken)
    {
        var payload = NormalizedEventPayloadReader.GetPostbackPayload(normalizedEvent.PayloadJson);
        if (string.IsNullOrWhiteSpace(payload))
        {
            _runtimeMetricsCollector.Increment("worker.processor.ignored");
            _logger.LogDebug("Ignoring postback event without payload. EventId: {EventId}", normalizedEvent.EventId);
            return;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var session = await _voteSessionStore.GetAsync(normalizedEvent.SenderId!, normalizedEvent.RecipientId!, cancellationToken);
        if (session is null)
        {
            _runtimeMetricsCollector.Increment("worker.processor.ignored");
            _logger.LogDebug(
                "Postback ignored because no active vote session exists. EventId: {EventId}, UserId: {UserId}, Payload: {Payload}",
                normalizedEvent.EventId,
                normalizedEvent.SenderId,
                TruncateForLog(payload));
            return;
        }

        if (IsCooldownActive(session, now))
        {
            _runtimeMetricsCollector.Increment("worker.processor.cooldown_ignored");
            _logger.LogDebug(
                "Postback ignored because cooldown is active. EventId: {EventId}, UserId: {UserId}",
                normalizedEvent.EventId,
                normalizedEvent.SenderId);
            return;
        }

        if (session.State == VoteState.OptionsSent)
        {
            var candidate = _candidateOptionsMonitor.CurrentValue.Items
                .FirstOrDefault(item => item.Enabled && string.Equals(item.Id, payload, StringComparison.OrdinalIgnoreCase));

            if (candidate is null)
            {
                _runtimeMetricsCollector.Increment("worker.processor.ignored");
                _logger.LogDebug(
                    "Postback ignored because candidate payload is unknown. EventId: {EventId}, UserId: {UserId}, Payload: {Payload}",
                    normalizedEvent.EventId,
                    normalizedEvent.SenderId,
                    TruncateForLog(payload));
                return;
            }

            _ = VoteStateMachine.Transition(VoteState.OptionsSent, VoteState.CandidateSelected);
            _ = VoteStateMachine.Transition(VoteState.CandidateSelected, VoteState.ConfirmationPending);

            var expiresAtUtc = now.Add(workflow.ConfirmationTimeout);
            var confirmationSnapshot = session with
            {
                State = VoteState.ConfirmationPending,
                CandidateId = candidate.Id,
                CandidateDisplayName = candidate.DisplayName,
                LastEventId = normalizedEvent.EventId,
                UpdatedAtUtc = now,
                ExpiresAtUtc = expiresAtUtc
            };

            await _voteSessionStore.SaveAsync(confirmationSnapshot, cancellationToken);
            _runtimeMetricsCollector.Increment("worker.processor.confirmation_pending");

            _logger.LogDebug(
                "Vote session moved to ConfirmationPending. EventId: {EventId}, UserId: {UserId}, CandidateId: {CandidateId}, ExpiresAtUtc: {ExpiresAtUtc}",
                normalizedEvent.EventId,
                normalizedEvent.SenderId,
                candidate.Id,
                expiresAtUtc);

            return;
        }

        if (session.State == VoteState.ConfirmationPending)
        {
            if (workflow.ConfirmationRejectTokens.Any(token => string.Equals(token, payload, StringComparison.OrdinalIgnoreCase)))
            {
                await _voteSessionStore.RemoveAsync(normalizedEvent.SenderId!, normalizedEvent.RecipientId!, cancellationToken);
                _runtimeMetricsCollector.Increment("worker.processor.confirmation_cancelled");
                _logger.LogDebug("Vote session cancelled from confirmation pending. EventId: {EventId}, UserId: {UserId}", normalizedEvent.EventId, normalizedEvent.SenderId);
                return;
            }

            if (workflow.ConfirmationAcceptTokens.Any(token => string.Equals(token, payload, StringComparison.OrdinalIgnoreCase)))
            {
                await AcceptVoteAsync(normalizedEvent, workflow, session, now, cancellationToken);
                return;
            }

            _runtimeMetricsCollector.Increment("worker.processor.ignored");
            _logger.LogDebug(
                "Postback ignored because confirmation token is unknown. EventId: {EventId}, UserId: {UserId}, Payload: {Payload}",
                normalizedEvent.EventId,
                normalizedEvent.SenderId,
                TruncateForLog(payload));
        }
    }

    private async ValueTask AcceptVoteAsync(
        NormalizedMessengerEvent normalizedEvent,
        VotingWorkflowOptions workflow,
        VoteSessionSnapshot session,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.CandidateId) || string.IsNullOrWhiteSpace(session.CandidateDisplayName))
        {
            _runtimeMetricsCollector.Increment("worker.processor.invalid");
            _logger.LogWarning(
                "Cannot accept vote because the confirmation session does not contain candidate details. EventId: {EventId}, UserId: {UserId}",
                normalizedEvent.EventId,
                normalizedEvent.SenderId);
            return;
        }

        var confirmationReceived = VoteStateMachine.Transition(VoteState.ConfirmationPending, VoteState.ConfirmationReceived);
        var votePersisted = VoteStateMachine.Transition(confirmationReceived, VoteState.VotePersisted);
        var userNotified = VoteStateMachine.Transition(votePersisted, VoteState.UserNotified);
        var cooldownActive = VoteStateMachine.Transition(userNotified, VoteState.CooldownActive);
        var cooldownUntilUtc = now.Add(workflow.Cooldown);

        var acceptedVote = new AcceptedVote(
            VoteId: Guid.NewGuid(),
            CorrelationId: normalizedEvent.RawEnvelopeId.ToString("N"),
            UserId: normalizedEvent.SenderId!,
            RecipientId: normalizedEvent.RecipientId!,
            CandidateId: session.CandidateId,
            CandidateDisplayName: session.CandidateDisplayName,
            SourceEventId: normalizedEvent.EventId,
            ConfirmedAtUtc: now,
            CooldownUntilUtc: cooldownUntilUtc,
            Channel: VoteChannel,
            MetadataJson: BuildAcceptedVoteMetadataJson(normalizedEvent, session));

        var inserted = await _acceptedVoteStore.TryAddAsync(acceptedVote, cancellationToken);

        var cooldownSnapshot = session with
        {
            State = cooldownActive,
            LastEventId = normalizedEvent.EventId,
            UpdatedAtUtc = now,
            ExpiresAtUtc = cooldownUntilUtc,
            CooldownUntilUtc = cooldownUntilUtc
        };

        await _voteSessionStore.SaveAsync(cooldownSnapshot, cancellationToken);
        _runtimeMetricsCollector.Increment(inserted ? "worker.processor.vote_accepted" : "worker.processor.vote_accepted_reconciled");

        _logger.LogInformation(
            inserted
                ? "Vote accepted, persisted, and moved to CooldownActive. EventId: {EventId}, UserId: {UserId}, CandidateId: {CandidateId}, CooldownUntilUtc: {CooldownUntilUtc}"
                : "Vote acceptance event was already persisted, and the session was reconciled to CooldownActive. EventId: {EventId}, UserId: {UserId}, CandidateId: {CandidateId}, CooldownUntilUtc: {CooldownUntilUtc}",
            normalizedEvent.EventId,
            normalizedEvent.SenderId,
            session.CandidateId,
            cooldownUntilUtc);

        // TODO: publish accepted-vote downstream event or SignalR notification from here.
    }

    private static string BuildAcceptedVoteMetadataJson(NormalizedMessengerEvent normalizedEvent, VoteSessionSnapshot session)
    {
        return JsonSerializer.Serialize(new
        {
            normalizedEvent.EventId,
            normalizedEvent.MessageId,
            normalizedEvent.RawEnvelopeId,
            normalizedEvent.EventType,
            normalizedEvent.OccurredAtUtc,
            session.CreatedAtUtc,
            session.CandidateId,
            session.CandidateDisplayName
        });
    }

    private static string BuildEventSummary(NormalizedMessengerEvent normalizedEvent)
    {
        return normalizedEvent.EventType switch
        {
            MessengerEventType.Message => $"text={TruncateForLog(NormalizedEventPayloadReader.GetMessageText(normalizedEvent.PayloadJson))}",
            MessengerEventType.Postback => $"payload={TruncateForLog(NormalizedEventPayloadReader.GetPostbackPayload(normalizedEvent.PayloadJson))}",
            _ => normalizedEvent.EventType.ToString()
        };
    }

    private static string TruncateForLog(string? value, int maxLength = 80)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "<empty>";
        }

        var normalized = value.Replace(Environment.NewLine, " ", StringComparison.Ordinal).Trim();
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..maxLength] + "...";
    }

    private static bool IsCooldownActive(VoteSessionSnapshot? session, DateTime now)
    {
        return session is not null &&
               session.State == VoteState.CooldownActive &&
               session.CooldownUntilUtc.HasValue &&
               session.CooldownUntilUtc.Value > now;
    }
}
