using System.Globalization;
using System.Text.Json;
using GameController.FBServiceExt.Application.Abstractions.Processing;
using GameController.FBServiceExt.Application.Contracts.Normalization;
using GameController.FBServiceExt.Application.Contracts.RawIngress;
using GameController.FBServiceExt.Domain.Messaging;

namespace GameController.FBServiceExt.Application.Services;

public sealed class RawWebhookNormalizer : IRawWebhookNormalizer
{
    // Meta webhook JSON-ს შლის ცალკეულ normalized event-ებად.
    // აქ messaging/standby მასივები ქცევა queue-ში გადასაცემ ერთეულებად.
    public ValueTask<IReadOnlyList<NormalizedMessengerEvent>> NormalizeAsync(RawWebhookEnvelope envelope, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(envelope.Body))
        {
            return ValueTask.FromResult<IReadOnlyList<NormalizedMessengerEvent>>(Array.Empty<NormalizedMessengerEvent>());
        }

        using var document = JsonDocument.Parse(envelope.Body);
        var results = new List<NormalizedMessengerEvent>();

        if (!document.RootElement.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return ValueTask.FromResult<IReadOnlyList<NormalizedMessengerEvent>>(results);
        }

        foreach (var entry in entries.EnumerateArray())
        {
            AppendEvents(results, entry, envelope, "messaging", null);
            AppendEvents(results, entry, envelope, "standby", MessengerEventType.Standby);
        }

        return ValueTask.FromResult<IReadOnlyList<NormalizedMessengerEvent>>(results);
    }

    // ერთი entry-დან თითოეულ messaging/standby item-ს გარდაქმნის NormalizedMessengerEvent-ად.
    private static void AppendEvents(
        ICollection<NormalizedMessengerEvent> results,
        JsonElement entry,
        RawWebhookEnvelope envelope,
        string propertyName,
        MessengerEventType? forcedEventType)
    {
        if (!entry.TryGetProperty(propertyName, out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in items.EnumerateArray())
        {
            var eventType = forcedEventType ?? InferEventType(item);
            var messageId = ExtractMessageId(item);
            var senderId = ExtractNestedString(item, "sender", "id");
            var recipientId = ExtractNestedString(item, "recipient", "id");
            var occurredAtUtc = ExtractTimestamp(item, envelope.ReceivedAtUtc);
            var payloadJson = item.GetRawText();
            var eventId = MessengerEventIdentityFactory.Create(messageId, senderId, recipientId, occurredAtUtc, eventType, payloadJson);

            results.Add(new NormalizedMessengerEvent(
                eventId,
                eventType,
                messageId,
                senderId,
                recipientId,
                occurredAtUtc,
                payloadJson,
                envelope.EnvelopeId));
        }
    }

    private static MessengerEventType InferEventType(JsonElement item)
    {
        if (item.TryGetProperty("postback", out _))
        {
            return MessengerEventType.Postback;
        }

        if (item.TryGetProperty("reaction", out _))
        {
            return MessengerEventType.Reaction;
        }

        if (item.TryGetProperty("read", out _))
        {
            return MessengerEventType.Read;
        }

        if (item.TryGetProperty("delivery", out _))
        {
            return MessengerEventType.Delivery;
        }

        if (item.TryGetProperty("referral", out _))
        {
            return MessengerEventType.Referral;
        }

        if (item.TryGetProperty("optin", out _))
        {
            return MessengerEventType.OptIn;
        }

        if (!item.TryGetProperty("message", out var message))
        {
            return MessengerEventType.Unknown;
        }

        if (message.TryGetProperty("is_echo", out var isEcho) && isEcho.ValueKind is JsonValueKind.True)
        {
            return MessengerEventType.Echo;
        }

        if (message.TryGetProperty("quick_reply", out _))
        {
            return MessengerEventType.QuickReply;
        }

        if (message.TryGetProperty("attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array && attachments.GetArrayLength() > 0)
        {
            return MessengerEventType.Attachment;
        }

        return MessengerEventType.Message;
    }

    private static string? ExtractMessageId(JsonElement item)
    {
        if (item.TryGetProperty("message", out var message) && message.TryGetProperty("mid", out var messageMid) && messageMid.ValueKind == JsonValueKind.String)
        {
            return messageMid.GetString();
        }

        if (item.TryGetProperty("postback", out var postback) && postback.TryGetProperty("mid", out var postbackMid) && postbackMid.ValueKind == JsonValueKind.String)
        {
            return postbackMid.GetString();
        }

        if (item.TryGetProperty("reaction", out var reaction) && reaction.TryGetProperty("mid", out var reactionMid) && reactionMid.ValueKind == JsonValueKind.String)
        {
            return reactionMid.GetString();
        }

        return null;
    }

    private static string? ExtractNestedString(JsonElement item, string parentProperty, string childProperty)
    {
        if (!item.TryGetProperty(parentProperty, out var parent) || parent.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!parent.TryGetProperty(childProperty, out var child) || child.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return child.GetString();
    }

    private static DateTime ExtractTimestamp(JsonElement item, DateTime fallbackUtc)
    {
        if (item.TryGetProperty("timestamp", out var timestamp))
        {
            if (timestamp.ValueKind == JsonValueKind.Number && timestamp.TryGetInt64(out var unixMilliseconds))
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).UtcDateTime;
            }

            if (timestamp.ValueKind == JsonValueKind.String && long.TryParse(timestamp.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out unixMilliseconds))
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).UtcDateTime;
            }
        }

        return fallbackUtc;
    }
}
