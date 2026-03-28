using System.Security.Cryptography;
using System.Text;
using GameController.FBServiceExt.Domain.Messaging;

namespace GameController.FBServiceExt.Application.Services;

public static class MessengerEventIdentityFactory
{
    public static string Create(
        string? messageId,
        string? senderId,
        string? recipientId,
        DateTime occurredAtUtc,
        MessengerEventType eventType,
        string payloadJson)
    {
        if (!string.IsNullOrWhiteSpace(messageId))
        {
            return messageId;
        }

        var composite = string.Join('|',
            senderId ?? string.Empty,
            recipientId ?? string.Empty,
            occurredAtUtc.Ticks.ToString(),
            eventType.ToString(),
            payloadJson);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(composite));
        return $"cmp_{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
