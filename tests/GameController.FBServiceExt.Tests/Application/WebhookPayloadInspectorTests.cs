using GameController.FBServiceExt.Application.Services;

namespace GameController.FBServiceExt.Tests.Application;

public sealed class WebhookPayloadInspectorTests
{
    private static readonly string[] ForgetMeTokens = ["#forgetme"];
    private static readonly string[] VoteStartTokens = ["GET_STARTED", "_voteStartFlag"];

    [Fact]
    public void Inspect_ForForgetMeMessage_DetectsBypassAndPreventsEarlyDrop()
    {
        const string payload = """
{"object":"page","entry":[{"messaging":[{"sender":{"id":"user-1"},"recipient":{"id":"page-1"},"timestamp":1710000001000,"message":{"mid":"m_1","text":"#forgetme"}}]}]}
""";

        var inspection = WebhookPayloadInspector.Inspect(payload, ForgetMeTokens, VoteStartTokens);

        Assert.True(inspection.ContainsForgetMeBypass);
        Assert.False(inspection.CanDropWhenVotingDisabled);
        Assert.False(inspection.CanDropAsGarbage);
        Assert.False(inspection.ContainsPostbackEvents);
        Assert.Equal(1, inspection.MessagingCount);
        Assert.Equal(1, inspection.ForgetMeMessageCount);
        Assert.Equal(0, inspection.GarbageMessageCount);
    }

    [Fact]
    public void Inspect_ForVoteStartMessage_AllowsVotingDisabledDropButNotGarbageDrop()
    {
        const string payload = """
{"object":"page","entry":[{"messaging":[{"sender":{"id":"user-1"},"recipient":{"id":"page-1"},"timestamp":1710000001000,"message":{"mid":"m_1","text":"GET_STARTED"}}]}]}
""";

        var inspection = WebhookPayloadInspector.Inspect(payload, ForgetMeTokens, VoteStartTokens);

        Assert.False(inspection.ContainsForgetMeBypass);
        Assert.True(inspection.CanDropWhenVotingDisabled);
        Assert.False(inspection.CanDropAsGarbage);
        Assert.False(inspection.ContainsPostbackEvents);
        Assert.True(inspection.ContainsVoteStartMessages);
        Assert.Equal(1, inspection.VoteStartMessageCount);
        Assert.Equal(0, inspection.GarbageMessageCount);
    }

    [Fact]
    public void Inspect_ForGarbageMessage_CanDropAsGarbage()
    {
        const string payload = """
{"object":"page","entry":[{"messaging":[{"sender":{"id":"user-1"},"recipient":{"id":"page-1"},"timestamp":1710000001000,"message":{"mid":"m_1","text":"hello there"}}]}]}
""";

        var inspection = WebhookPayloadInspector.Inspect(payload, ForgetMeTokens, VoteStartTokens);

        Assert.True(inspection.CanDropWhenVotingDisabled);
        Assert.True(inspection.CanDropAsGarbage);
        Assert.Equal(1, inspection.GarbageMessageCount);
        Assert.Equal(0, inspection.VoteStartMessageCount);
        Assert.Equal(0, inspection.ForgetMeMessageCount);
        Assert.Equal(0, inspection.PostbackCount);
    }

    [Fact]
    public void Inspect_ForPostback_KeepsRequestForWorkerDecision()
    {
        const string payload = """
{"object":"page","entry":[{"messaging":[{"sender":{"id":"user-1"},"recipient":{"id":"page-1"},"timestamp":1710000001000,"postback":{"payload":"YES"}}]}]}
""";

        var inspection = WebhookPayloadInspector.Inspect(payload, ForgetMeTokens, VoteStartTokens);

        Assert.True(inspection.ContainsPostbackEvents);
        Assert.False(inspection.CanDropWhenVotingDisabled);
        Assert.False(inspection.CanDropAsGarbage);
        Assert.False(inspection.ContainsForgetMeBypass);
        Assert.Equal(1, inspection.PostbackCount);
    }
}
