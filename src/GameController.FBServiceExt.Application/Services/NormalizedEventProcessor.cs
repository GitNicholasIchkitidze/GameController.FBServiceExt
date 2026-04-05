using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameController.FBServiceExt.Application.Abstractions.Messaging;
using GameController.FBServiceExt.Application.Abstractions.Observability;
using GameController.FBServiceExt.Application.Abstractions.Persistence;
using GameController.FBServiceExt.Application.Abstractions.Processing;
using GameController.FBServiceExt.Application.Abstractions.State;
using GameController.FBServiceExt.Application.Contracts.Emoji;
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
    private const string VotePayloadPrefix = "VOTE1";
    private const string ConfirmationPayloadPrefix = "CONFIRM1";
    private const string DataErasurePayloadPrefix = "ERASE1";

    private readonly IEventDeduplicationStore _eventDeduplicationStore;
    private readonly IVotingGateService _votingGateService;
    private readonly IUserProcessingLockManager _userProcessingLockManager;
    private readonly IVoteCooldownStore _voteCooldownStore;
    private readonly INormalizedEventStore _normalizedEventStore;
    private readonly IAcceptedVoteStore _acceptedVoteStore;
    private readonly IUserAccountNameResolver _userAccountNameResolver;
    private readonly IUserDataErasureService _userDataErasureService;
    private readonly IRuntimeMetricsCollector _runtimeMetricsCollector;
    private readonly IOutboundMessengerClient _outboundMessengerClient;
    private readonly IOptionsMonitor<VotingWorkflowOptions> _workflowOptionsMonitor;
    private readonly IOptionsMonitor<DataErasureOptions> _dataErasureOptionsMonitor;
    private readonly IOptionsMonitor<CandidatesOptions> _candidateOptionsMonitor;
    private readonly IOptionsMonitor<MessengerContentOptions> _messengerContentOptionsMonitor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NormalizedEventProcessor> _logger;

    public NormalizedEventProcessor(
        IEventDeduplicationStore eventDeduplicationStore,
        IVotingGateService votingGateService,
        IUserProcessingLockManager userProcessingLockManager,
        IVoteCooldownStore voteCooldownStore,
        INormalizedEventStore normalizedEventStore,
        IAcceptedVoteStore acceptedVoteStore,
        IUserAccountNameResolver userAccountNameResolver,
        IUserDataErasureService userDataErasureService,
        IRuntimeMetricsCollector runtimeMetricsCollector,
        IOutboundMessengerClient outboundMessengerClient,
        IOptionsMonitor<VotingWorkflowOptions> workflowOptionsMonitor,
        IOptionsMonitor<DataErasureOptions> dataErasureOptionsMonitor,
        IOptionsMonitor<CandidatesOptions> candidateOptionsMonitor,
        IOptionsMonitor<MessengerContentOptions> messengerContentOptionsMonitor,
        TimeProvider timeProvider,
        ILogger<NormalizedEventProcessor> logger)
    {
        _eventDeduplicationStore = eventDeduplicationStore;
        _votingGateService = votingGateService;
        _userProcessingLockManager = userProcessingLockManager;
        _voteCooldownStore = voteCooldownStore;
        _normalizedEventStore = normalizedEventStore;
        _acceptedVoteStore = acceptedVoteStore;
        _userAccountNameResolver = userAccountNameResolver;
        _userDataErasureService = userDataErasureService;
        _runtimeMetricsCollector = runtimeMetricsCollector;
        _outboundMessengerClient = outboundMessengerClient;
        _workflowOptionsMonitor = workflowOptionsMonitor;
        _dataErasureOptionsMonitor = dataErasureOptionsMonitor;
        _candidateOptionsMonitor = candidateOptionsMonitor;
        _messengerContentOptionsMonitor = messengerContentOptionsMonitor;
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

        var userAccountName = await _userAccountNameResolver.GetOrResolveAsync(normalizedEvent.SenderId!, cancellationToken);

        var result = normalizedEvent.EventType switch
        {
            MessengerEventType.Message => await ProcessMessageAsync(normalizedEvent, workflow, cancellationToken),
            MessengerEventType.Postback => await ProcessPostbackAsync(normalizedEvent, workflow, userAccountName, cancellationToken),
            _ => default
        };

        if (!result.SkipNormalizedEventPersistence)
        {
            await _normalizedEventStore.TryAddAsync(normalizedEvent, cancellationToken);
        }

        await _eventDeduplicationStore.MarkProcessedAsync(normalizedEvent.EventId, workflow.ProcessedEventRetention, cancellationToken);
    }

    private async ValueTask<EventProcessingResult> ProcessMessageAsync(
        NormalizedMessengerEvent normalizedEvent,
        VotingWorkflowOptions workflow,
        CancellationToken cancellationToken)
    {
        var text = NormalizedEventPayloadReader.GetMessageText(normalizedEvent.PayloadJson);
        if (MatchesForgetMeToken(text))
        {
            return await ProcessForgetMeRequestAsync(normalizedEvent, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(text) || !MatchesVoteStartToken(text, workflow))
        {
            _runtimeMetricsCollector.Increment("worker.processor.ignored");
            return default;
        }

        var votingContext = await GetActiveVotingContextOrNotifyInactiveAsync(normalizedEvent.SenderId!, normalizedEvent.RecipientId!, cancellationToken);
        if (votingContext is null)
        {
            return default;
        }

        var now = GetUtcNow();
        if (await TryHandleCooldownAsync(normalizedEvent, workflow, votingContext.ShowId, now, fromPostback: false, cancellationToken))
        {
            return default;
        }

        _runtimeMetricsCollector.Increment("worker.processor.options_sent");
        await TrySendCandidateCarouselAsync(normalizedEvent.SenderId!, normalizedEvent.RecipientId!, votingContext.ShowId, cancellationToken);
        return default;
    }

    private async ValueTask<EventProcessingResult> ProcessPostbackAsync(
        NormalizedMessengerEvent normalizedEvent,
        VotingWorkflowOptions workflow,
        string? userAccountName,
        CancellationToken cancellationToken)
    {
        var payload = NormalizedEventPayloadReader.GetPostbackPayload(normalizedEvent.PayloadJson);
        if (string.IsNullOrWhiteSpace(payload))
        {
            _runtimeMetricsCollector.Increment("worker.processor.ignored");
            return default;
        }

        var dataErasureOptions = _dataErasureOptionsMonitor.CurrentValue;
        if (LooksLikeLegacyDataErasurePayload(payload))
        {
            return await ResendForgetMeConfirmationAsync(normalizedEvent, cancellationToken);
        }

        var dataErasureStatus = TryParseDataErasurePayload(payload, normalizedEvent.RecipientId!, dataErasureOptions.ConfirmationPayloadSecret, out var dataErasurePayload);
        if (dataErasureStatus == SignedPayloadParseStatus.Success)
        {
            return await ProcessDataErasureConfirmationAsync(normalizedEvent, dataErasurePayload!, dataErasureOptions, cancellationToken);
        }

        if (dataErasureStatus == SignedPayloadParseStatus.Invalid)
        {
            _runtimeMetricsCollector.Increment("worker.processor.invalid_forgetme_confirmation_recovered");
            return await ResendForgetMeConfirmationAsync(normalizedEvent, cancellationToken);
        }

        var voteStatus = TryParseVotePayload(payload, normalizedEvent.RecipientId!, workflow.PayloadSignatureSecret, out var votePayload);
        if (voteStatus == SignedPayloadParseStatus.Success)
        {
            return await ProcessVoteSelectionAsync(normalizedEvent, workflow, votePayload!, userAccountName, cancellationToken);
        }

        var confirmationStatus = TryParseConfirmationPayload(payload, normalizedEvent.RecipientId!, workflow.PayloadSignatureSecret, out var confirmationPayload);
        if (confirmationStatus == SignedPayloadParseStatus.Success)
        {
            return await ProcessVoteConfirmationAsync(normalizedEvent, workflow, confirmationPayload!, userAccountName, cancellationToken);
        }

        if (confirmationStatus == SignedPayloadParseStatus.Invalid)
        {
            return await RecoverVoteFlowAsync(normalizedEvent, workflow, RecoveryReason.InvalidConfirmation, cancellationToken);
        }

        if (voteStatus == SignedPayloadParseStatus.Invalid || LooksLikeLegacyVotePayload(payload))
        {
            return await RecoverVoteFlowAsync(normalizedEvent, workflow, RecoveryReason.InvalidVotePayload, cancellationToken);
        }

        _runtimeMetricsCollector.Increment("worker.processor.ignored");
        return default;
    }

    private async ValueTask<EventProcessingResult> ProcessVoteSelectionAsync(
        NormalizedMessengerEvent normalizedEvent,
        VotingWorkflowOptions workflow,
        VotePayloadEnvelope votePayload,
        string? userAccountName,
        CancellationToken cancellationToken)
    {
        var votingContext = await GetActiveVotingContextOrNotifyInactiveAsync(normalizedEvent.SenderId!, normalizedEvent.RecipientId!, cancellationToken);
        if (votingContext is null)
        {
            return default;
        }

        var now = GetUtcNow();
        if (!string.Equals(votingContext.ShowId, votePayload.ShowId, StringComparison.Ordinal))
        {
            return await RecoverVoteFlowAsync(normalizedEvent, workflow, RecoveryReason.StaleShow, cancellationToken);
        }

        if (await TryHandleCooldownAsync(normalizedEvent, workflow, votingContext.ShowId, now, fromPostback: true, cancellationToken))
        {
            return default;
        }

        var candidate = GetEnabledCandidate(votePayload.CandidateId);
        if (candidate is null)
        {
            return await RecoverVoteFlowAsync(normalizedEvent, workflow, RecoveryReason.InvalidVotePayload, cancellationToken);
        }

        if (!workflow.RequireConfirmationForAll)
        {
            // TODO AntiClicker Check
            await AcceptVoteAsync(normalizedEvent, workflow, votingContext.ShowId, candidate, now, userAccountName, cancellationToken);
            return default;
        }

        _runtimeMetricsCollector.Increment("worker.processor.confirmation_pending");
        await TrySendVoteConfirmationPromptAsync(normalizedEvent.SenderId!, normalizedEvent.RecipientId!, votingContext.ShowId, candidate, workflow, now, cancellationToken);
        return default;
    }

    private async ValueTask<EventProcessingResult> ProcessVoteConfirmationAsync(
        NormalizedMessengerEvent normalizedEvent,
        VotingWorkflowOptions workflow,
        ConfirmationPayloadEnvelope confirmationPayload,
        string? userAccountName,
        CancellationToken cancellationToken)
    {
        var votingContext = await GetActiveVotingContextOrNotifyInactiveAsync(normalizedEvent.SenderId!, normalizedEvent.RecipientId!, cancellationToken);
        if (votingContext is null)
        {
            return default;
        }

        var now = GetUtcNow();
        if (!string.Equals(votingContext.ShowId, confirmationPayload.ShowId, StringComparison.Ordinal))
        {
            return await RecoverVoteFlowAsync(normalizedEvent, workflow, RecoveryReason.StaleShow, cancellationToken);
        }

        if (HasConfirmationExpired(confirmationPayload.IssuedAtUtc, workflow.ConfirmationTimeout, now))
        {
            _runtimeMetricsCollector.Increment("worker.processor.confirmation_expired");
            await TrySendTextMessageAsync(
                normalizedEvent.SenderId!,
                GetMessengerContentOptions().VoteConfirmationExpiredText,
                "worker.outbound.vote_confirmation_expired",
                cancellationToken);
            return default;
        }

        if (!confirmationPayload.IsAccepted)
        {
            _runtimeMetricsCollector.Increment("worker.processor.confirmation_rejected");
            _runtimeMetricsCollector.Increment("worker.processor.options_sent");
            await TrySendCandidateCarouselAsync(normalizedEvent.SenderId!, normalizedEvent.RecipientId!, votingContext.ShowId, cancellationToken);
            return default;
        }

        if (await TryHandleCooldownAsync(normalizedEvent, workflow, votingContext.ShowId, now, fromPostback: true, cancellationToken))
        {
            return default;
        }

        var candidate = GetEnabledCandidate(confirmationPayload.CandidateId);
        if (candidate is null)
        {
            return await RecoverVoteFlowAsync(normalizedEvent, workflow, RecoveryReason.InvalidConfirmation, cancellationToken);
        }

        await AcceptVoteAsync(normalizedEvent, workflow, votingContext.ShowId, candidate, now, userAccountName, cancellationToken);
        return default;
    }

    private async ValueTask<EventProcessingResult> ProcessForgetMeRequestAsync(
        NormalizedMessengerEvent normalizedEvent,
        CancellationToken cancellationToken)
    {
        _runtimeMetricsCollector.Increment("worker.processor.forgetme_requested");
        await TrySendForgetMePromptAsync(normalizedEvent.SenderId!, normalizedEvent.RecipientId!, cancellationToken);
        return default;
    }

    private async ValueTask<EventProcessingResult> ProcessDataErasureConfirmationAsync(
        NormalizedMessengerEvent normalizedEvent,
        DataErasurePayloadEnvelope payload,
        DataErasureOptions options,
        CancellationToken cancellationToken)
    {
        var now = GetUtcNow();
        if (HasConfirmationExpired(payload.IssuedAtUtc, options.ConfirmationTimeout, now))
        {
            _runtimeMetricsCollector.Increment("worker.processor.forgetme_confirmation_expired_reprompted");
            return await ResendForgetMeConfirmationAsync(normalizedEvent, cancellationToken);
        }

        if (!payload.IsAccepted)
        {
            await TrySendForgetMeResultAsync(
                normalizedEvent.SenderId!,
                GetMessengerContentOptions().ForgetMeCancelledText,
                "worker.outbound.forgetme_cancelled",
                cancellationToken);
            return default;
        }

        var erasure = await _userDataErasureService.EraseUserDataAsync(normalizedEvent.SenderId!, cancellationToken);
        _runtimeMetricsCollector.Increment("worker.processor.forgetme_completed");

        var contentOptions = GetMessengerContentOptions();
        var deletedAny = erasure.AcceptedVotesDeleted > 0 || erasure.NormalizedEventsDeleted > 0;
        await TrySendForgetMeResultAsync(
            normalizedEvent.SenderId!,
            deletedAny ? contentOptions.ForgetMeDeletedText : contentOptions.ForgetMeAlreadyDeletedText,
            deletedAny ? "worker.outbound.forgetme_deleted" : "worker.outbound.forgetme_already_deleted",
            cancellationToken);

        return new EventProcessingResult(SkipNormalizedEventPersistence: true);
    }

    private async ValueTask<EventProcessingResult> ResendForgetMeConfirmationAsync(
        NormalizedMessengerEvent normalizedEvent,
        CancellationToken cancellationToken)
    {
        await TrySendForgetMePromptAsync(normalizedEvent.SenderId!, normalizedEvent.RecipientId!, cancellationToken);
        return default;
    }

    private async ValueTask<EventProcessingResult> RecoverVoteFlowAsync(
        NormalizedMessengerEvent normalizedEvent,
        VotingWorkflowOptions workflow,
        RecoveryReason reason,
        CancellationToken cancellationToken)
    {
        var votingContext = await GetActiveVotingContextOrNotifyInactiveAsync(normalizedEvent.SenderId!, normalizedEvent.RecipientId!, cancellationToken);
        if (votingContext is null)
        {
            return default;
        }

        var now = GetUtcNow();
        if (await TryHandleCooldownAsync(normalizedEvent, workflow, votingContext.ShowId, now, fromPostback: true, cancellationToken))
        {
            return default;
        }

        switch (reason)
        {
            case RecoveryReason.InvalidConfirmation:
                _runtimeMetricsCollector.Increment("worker.processor.invalid_confirmation_recovered");
                break;
            case RecoveryReason.InvalidVotePayload:
                _runtimeMetricsCollector.Increment("worker.processor.invalid_vote_payload_recovered");
                break;
            case RecoveryReason.StaleShow:
                _runtimeMetricsCollector.Increment("worker.processor.stale_show_recovered");
                break;
        }

        _runtimeMetricsCollector.Increment("worker.processor.options_sent");
        await TrySendCandidateCarouselAsync(normalizedEvent.SenderId!, normalizedEvent.RecipientId!, votingContext.ShowId, cancellationToken);
        return default;
    }

    private async ValueTask AcceptVoteAsync(
        NormalizedMessengerEvent normalizedEvent,
        VotingWorkflowOptions workflow,
        string showId,
        CandidateDefinition candidate,
        DateTime confirmedAtUtc,
        string? userAccountName,
        CancellationToken cancellationToken)
    {
        var cooldownUntilUtc = confirmedAtUtc.Add(workflow.Cooldown);
        var vote = new AcceptedVote(
            VoteId: Guid.NewGuid(),
            CorrelationId: normalizedEvent.RawEnvelopeId.ToString("N", CultureInfo.InvariantCulture),
            UserId: normalizedEvent.SenderId!,
            RecipientId: normalizedEvent.RecipientId!,
            ShowId: showId,
            CandidateId: candidate.Id,
            CandidateDisplayName: candidate.DisplayName,
            SourceEventId: normalizedEvent.EventId,
            ConfirmedAtUtc: confirmedAtUtc,
            CooldownUntilUtc: cooldownUntilUtc,
            Channel: VoteChannel,
            MetadataJson: BuildAcceptedVoteMetadataJson(normalizedEvent, showId, candidate, confirmedAtUtc),
            UserAccountName: userAccountName);

        var inserted = await _acceptedVoteStore.TryAddAsync(vote, cancellationToken);
        await _voteCooldownStore.SaveAsync(
            new VoteCooldownSnapshot(showId, normalizedEvent.SenderId!, normalizedEvent.RecipientId!, confirmedAtUtc),
            workflow.Cooldown,
            cancellationToken);

        if (inserted)
        {
            _runtimeMetricsCollector.Increment("worker.processor.vote_accepted");
        }

        await TrySendVoteAcceptedMessageAsync(normalizedEvent.SenderId!, workflow, candidate.DisplayName, cooldownUntilUtc, cancellationToken);

        _logger.LogInformation(
            inserted
                ? "Vote accepted and persisted. EventId: {EventId}, UserId: {UserId}, ShowId: {ShowId}, CandidateId: {CandidateId}, CooldownUntilUtc: {CooldownUntilUtc}"
                : "Vote acceptance was already persisted. EventId: {EventId}, UserId: {UserId}, ShowId: {ShowId}, CandidateId: {CandidateId}, CooldownUntilUtc: {CooldownUntilUtc}",
            normalizedEvent.EventId,
            normalizedEvent.SenderId,
            showId,
            candidate.Id,
            cooldownUntilUtc);
    }

    private async ValueTask<ActiveVotingContext?> GetActiveVotingContextOrNotifyInactiveAsync(
        string recipientId,
        string pageId,
        CancellationToken cancellationToken)
    {
        var state = await _votingGateService.GetStateAsync(cancellationToken);
        _runtimeMetricsCollector.SetGauge("worker.voting.started", state.VotingStarted ? 1 : 0);
        _runtimeMetricsCollector.SetGauge("worker.voting.active_show_configured", string.IsNullOrWhiteSpace(state.ActiveShowId) ? 0 : 1);

        if (!state.VotingStarted || string.IsNullOrWhiteSpace(state.ActiveShowId))
        {
            _runtimeMetricsCollector.Increment("worker.processor.voting_inactive");
            await TrySendVotingInactiveMessageAsync(recipientId, cancellationToken);
            return null;
        }

        return new ActiveVotingContext(state.ActiveShowId.Trim());
    }

    private async ValueTask<bool> TryHandleCooldownAsync(
        NormalizedMessengerEvent normalizedEvent,
        VotingWorkflowOptions workflow,
        string showId,
        DateTime now,
        bool fromPostback,
        CancellationToken cancellationToken)
    {
        var cooldown = await GetActiveCooldownAsync(showId, normalizedEvent.SenderId!, normalizedEvent.RecipientId!, workflow, now, cancellationToken);
        if (cooldown is null)
        {
            return false;
        }

        _runtimeMetricsCollector.Increment("worker.processor.cooldown_ignored");
        _runtimeMetricsCollector.Increment("worker.processor.cooldown_attempts");
        _runtimeMetricsCollector.Increment(fromPostback
            ? "worker.processor.cooldown_postback_attempts"
            : "worker.processor.cooldown_message_attempts");

        await TrySendCooldownActiveMessageAsync(normalizedEvent.SenderId!, workflow, cooldown, cancellationToken);
        return true;
    }

    private async ValueTask<VoteCooldownSnapshot?> GetActiveCooldownAsync(
        string showId,
        string userId,
        string recipientId,
        VotingWorkflowOptions workflow,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var cooldown = await _voteCooldownStore.GetAsync(showId, userId, recipientId, cancellationToken);
        if (cooldown is null)
        {
            return null;
        }

        var cooldownUntilUtc = cooldown.LastVotedUtc.Add(workflow.Cooldown);
        if (cooldownUntilUtc > now)
        {
            return cooldown;
        }

        await _voteCooldownStore.RemoveAsync(showId, userId, recipientId, cancellationToken);
        return null;
    }

    private async ValueTask TrySendVoteAcceptedMessageAsync(
        string recipientId,
        VotingWorkflowOptions workflow,
        string candidateDisplayName,
        DateTime cooldownUntilUtc,
        CancellationToken cancellationToken)
    {
        if (workflow.CooldownResponseMode == CooldownResponseMode.Silent)
        {
            return;
        }

        var contentOptions = GetMessengerContentOptions();
        var messageText = BuildVoteAcceptedText(contentOptions, workflow, candidateDisplayName, cooldownUntilUtc);
        await TrySendTextMessageAsync(recipientId, messageText, "worker.outbound.vote_accepted", cancellationToken);
    }

    private async ValueTask TrySendCooldownActiveMessageAsync(
        string recipientId,
        VotingWorkflowOptions workflow,
        VoteCooldownSnapshot cooldown,
        CancellationToken cancellationToken)
    {
        if (workflow.CooldownResponseMode == CooldownResponseMode.Silent)
        {
            return;
        }

        var cooldownUntilUtc = cooldown.LastVotedUtc.Add(workflow.Cooldown);
        var contentOptions = GetMessengerContentOptions();
        var messageText = BuildCooldownActiveText(contentOptions, workflow, cooldownUntilUtc);
        await TrySendTextMessageAsync(recipientId, messageText, "worker.outbound.cooldown_active", cancellationToken);
    }

    private async ValueTask TrySendCandidateCarouselAsync(
        string recipientId,
        string pageId,
        string showId,
        CancellationToken cancellationToken)
    {
        var candidateOptions = _candidateOptionsMonitor.CurrentValue;
        var contentOptions = GetMessengerContentOptions();
        var enabledCandidates = candidateOptions.Items.Where(item => item.Enabled).ToArray();

        if (enabledCandidates.Length == 0)
        {
            _runtimeMetricsCollector.Increment("worker.outbound.candidate_carousel.skipped");
            _logger.LogWarning("Candidate carousel was skipped because no enabled candidates are configured. RecipientId: {RecipientId}", recipientId);
            return;
        }

        var secret = _workflowOptionsMonitor.CurrentValue.PayloadSignatureSecret;
        var elements = enabledCandidates
            .Select(candidate => new MessengerGenericTemplateElement(
                Title: candidate.DisplayName,
                Subtitle: string.IsNullOrWhiteSpace(candidate.Subtitle) ? candidate.Phone : candidate.Subtitle,
                ImageUrl: BuildCandidateImageUrl(candidateOptions, candidate),
                Buttons: new[]
                {
                    new MessengerPostbackButton(
                        BuildCandidateVoteButtonTitle(contentOptions, candidate),
                        BuildVotePayload(showId, candidate.Id, pageId, secret))
                }))
            .ToArray();

        await TrySendGenericTemplateAsync(recipientId, elements, "worker.outbound.candidate_carousel", "Candidate carousel", cancellationToken);
    }

    private async ValueTask TrySendVoteConfirmationPromptAsync(
        string recipientId,
        string pageId,
        string showId,
        CandidateDefinition candidate,
        VotingWorkflowOptions workflow,
        DateTime issuedAtUtc,
        CancellationToken cancellationToken)
    {
        var candidateOptions = _candidateOptionsMonitor.CurrentValue;
        var contentOptions = GetMessengerContentOptions();
        var buttons = BuildVoteConfirmationButtons(contentOptions, pageId, showId, candidate, issuedAtUtc, workflow.PayloadSignatureSecret);
        var element = new MessengerGenericTemplateElement(
            Title: BuildVoteConfirmationPrompt(contentOptions, candidate, workflow),
            Subtitle: null,
            ImageUrl: BuildCandidateImageUrl(candidateOptions, candidate),
            Buttons: buttons);

        await TrySendGenericTemplateAsync(
            recipientId,
            new[] { element },
            "worker.outbound.vote_confirmation",
            "Vote confirmation prompt",
            cancellationToken);
    }

    private async ValueTask TrySendVotingInactiveMessageAsync(string recipientId, CancellationToken cancellationToken)
    {
        var messageText = GetMessengerContentOptions().VotingInactiveText;
        await TrySendTextMessageAsync(recipientId, messageText, "worker.outbound.voting_inactive", cancellationToken);
    }

    private async ValueTask TrySendForgetMePromptAsync(string recipientId, string pageId, CancellationToken cancellationToken)
    {
        var contentOptions = GetMessengerContentOptions();
        var now = GetUtcNow();
        var secret = _dataErasureOptionsMonitor.CurrentValue.ConfirmationPayloadSecret;
        var buttons = new[]
        {
            new MessengerPostbackButton(contentOptions.ForgetMeConfirmButtonTitle, BuildDataErasurePayload(pageId, now, DataErasureAction.Accept, secret)),
            new MessengerPostbackButton(contentOptions.ForgetMeCancelButtonTitle, BuildDataErasurePayload(pageId, now, DataErasureAction.Cancel, secret))
        };

        var promptText = EmojiBasicConstants.FlagGeorgia + " " + contentOptions.ForgetMeConfirmationPrompt;
        await TrySendButtonTemplateAsync(recipientId, promptText, buttons, "worker.outbound.forgetme_prompt", cancellationToken);
    }

    private async ValueTask TrySendGenericTemplateAsync(
        string recipientId,
        IReadOnlyCollection<MessengerGenericTemplateElement> elements,
        string metricPrefix,
        string templateDescription,
        CancellationToken cancellationToken)
    {
        try
        {
            var sent = await _outboundMessengerClient.SendGenericTemplateAsync(recipientId, elements, cancellationToken);
            if (sent)
            {
                _runtimeMetricsCollector.Increment("worker.outbound.messenger.sent");
                _runtimeMetricsCollector.Increment(metricPrefix + ".sent");
                return;
            }

            _runtimeMetricsCollector.Increment("worker.outbound.messenger.failed");
            _runtimeMetricsCollector.Increment(metricPrefix + ".failed");
            _logger.LogWarning("{TemplateDescription} was not sent. RecipientId: {RecipientId}, ElementCount: {ElementCount}", templateDescription, recipientId, elements.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _runtimeMetricsCollector.Increment("worker.outbound.messenger.failed");
            _runtimeMetricsCollector.Increment(metricPrefix + ".failed");
            _logger.LogWarning(exception, "{TemplateDescription} send failed. RecipientId: {RecipientId}, ElementCount: {ElementCount}", templateDescription, recipientId, elements.Count);
        }
    }

    private async ValueTask TrySendTextMessageAsync(
        string recipientId,
        string messageText,
        string metricPrefix,
        CancellationToken cancellationToken)
    {
        try
        {
            var sent = await _outboundMessengerClient.SendTextAsync(recipientId, messageText, cancellationToken);
            if (sent)
            {
                _runtimeMetricsCollector.Increment("worker.outbound.messenger.sent");
                _runtimeMetricsCollector.Increment(metricPrefix + ".sent");
                return;
            }

            _runtimeMetricsCollector.Increment("worker.outbound.messenger.failed");
            _runtimeMetricsCollector.Increment(metricPrefix + ".failed");
            _logger.LogWarning("Messenger text was not sent. RecipientId: {RecipientId}, MetricPrefix: {MetricPrefix}", recipientId, metricPrefix);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _runtimeMetricsCollector.Increment("worker.outbound.messenger.failed");
            _runtimeMetricsCollector.Increment(metricPrefix + ".failed");
            _logger.LogWarning(exception, "Messenger text send failed. RecipientId: {RecipientId}, MetricPrefix: {MetricPrefix}", recipientId, metricPrefix);
        }
    }

    private async ValueTask TrySendButtonTemplateAsync(
        string recipientId,
        string promptText,
        IReadOnlyCollection<MessengerPostbackButton> buttons,
        string metricPrefix,
        CancellationToken cancellationToken)
    {
        try
        {
            var sent = await _outboundMessengerClient.SendButtonTemplateAsync(recipientId, promptText, buttons, cancellationToken);
            if (sent)
            {
                _runtimeMetricsCollector.Increment("worker.outbound.messenger.sent");
                _runtimeMetricsCollector.Increment(metricPrefix + ".sent");
                return;
            }

            _runtimeMetricsCollector.Increment("worker.outbound.messenger.failed");
            _runtimeMetricsCollector.Increment(metricPrefix + ".failed");
            _logger.LogWarning("Messenger button template was not sent. RecipientId: {RecipientId}, MetricPrefix: {MetricPrefix}", recipientId, metricPrefix);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _runtimeMetricsCollector.Increment("worker.outbound.messenger.failed");
            _runtimeMetricsCollector.Increment(metricPrefix + ".failed");
            _logger.LogWarning(exception, "Messenger button template send failed. RecipientId: {RecipientId}, MetricPrefix: {MetricPrefix}", recipientId, metricPrefix);
        }
    }

    private ValueTask TrySendForgetMeResultAsync(string recipientId, string messageText, string metricPrefix, CancellationToken cancellationToken)
        => TrySendTextMessageAsync(recipientId, messageText, metricPrefix, cancellationToken);

    private MessengerContentOptions GetMessengerContentOptions() => _messengerContentOptionsMonitor.CurrentValue;

    private static bool MatchesVoteStartToken(string? text, VotingWorkflowOptions workflow)
        => MessengerInboundClassifier.IsVoteStartToken(text, workflow.VoteStartTokens);

    private bool MatchesForgetMeToken(string? text)
        => !string.IsNullOrWhiteSpace(text) && MessengerInboundClassifier.IsForgetMeToken(text, GetMessengerContentOptions().ForgetMeTokens);

    private CandidateDefinition? GetEnabledCandidate(string candidateId)
        => _candidateOptionsMonitor.CurrentValue.Items.FirstOrDefault(item => item.Enabled && string.Equals(item.Id, candidateId, StringComparison.OrdinalIgnoreCase));

    private static string BuildCandidateVoteButtonTitle(MessengerContentOptions contentOptions, CandidateDefinition candidate)
    {
        var template = string.IsNullOrWhiteSpace(candidate.ButtonTitle)
            ? contentOptions.CandidateVoteButtonTitleFormat
            : candidate.ButtonTitle;
        var resolved = template.Replace("{DisplayName}", candidate.DisplayName, StringComparison.Ordinal);
        return TrimToMaxLength(resolved, 20);
    }

    private static string BuildVoteConfirmationPrompt(MessengerContentOptions contentOptions, CandidateDefinition candidate, VotingWorkflowOptions workflow)
    {
        var seconds = Math.Max(1, (int)Math.Round(workflow.ConfirmationTimeout.TotalSeconds, MidpointRounding.AwayFromZero));
        return contentOptions.VoteConfirmationPromptFormat
            .Replace("{Seconds}", seconds.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{DisplayName}", candidate.DisplayName, StringComparison.Ordinal) + " " + EmojiBasicConstants.Countdown;
    }

    private static MessengerPostbackButton[] BuildVoteConfirmationButtons(
        MessengerContentOptions contentOptions,
        string pageId,
        string showId,
        CandidateDefinition candidate,
        DateTime issuedAtUtc,
        string secret)
    {
        var buttons = new List<MessengerPostbackButton>(capacity: 3)
        {
            new(
                BuildVoteConfirmationCorrectButtonTitle(contentOptions, candidate),
                BuildConfirmationPayload(showId, candidate.Id, issuedAtUtc, ConfirmationAction.Accept, pageId, secret))
        };

        var decoyTitles = contentOptions.VoteConfirmationDecoyButtonTitles
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .Select(title => TrimToMaxLength(title, 20))
            .Take(2)
            .ToList();

        while (decoyTitles.Count < 2)
        {
            decoyTitles.Add("-");
        }

        for (var index = 0; index < decoyTitles.Count; index++)
        {
            buttons.Add(new MessengerPostbackButton(
                decoyTitles[index],
                BuildConfirmationPayload(showId, candidate.Id, issuedAtUtc, index == 0 ? ConfirmationAction.DecoyA : ConfirmationAction.DecoyB, pageId, secret)));
        }

        for (var index = buttons.Count - 1; index > 0; index--)
        {
            var swapIndex = Random.Shared.Next(index + 1);
            (buttons[index], buttons[swapIndex]) = (buttons[swapIndex], buttons[index]);
        }

        return buttons.ToArray();
    }

    private static string BuildVoteAcceptedText(MessengerContentOptions contentOptions, VotingWorkflowOptions workflow, string candidateDisplayName, DateTime cooldownUntilUtc)
        => contentOptions.VoteAcceptedTextFormat.Insert(0, EmojiBasicConstants.Agreement)
            .Replace("{CandidateDisplayName}", candidateDisplayName, StringComparison.Ordinal)
            .Replace("{CooldownMinutes}", FormatCooldownMinutes(workflow.Cooldown), StringComparison.Ordinal)
            .Replace("{CooldownUntilLocal}", FormatLocalTime(cooldownUntilUtc, contentOptions), StringComparison.Ordinal);

    private static string BuildCooldownActiveText(MessengerContentOptions contentOptions, VotingWorkflowOptions workflow, DateTime cooldownUntilUtc)
        => contentOptions.CooldownActiveTextFormat.Insert(0, EmojiBasicConstants.CrossMark)
            .Replace("{CooldownMinutes}", FormatCooldownMinutes(workflow.Cooldown), StringComparison.Ordinal)
            .Replace("{CooldownUntilLocal}", FormatLocalTime(cooldownUntilUtc, contentOptions), StringComparison.Ordinal);

    private static string FormatCooldownMinutes(TimeSpan cooldown)
        => Math.Max(1, (int)Math.Round(cooldown.TotalMinutes, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);

    private static string FormatLocalTime(DateTime utcDateTime, MessengerContentOptions contentOptions)
    {
        var localValue = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc), TimeZoneInfo.Local);
        var format = string.IsNullOrWhiteSpace(contentOptions.CooldownTimeFormat) ? "HH:mm:ss" : contentOptions.CooldownTimeFormat;
        return localValue.ToString(format, CultureInfo.InvariantCulture);
    }

    private static string BuildVoteConfirmationCorrectButtonTitle(MessengerContentOptions contentOptions, CandidateDefinition candidate)
    {
        var template = string.IsNullOrWhiteSpace(contentOptions.VoteConfirmationCorrectButtonTitleFormat)
            ? "{DisplayName}"
            : contentOptions.VoteConfirmationCorrectButtonTitleFormat;
        var resolved = template.Replace("{DisplayName}", candidate.DisplayName, StringComparison.Ordinal);
        return TrimToMaxLength(resolved, 20);
    }

    private static string? BuildCandidateImageUrl(CandidatesOptions candidateOptions, CandidateDefinition candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.Image) || string.IsNullOrWhiteSpace(candidateOptions.PublicBaseUrl))
        {
            return null;
        }

        var baseUrl = candidateOptions.PublicBaseUrl.TrimEnd('/');
        var assetBasePath = string.IsNullOrWhiteSpace(candidateOptions.AssetBasePath)
            ? string.Empty
            : "/" + candidateOptions.AssetBasePath.Trim().Trim('/');
        var fileName = candidate.Image.TrimStart('/');
        return $"{baseUrl}{assetBasePath}/{Uri.EscapeDataString(fileName)}";
    }

    private static string TrimToMaxLength(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string BuildAcceptedVoteMetadataJson(
        NormalizedMessengerEvent normalizedEvent,
        string showId,
        CandidateDefinition candidate,
        DateTime confirmedAtUtc)
    {
        return JsonSerializer.Serialize(new
        {
            normalizedEvent.EventId,
            normalizedEvent.MessageId,
            normalizedEvent.RawEnvelopeId,
            normalizedEvent.EventType,
            normalizedEvent.OccurredAtUtc,
            ConfirmedAtUtc = confirmedAtUtc,
            ShowId = showId,
            CandidateId = candidate.Id,
            CandidateDisplayName = candidate.DisplayName
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
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
    }

    private DateTime GetUtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private static bool HasConfirmationExpired(DateTime issuedAtUtc, TimeSpan timeout, DateTime now)
        => issuedAtUtc.Add(timeout) < now;

    private static bool LooksLikeLegacyDataErasurePayload(string payload)
        => string.Equals(payload, "YES", StringComparison.OrdinalIgnoreCase) || string.Equals(payload, "NO", StringComparison.OrdinalIgnoreCase);

    private bool LooksLikeLegacyVotePayload(string payload)
    {
        if (payload.StartsWith("CONFIRM:", StringComparison.OrdinalIgnoreCase) || payload.StartsWith("VOTE:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return _candidateOptionsMonitor.CurrentValue.Items.Any(item => string.Equals(item.Id, payload, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildVotePayload(string showId, string candidateId, string pageId, string secret)
    {
        var bodyBase64 = SerializePayload(new VotePayloadBody(showId, candidateId));
        var signature = ComputeSignature(VotePayloadPrefix, pageId, bodyBase64, secret);
        return $"{VotePayloadPrefix}:{bodyBase64}:{signature}";
    }

    private static string BuildConfirmationPayload(string showId, string candidateId, DateTime issuedAtUtc, ConfirmationAction action, string pageId, string secret)
    {
        var bodyBase64 = SerializePayload(new ConfirmationPayloadBody(showId, candidateId, ToUnixTimeSeconds(issuedAtUtc), action.ToString().ToUpperInvariant()));
        var signature = ComputeSignature(ConfirmationPayloadPrefix, pageId, bodyBase64, secret);
        return $"{ConfirmationPayloadPrefix}:{bodyBase64}:{signature}";
    }

    private static string BuildDataErasurePayload(string pageId, DateTime issuedAtUtc, DataErasureAction action, string secret)
    {
        var bodyBase64 = SerializePayload(new DataErasurePayloadBody(ToUnixTimeSeconds(issuedAtUtc), action.ToString().ToUpperInvariant()));
        var signature = ComputeSignature(DataErasurePayloadPrefix, pageId, bodyBase64, secret);
        return $"{DataErasurePayloadPrefix}:{bodyBase64}:{signature}";
    }

    private static SignedPayloadParseStatus TryParseVotePayload(string payload, string pageId, string secret, out VotePayloadEnvelope? envelope)
    {
        envelope = null;
        var status = TryParseSignedPayload(payload, VotePayloadPrefix, pageId, secret, out VotePayloadBody? body);
        if (status != SignedPayloadParseStatus.Success || body is null || string.IsNullOrWhiteSpace(body.ShowId) || string.IsNullOrWhiteSpace(body.CandidateId))
        {
            return status;
        }

        envelope = new VotePayloadEnvelope(body.ShowId, body.CandidateId);
        return SignedPayloadParseStatus.Success;
    }

    private static SignedPayloadParseStatus TryParseConfirmationPayload(string payload, string pageId, string secret, out ConfirmationPayloadEnvelope? envelope)
    {
        envelope = null;
        var status = TryParseSignedPayload(payload, ConfirmationPayloadPrefix, pageId, secret, out ConfirmationPayloadBody? body);
        if (status != SignedPayloadParseStatus.Success || body is null || string.IsNullOrWhiteSpace(body.ShowId) || string.IsNullOrWhiteSpace(body.CandidateId) || string.IsNullOrWhiteSpace(body.Action))
        {
            return status;
        }

        if (!TryFromUnixTimeSeconds(body.IssuedAtUnixTimeSeconds, out var issuedAtUtc))
        {
            return SignedPayloadParseStatus.Invalid;
        }

        envelope = new ConfirmationPayloadEnvelope(body.ShowId, body.CandidateId, issuedAtUtc, ParseConfirmationAction(body.Action));
        return SignedPayloadParseStatus.Success;
    }

    private static SignedPayloadParseStatus TryParseDataErasurePayload(string payload, string pageId, string secret, out DataErasurePayloadEnvelope? envelope)
    {
        envelope = null;
        var status = TryParseSignedPayload(payload, DataErasurePayloadPrefix, pageId, secret, out DataErasurePayloadBody? body);
        if (status != SignedPayloadParseStatus.Success || body is null || string.IsNullOrWhiteSpace(body.Action))
        {
            return status;
        }

        if (!TryFromUnixTimeSeconds(body.IssuedAtUnixTimeSeconds, out var issuedAtUtc))
        {
            return SignedPayloadParseStatus.Invalid;
        }

        envelope = new DataErasurePayloadEnvelope(issuedAtUtc, ParseDataErasureAction(body.Action));
        return SignedPayloadParseStatus.Success;
    }

    private static SignedPayloadParseStatus TryParseSignedPayload<TBody>(string payload, string prefix, string pageId, string secret, out TBody? body)
        where TBody : class
    {
        body = null;
        if (!payload.StartsWith(prefix + ":", StringComparison.Ordinal))
        {
            return SignedPayloadParseStatus.NotMatched;
        }

        var parts = payload.Split(':', 3, StringSplitOptions.None);
        if (parts.Length != 3 || !string.Equals(parts[0], prefix, StringComparison.Ordinal))
        {
            return SignedPayloadParseStatus.Invalid;
        }

        var expectedSignature = ComputeSignature(prefix, pageId, parts[1], secret);
        if (!FixedTimeEquals(expectedSignature, parts[2]))
        {
            return SignedPayloadParseStatus.Invalid;
        }

        try
        {
            var jsonBytes = Base64UrlDecode(parts[1]);
            body = JsonSerializer.Deserialize<TBody>(jsonBytes);
            return body is null ? SignedPayloadParseStatus.Invalid : SignedPayloadParseStatus.Success;
        }
        catch
        {
            return SignedPayloadParseStatus.Invalid;
        }
    }

    private static string SerializePayload<TBody>(TBody body)
        => Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(body));

    private static string ComputeSignature(string prefix, string pageId, string bodyBase64, string secret)
    {
        var payload = Encoding.UTF8.GetBytes($"{prefix}|{pageId}|{bodyBase64}");
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Base64UrlEncode(hmac.ComputeHash(payload));
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        var padding = normalized.Length % 4;
        if (padding > 0)
        {
            normalized = normalized.PadRight(normalized.Length + (4 - padding), '=');
        }

        return Convert.FromBase64String(normalized);
    }

    private static long ToUnixTimeSeconds(DateTime utcDateTime)
        => new DateTimeOffset(DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static bool TryFromUnixTimeSeconds(long seconds, out DateTime utcDateTime)
    {
        try
        {
            utcDateTime = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            return true;
        }
        catch
        {
            utcDateTime = default;
            return false;
        }
    }

    private static ConfirmationAction ParseConfirmationAction(string value)
        => value.ToUpperInvariant() switch
        {
            "YES" or "ACCEPT" => ConfirmationAction.Accept,
            "DECOYA" or "NO_A" or "NO1" => ConfirmationAction.DecoyA,
            "DECOYB" or "NO_B" or "NO2" => ConfirmationAction.DecoyB,
            _ => ConfirmationAction.Invalid
        };

    private static DataErasureAction ParseDataErasureAction(string value)
        => value.ToUpperInvariant() switch
        {
            "YES" or "ACCEPT" => DataErasureAction.Accept,
            "NO" or "CANCEL" => DataErasureAction.Cancel,
            _ => DataErasureAction.Invalid
        };

    private readonly record struct EventProcessingResult(bool SkipNormalizedEventPersistence = false);
    private sealed record ActiveVotingContext(string ShowId);
    private sealed record VotePayloadBody(string ShowId, string CandidateId);
    private sealed record ConfirmationPayloadBody(string ShowId, string CandidateId, long IssuedAtUnixTimeSeconds, string Action);
    private sealed record DataErasurePayloadBody(long IssuedAtUnixTimeSeconds, string Action);
    private sealed record VotePayloadEnvelope(string ShowId, string CandidateId);
    private sealed record ConfirmationPayloadEnvelope(string ShowId, string CandidateId, DateTime IssuedAtUtc, ConfirmationAction Action)
    {
        public bool IsAccepted => Action == ConfirmationAction.Accept;
    }

    private sealed record DataErasurePayloadEnvelope(DateTime IssuedAtUtc, DataErasureAction Action)
    {
        public bool IsAccepted => Action == DataErasureAction.Accept;
    }

    private enum SignedPayloadParseStatus
    {
        NotMatched,
        Success,
        Invalid
    }

    private enum RecoveryReason
    {
        InvalidVotePayload,
        InvalidConfirmation,
        StaleShow
    }

    private enum ConfirmationAction
    {
        Invalid,
        Accept,
        DecoyA,
        DecoyB
    }

    private enum DataErasureAction
    {
        Invalid,
        Accept,
        Cancel
    }
}

