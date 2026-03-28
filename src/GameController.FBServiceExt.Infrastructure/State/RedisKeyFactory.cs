namespace GameController.FBServiceExt.Infrastructure.State;

internal static class RedisKeyFactory
{
    public static string ProcessedEvent(string prefix, string eventId) => $"{prefix}:evt:processed:{eventId}";

    public static string VoteSession(string prefix, string recipientId, string userId) => $"{prefix}:vote:session:{recipientId}:{userId}";

    public static string UserLock(string prefix, string scope) => $"{prefix}:lock:user:{scope}";
}
