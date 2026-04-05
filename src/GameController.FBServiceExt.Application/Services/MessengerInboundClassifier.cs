using System.Text.Json;
using GameController.FBServiceExt.Application.Contracts.Normalization;
using GameController.FBServiceExt.Domain.Messaging;

namespace GameController.FBServiceExt.Application.Services;

public enum MessengerInboundMessageKind
{
    Unknown = 0,
    Garbage = 1,
    VoteStart = 2,
    ForgetMe = 3,
    Postback = 4
}

public static class MessengerInboundClassifier
{
    public static bool IsForgetMeToken(string? text, IReadOnlyCollection<string>? forgetMeTokens)
    {
        if (string.IsNullOrWhiteSpace(text) || forgetMeTokens is null || forgetMeTokens.Count == 0)
        {
            return false;
        }

        return forgetMeTokens.Any(token => string.Equals(text, token, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsVoteStartToken(string? text, IReadOnlyCollection<string>? voteStartTokens)
    {
        if (string.IsNullOrWhiteSpace(text) || voteStartTokens is null || voteStartTokens.Count == 0)
        {
            return false;
        }

        return voteStartTokens.Any(token => string.Equals(text, token, StringComparison.OrdinalIgnoreCase));
    }

    public static MessengerInboundMessageKind ClassifyMessagingItem(
        JsonElement item,
        IReadOnlyCollection<string>? forgetMeTokens,
        IReadOnlyCollection<string>? voteStartTokens)
    {
        if (item.TryGetProperty("postback", out _))
        {
            return MessengerInboundMessageKind.Postback;
        }

        if (!item.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
        {
            return MessengerInboundMessageKind.Garbage;
        }

        var text = message.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String
            ? textElement.GetString()
            : null;

        if (IsForgetMeToken(text, forgetMeTokens))
        {
            return MessengerInboundMessageKind.ForgetMe;
        }

        if (IsVoteStartToken(text, voteStartTokens))
        {
            return MessengerInboundMessageKind.VoteStart;
        }

        return MessengerInboundMessageKind.Garbage;
    }

    public static bool IsBusinessRelevantEvent(
        NormalizedMessengerEvent normalizedEvent,
        IReadOnlyCollection<string>? forgetMeTokens,
        IReadOnlyCollection<string>? voteStartTokens)
    {
        if (normalizedEvent.EventType == MessengerEventType.Postback)
        {
            return true;
        }

        if (normalizedEvent.EventType != MessengerEventType.Message)
        {
            return false;
        }

        var text = NormalizedEventPayloadReader.GetMessageText(normalizedEvent.PayloadJson);
        return IsForgetMeToken(text, forgetMeTokens) || IsVoteStartToken(text, voteStartTokens);
    }
}
