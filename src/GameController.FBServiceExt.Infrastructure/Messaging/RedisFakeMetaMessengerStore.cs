using System.Text.Json;
using GameController.FBServiceExt.Application.Abstractions.Messaging;
using GameController.FBServiceExt.Infrastructure.Options;
using GameController.FBServiceExt.Infrastructure.State;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GameController.FBServiceExt.Infrastructure.Messaging;

public sealed class RedisFakeMetaMessengerStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan KeyTtl = TimeSpan.FromHours(6);
    private readonly RedisConnectionProvider _redisConnectionProvider;
    private readonly IOptionsMonitor<RedisOptions> _redisOptions;

    public RedisFakeMetaMessengerStore(
        RedisConnectionProvider redisConnectionProvider,
        IOptionsMonitor<RedisOptions> redisOptions)
    {
        _redisConnectionProvider = redisConnectionProvider;
        _redisOptions = redisOptions;
    }

    public ValueTask<FakeMetaOutboundMessage> CaptureTextAsync(
        string recipientId,
        string version,
        string messageText,
        CancellationToken cancellationToken)
    {
        var message = new FakeMetaOutboundMessage(
            Sequence: 0,
            RecipientId: recipientId,
            Version: version,
            CapturedAtUtc: DateTime.UtcNow,
            Kind: "text",
            Text: messageText,
            TemplateType: null,
            Elements: Array.Empty<FakeMetaTemplateElement>(),
            Buttons: Array.Empty<FakeMetaButton>());

        return AppendAsync(message, cancellationToken);
    }

    public ValueTask<FakeMetaOutboundMessage> CaptureButtonTemplateAsync(
        string recipientId,
        string version,
        string promptText,
        IReadOnlyCollection<MessengerPostbackButton> buttons,
        CancellationToken cancellationToken)
    {
        var message = new FakeMetaOutboundMessage(
            Sequence: 0,
            RecipientId: recipientId,
            Version: version,
            CapturedAtUtc: DateTime.UtcNow,
            Kind: "button",
            Text: promptText,
            TemplateType: "button",
            Elements: Array.Empty<FakeMetaTemplateElement>(),
            Buttons: buttons.Select(static button => new FakeMetaButton(button.Title, button.Payload, "postback")).ToArray());

        return AppendAsync(message, cancellationToken);
    }

    public ValueTask<FakeMetaOutboundMessage> CaptureGenericTemplateAsync(
        string recipientId,
        string version,
        IReadOnlyCollection<MessengerGenericTemplateElement> elements,
        CancellationToken cancellationToken)
    {
        var message = new FakeMetaOutboundMessage(
            Sequence: 0,
            RecipientId: recipientId,
            Version: version,
            CapturedAtUtc: DateTime.UtcNow,
            Kind: "generic",
            Text: null,
            TemplateType: "generic",
            Elements: elements.Select(static element => new FakeMetaTemplateElement(
                element.Title,
                element.Subtitle,
                element.ImageUrl,
                element.Buttons.Select(static button => new FakeMetaButton(button.Title, button.Payload, "postback")).ToArray())).ToArray(),
            Buttons: Array.Empty<FakeMetaButton>());

        return AppendAsync(message, cancellationToken);
    }

    // fake-meta endpoint-ზე მოსულ outbound request-ს შლის და simulator-readable ფორმატით ინახავს.
    public async ValueTask<FakeMetaOutboundMessage> CaptureRequestAsync(
        string recipientId,
        string version,
        JsonDocument requestDocument,
        CancellationToken cancellationToken)
    {
        if (requestDocument.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Fake Meta request body must be a JSON object.");
        }

        var message = requestDocument.RootElement.GetProperty("message");
        var outbound = new FakeMetaOutboundMessage(
            Sequence: 0,
            RecipientId: recipientId,
            Version: version,
            CapturedAtUtc: DateTime.UtcNow,
            Kind: ResolveKind(message),
            Text: TryGetText(message),
            TemplateType: TryGetTemplateType(message),
            Elements: ParseElements(message),
            Buttons: ParseButtons(message));

        return await AppendAsync(outbound, cancellationToken);
    }

    // კონკრეტული recipient-ისთვის დაგროვილ outbound შეტყობინებებს sequence-ის მიხედვით აბრუნებს.
    public async ValueTask<IReadOnlyList<FakeMetaOutboundMessage>> GetMessagesAsync(
        string recipientId,
        long afterSequence,
        CancellationToken cancellationToken)
    {
        var database = await _redisConnectionProvider.GetDatabaseAsync(cancellationToken);
        var values = await database.SortedSetRangeByScoreAsync(
            GetRecipientMessagesKey(recipientId),
            afterSequence + 1,
            double.PositiveInfinity,
            Exclude.None,
            Order.Ascending);

        if (values.Length == 0)
        {
            return Array.Empty<FakeMetaOutboundMessage>();
        }

        var results = new List<FakeMetaOutboundMessage>(values.Length);
        foreach (var value in values)
        {
            if (value.IsNullOrEmpty)
            {
                continue;
            }

            var message = JsonSerializer.Deserialize<FakeMetaOutboundMessage>(value!, SerializerOptions);
            if (message is not null)
            {
                results.Add(message);
            }
        }

        return results;
    }

    // simulator polling-ისთვის ცოტა ხანს ელოდება ახალ outbound message-ებს და შემდეგ აბრუნებს სიას.
    public async ValueTask<IReadOnlyList<FakeMetaOutboundMessage>> WaitForMessagesAsync(
        string recipientId,
        long afterSequence,
        TimeSpan waitTime,
        CancellationToken cancellationToken)
    {
        if (waitTime <= TimeSpan.Zero)
        {
            return await GetMessagesAsync(recipientId, afterSequence, cancellationToken);
        }

        var deadline = DateTime.UtcNow.Add(waitTime);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var messages = await GetMessagesAsync(recipientId, afterSequence, cancellationToken);
            if (messages.Count > 0)
            {
                return messages;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return Array.Empty<FakeMetaOutboundMessage>();
            }

            var delay = remaining > TimeSpan.FromMilliseconds(200)
                ? TimeSpan.FromMilliseconds(200)
                : remaining;
            await Task.Delay(delay, cancellationToken);
        }
    }

    // fake-meta capture state-ს მთლიანად წმენდს Redis-იდან ახალი სიმულაციის დაწყებამდე.
    public async ValueTask ClearAsync(CancellationToken cancellationToken)
    {
        var database = await _redisConnectionProvider.GetDatabaseAsync(cancellationToken);
        var recipientsKey = GetRecipientsKey();
        var sequenceKey = GetSequenceKey();
        var recipientIds = await database.SetMembersAsync(recipientsKey);

        var keys = new List<RedisKey>(recipientIds.Length + 2)
        {
            recipientsKey,
            sequenceKey
        };

        foreach (var recipientId in recipientIds)
        {
            if (!recipientId.IsNullOrEmpty)
            {
                keys.Add(GetRecipientMessagesKey(recipientId!));
            }
        }

        if (keys.Count > 0)
        {
            await database.KeyDeleteAsync(keys.ToArray());
        }
    }

    // outbound fake-meta message-ს sequence ანიჭებს, Redis-ში ინახავს და subscriber-ებს ატყობინებს.
    private async ValueTask<FakeMetaOutboundMessage> AppendAsync(FakeMetaOutboundMessage message, CancellationToken cancellationToken)
    {
        var connection = await _redisConnectionProvider.GetConnectionAsync(cancellationToken);
        var database = connection.GetDatabase();
        var sequence = await database.StringIncrementAsync(GetSequenceKey());
        var captured = message with
        {
            Sequence = sequence,
            CapturedAtUtc = DateTime.UtcNow
        };

        var serialized = JsonSerializer.Serialize(captured, SerializerOptions);
        var recipientMessagesKey = GetRecipientMessagesKey(captured.RecipientId);
        var recipientsKey = GetRecipientsKey();
        var sequenceKey = GetSequenceKey();

        await database.SortedSetAddAsync(recipientMessagesKey, serialized, sequence);
        await database.SetAddAsync(recipientsKey, captured.RecipientId);
        await database.KeyExpireAsync(recipientMessagesKey, KeyTtl);
        await database.KeyExpireAsync(recipientsKey, KeyTtl);
        await database.KeyExpireAsync(sequenceKey, KeyTtl);
        await connection.GetSubscriber().PublishAsync(new RedisChannel(GetChannelKey(), RedisChannel.PatternMode.Literal), serialized);

        return captured;
    }

    private string GetRecipientsKey() => RedisKeyFactory.FakeMetaRecipients(_redisOptions.CurrentValue.KeyPrefix);

    private string GetSequenceKey() => RedisKeyFactory.FakeMetaSequence(_redisOptions.CurrentValue.KeyPrefix);

    private string GetRecipientMessagesKey(string recipientId) => RedisKeyFactory.FakeMetaRecipientMessages(_redisOptions.CurrentValue.KeyPrefix, recipientId);

    private string GetChannelKey() => RedisKeyFactory.FakeMetaChannel(_redisOptions.CurrentValue.KeyPrefix);

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

        var results = new List<FakeMetaTemplateElement>();
        foreach (var element in elements.EnumerateArray())
        {
            results.Add(new FakeMetaTemplateElement(
                TryGetString(element, "title"),
                TryGetString(element, "subtitle"),
                TryGetString(element, "image_url"),
                ParseButtons(element)));
        }

        return results;
    }

    private static IReadOnlyList<FakeMetaButton> ParseButtons(JsonElement node)
    {
        if (!node.TryGetProperty("buttons", out var buttons) || buttons.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<FakeMetaButton>();
        }

        var results = new List<FakeMetaButton>();
        foreach (var button in buttons.EnumerateArray())
        {
            results.Add(new FakeMetaButton(
                TryGetString(button, "title"),
                TryGetString(button, "payload"),
                TryGetString(button, "type")));
        }

        return results;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }
}

public sealed record FakeMetaOutboundMessage(
    long Sequence,
    string RecipientId,
    string Version,
    DateTime CapturedAtUtc,
    string Kind,
    string? Text,
    string? TemplateType,
    IReadOnlyList<FakeMetaTemplateElement> Elements,
    IReadOnlyList<FakeMetaButton> Buttons);

public sealed record FakeMetaTemplateElement(
    string? Title,
    string? Subtitle,
    string? ImageUrl,
    IReadOnlyList<FakeMetaButton> Buttons);

public sealed record FakeMetaButton(
    string? Title,
    string? Payload,
    string? Type);


