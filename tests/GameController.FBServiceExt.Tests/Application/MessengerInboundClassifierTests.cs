using System.Text.Json;
using GameController.FBServiceExt.Application.Contracts.Normalization;
using GameController.FBServiceExt.Application.Services;
using GameController.FBServiceExt.Domain.Messaging;

namespace GameController.FBServiceExt.Tests.Application;

public sealed class MessengerInboundClassifierTests
{
    private static readonly string[] ForgetMeTokens = ["#forgetme"];
    private static readonly string[] VoteStartTokens = ["GET_STARTED", "_voteStartFlag"];

    [Fact]
    public void ClassifyMessagingItem_ForVoteStartMessage_ReturnsVoteStart()
    {
        using var document = JsonDocument.Parse("""
{"message":{"text":"GET_STARTED"}}
""");

        var kind = MessengerInboundClassifier.ClassifyMessagingItem(document.RootElement, ForgetMeTokens, VoteStartTokens);

        Assert.Equal(MessengerInboundMessageKind.VoteStart, kind);
    }

    [Fact]
    public void IsBusinessRelevantEvent_ForPlainMessage_ReturnsFalse()
    {
        var normalizedEvent = new NormalizedMessengerEvent(
            "mid-1",
            MessengerEventType.Message,
            "mid-1",
            "user-1",
            "page-1",
            DateTime.UtcNow,
            """
{"message":{"text":"hello"}}
""",
            Guid.NewGuid());

        var relevant = MessengerInboundClassifier.IsBusinessRelevantEvent(normalizedEvent, ForgetMeTokens, VoteStartTokens);

        Assert.False(relevant);
    }

    [Fact]
    public void IsBusinessRelevantEvent_ForPostback_ReturnsTrue()
    {
        var normalizedEvent = new NormalizedMessengerEvent(
            "pb-1",
            MessengerEventType.Postback,
            null,
            "user-1",
            "page-1",
            DateTime.UtcNow,
            """
{"postback":{"payload":"YES"}}
""",
            Guid.NewGuid());

        var relevant = MessengerInboundClassifier.IsBusinessRelevantEvent(normalizedEvent, ForgetMeTokens, VoteStartTokens);

        Assert.True(relevant);
    }
}
