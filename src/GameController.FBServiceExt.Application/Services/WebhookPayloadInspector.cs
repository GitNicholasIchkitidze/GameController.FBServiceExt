using System.Text.Json;

namespace GameController.FBServiceExt.Application.Services;

public static class WebhookPayloadInspector
{
    public static WebhookPayloadInspection Inspect(
        string body,
        IReadOnlyCollection<string>? forgetMeTokens,
        IReadOnlyCollection<string>? voteStartTokens)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return WebhookPayloadInspection.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return InspectDocument(document, forgetMeTokens, voteStartTokens);
        }
        catch (JsonException)
        {
            return WebhookPayloadInspection.InvalidJson;
        }
    }

    public static WebhookPayloadInspection Inspect(
        ReadOnlyMemory<byte> bodyUtf8,
        IReadOnlyCollection<string>? forgetMeTokens,
        IReadOnlyCollection<string>? voteStartTokens)
    {
        if (bodyUtf8.IsEmpty)
        {
            return WebhookPayloadInspection.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(bodyUtf8);
            return InspectDocument(document, forgetMeTokens, voteStartTokens);
        }
        catch (JsonException)
        {
            return WebhookPayloadInspection.InvalidJson;
        }
    }

    private static WebhookPayloadInspection InspectDocument(
        JsonDocument document,
        IReadOnlyCollection<string>? forgetMeTokens,
        IReadOnlyCollection<string>? voteStartTokens)
    {
        var root = document.RootElement;
        var objectType = root.TryGetProperty("object", out var objectProperty) && objectProperty.ValueKind == JsonValueKind.String
            ? objectProperty.GetString() ?? "unknown"
            : "unknown";

        if (!root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return new WebhookPayloadInspection(objectType, 0, 0, 0, 0, 0, 0, 0, false);
        }

        var entryCount = 0;
        var messagingCount = 0;
        var standbyCount = 0;
        var postbackCount = 0;
        var voteStartMessageCount = 0;
        var forgetMeMessageCount = 0;
        var garbageMessageCount = 0;

        foreach (var entry in entries.EnumerateArray())
        {
            entryCount++;

            if (entry.TryGetProperty("messaging", out var messaging) && messaging.ValueKind == JsonValueKind.Array)
            {
                messagingCount += messaging.GetArrayLength();

                foreach (var item in messaging.EnumerateArray())
                {
                    switch (MessengerInboundClassifier.ClassifyMessagingItem(item, forgetMeTokens, voteStartTokens))
                    {
                        case MessengerInboundMessageKind.Postback:
                            postbackCount++;
                            break;
                        case MessengerInboundMessageKind.VoteStart:
                            voteStartMessageCount++;
                            break;
                        case MessengerInboundMessageKind.ForgetMe:
                            forgetMeMessageCount++;
                            break;
                        default:
                            garbageMessageCount++;
                            break;
                    }
                }
            }

            if (entry.TryGetProperty("standby", out var standby) && standby.ValueKind == JsonValueKind.Array)
            {
                standbyCount += standby.GetArrayLength();
            }
        }

        return new WebhookPayloadInspection(
            objectType,
            entryCount,
            messagingCount,
            standbyCount,
            postbackCount,
            voteStartMessageCount,
            forgetMeMessageCount,
            garbageMessageCount,
            false);
    }
}

public readonly record struct WebhookPayloadInspection(
    string ObjectType,
    int EntryCount,
    int MessagingCount,
    int StandbyCount,
    int PostbackCount,
    int VoteStartMessageCount,
    int ForgetMeMessageCount,
    int GarbageMessageCount,
    bool IsInvalidJson)
{
    public static WebhookPayloadInspection Empty { get; } = new("empty", 0, 0, 0, 0, 0, 0, 0, false);

    public static WebhookPayloadInspection InvalidJson { get; } = new("invalid-json", 0, 0, 0, 0, 0, 0, 0, true);

    public bool ContainsForgetMeBypass => ForgetMeMessageCount > 0;

    public bool ContainsPostbackEvents => PostbackCount > 0;

    public bool ContainsVoteStartMessages => VoteStartMessageCount > 0;

    public bool ContainsBusinessTraffic => ContainsForgetMeBypass || ContainsPostbackEvents || ContainsVoteStartMessages;

    public bool CanDropWhenVotingDisabled => !IsInvalidJson && !ContainsForgetMeBypass && !ContainsPostbackEvents;

    public bool CanDropAsGarbage => !IsInvalidJson && MessagingCount > 0 && !ContainsBusinessTraffic && GarbageMessageCount == MessagingCount;
}
