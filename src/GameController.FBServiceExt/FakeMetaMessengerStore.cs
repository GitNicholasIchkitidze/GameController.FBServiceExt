using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameController.FBServiceExt;

internal sealed class FakeMetaMessengerStore
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<FakeMetaOutboundMessage>> _messagesByRecipient = new(StringComparer.Ordinal);
    private long _globalSequence;

    public FakeMetaOutboundMessage Capture(string recipientId, string version, JsonDocument requestDocument)
    {
        if (requestDocument.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Fake Meta request body must be a JSON object.");
        }

        var message = requestDocument.RootElement.GetProperty("message");
        var sequence = Interlocked.Increment(ref _globalSequence);
        var outbound = new FakeMetaOutboundMessage(
            Sequence: sequence,
            RecipientId: recipientId,
            Version: version,
            CapturedAtUtc: DateTime.UtcNow,
            Kind: ResolveKind(message),
            Text: TryGetText(message),
            TemplateType: TryGetTemplateType(message),
            Elements: ParseElements(message),
            Buttons: ParseButtons(message));

        var queue = _messagesByRecipient.GetOrAdd(recipientId, static _ => new ConcurrentQueue<FakeMetaOutboundMessage>());
        queue.Enqueue(outbound);
        return outbound;
    }

    public IReadOnlyList<FakeMetaOutboundMessage> GetMessages(string recipientId, long afterSequence)
    {
        if (!_messagesByRecipient.TryGetValue(recipientId, out var queue))
        {
            return Array.Empty<FakeMetaOutboundMessage>();
        }

        return queue
            .Where(message => message.Sequence > afterSequence)
            .OrderBy(message => message.Sequence)
            .ToArray();
    }

    public void Clear()
    {
        _messagesByRecipient.Clear();
        Interlocked.Exchange(ref _globalSequence, 0);
    }

    private static string ResolveKind(JsonElement message)
    {
        if (message.TryGetProperty("attachment", out var attachment) &&
            attachment.ValueKind == JsonValueKind.Object &&
            attachment.TryGetProperty("payload", out var payload) &&
            payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("template_type", out var templateType) &&
            templateType.ValueKind == JsonValueKind.String)
        {
            return templateType.GetString() ?? "template";
        }

        return message.TryGetProperty("text", out _) ? "text" : "unknown";
    }

    private static string? TryGetText(JsonElement message)
    {
        return message.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String
            ? textElement.GetString()
            : null;
    }

    private static string? TryGetTemplateType(JsonElement message)
    {
        if (!message.TryGetProperty("attachment", out var attachment) ||
            attachment.ValueKind != JsonValueKind.Object ||
            !attachment.TryGetProperty("payload", out var payload) ||
            payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("template_type", out var templateType) ||
            templateType.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return templateType.GetString();
    }

    private static IReadOnlyList<FakeMetaTemplateElement> ParseElements(JsonElement message)
    {
        if (!message.TryGetProperty("attachment", out var attachment) ||
            attachment.ValueKind != JsonValueKind.Object ||
            !attachment.TryGetProperty("payload", out var payload) ||
            payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("elements", out var elements) ||
            elements.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<FakeMetaTemplateElement>();
        }

        var result = new List<FakeMetaTemplateElement>();
        foreach (var element in elements.EnumerateArray())
        {
            result.Add(new FakeMetaTemplateElement(
                Title: TryGetString(element, "title"),
                Subtitle: TryGetString(element, "subtitle"),
                ImageUrl: TryGetString(element, "image_url"),
                Buttons: ParseButtons(element)));
        }

        return result;
    }

    private static IReadOnlyList<FakeMetaButton> ParseButtons(JsonElement node)
    {
        if (!node.TryGetProperty("buttons", out var buttons) || buttons.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<FakeMetaButton>();
        }

        var result = new List<FakeMetaButton>();
        foreach (var button in buttons.EnumerateArray())
        {
            result.Add(new FakeMetaButton(
                Title: TryGetString(button, "title"),
                Payload: TryGetString(button, "payload"),
                Type: TryGetString(button, "type")));
        }

        return result;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }
}

internal sealed record FakeMetaOutboundMessage(
    long Sequence,
    string RecipientId,
    string Version,
    DateTime CapturedAtUtc,
    string Kind,
    string? Text,
    string? TemplateType,
    IReadOnlyList<FakeMetaTemplateElement> Elements,
    IReadOnlyList<FakeMetaButton> Buttons);

internal sealed record FakeMetaTemplateElement(
    string? Title,
    string? Subtitle,
    string? ImageUrl,
    IReadOnlyList<FakeMetaButton> Buttons);

internal sealed record FakeMetaButton(
    string? Title,
    string? Payload,
    string? Type);
