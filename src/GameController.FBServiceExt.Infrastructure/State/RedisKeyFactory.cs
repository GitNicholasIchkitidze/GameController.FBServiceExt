namespace GameController.FBServiceExt.Infrastructure.State;

internal static class RedisKeyFactory
{
    public static string ProcessedEvent(string prefix, string eventId) => $"{prefix}:evt:processed:{eventId}";


    public static string VoteCooldown(string prefix, string showId, string recipientId, string userId) => $"{prefix}:vote:cooldown:{showId}:{recipientId}:{userId}";

    public static string UserLock(string prefix, string scope) => $"{prefix}:lock:user:{scope}";

    public static string UserAccountName(string prefix, string userId) => $"{prefix}:user:account-name:{userId}";

    public static string VotingStarted(string prefix) => $"{prefix}:voting:started";

    public static string ActiveShowId(string prefix) => $"{prefix}:voting:active-show-id";

    public static string VotingStateChangedChannel(string prefix) => $"{prefix}:voting:state:changed";

    public static string FakeMetaRecipients(string prefix) => $"{prefix}:fake-meta:recipients";

    public static string FakeMetaSequence(string prefix) => $"{prefix}:fake-meta:sequence";

    public static string FakeMetaRecipientMessages(string prefix, string recipientId) => $"{prefix}:fake-meta:recipient:{recipientId}";

    public static string FakeMetaChannel(string prefix) => $"{prefix}:fake-meta:channel";
}