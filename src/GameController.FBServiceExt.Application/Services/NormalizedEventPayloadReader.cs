using System.Text.Json;

namespace GameController.FBServiceExt.Application.Services;

internal static class NormalizedEventPayloadReader
{
    public static string? GetMessageText(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        if (!document.RootElement.TryGetProperty("message", out var message))
        {
            return null;
        }

        return message.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString()
            : null;
    }

    public static string? GetPostbackPayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        if (!document.RootElement.TryGetProperty("postback", out var postback))
        {
            return null;
        }

        return postback.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.String
            ? payload.GetString()
            : null;
    }
}
