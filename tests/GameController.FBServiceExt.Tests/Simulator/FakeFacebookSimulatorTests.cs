using System.Text.Json;
using GameController.FBServiceExt.FakeFBForSimulate;

namespace GameController.FBServiceExt.Tests.Simulator;

public sealed class FakeFacebookSimulatorTests
{
    private static readonly SimulatorTextPatterns Patterns = new(
        ["ბოლო ხმის მიცემიდან", "ხელახლა ხმის მიცემას შეძლებთ"],
        ["დადასტურება არასწორია"],
        ["დადასტურების დრო ამოიწურა"],
        ["ვოტინგი ამჟამად არ არის აქტიური"]);

    [Fact]
    public void Classify_CooldownText_ReturnsCooldown()
    {
        var message = CreateTextMessage("ბოლო ხმის მიცემიდან ცოტა დრო გავიდა და ხელახლა ხმის მიცემას შეძლებთ მოგვიანებით.");

        var result = SimulatorTextClassifier.Classify(message, Patterns);

        Assert.Equal(SimulatorTextOutcome.Cooldown, result);
    }

    [Fact]
    public void Classify_RejectedText_ReturnsRejected()
    {
        var message = CreateTextMessage("დადასტურება არასწორია");

        var result = SimulatorTextClassifier.Classify(message, Patterns);

        Assert.Equal(SimulatorTextOutcome.Rejected, result);
    }

    [Fact]
    public void Classify_ExpiredText_ReturnsExpired()
    {
        var message = CreateTextMessage("დადასტურების დრო ამოიწურა");

        var result = SimulatorTextClassifier.Classify(message, Patterns);

        Assert.Equal(SimulatorTextOutcome.Expired, result);
    }

    [Fact]
    public void Classify_InactiveVotingText_ReturnsInactive()
    {
        var message = CreateTextMessage("ვოტინგი ამჟამად არ არის აქტიური");

        var result = SimulatorTextClassifier.Classify(message, Patterns);

        Assert.Equal(SimulatorTextOutcome.Inactive, result);
    }

    [Fact]
    public void Classify_AcceptedText_ReturnsAccepted()
    {
        var message = CreateTextMessage("თქვენ ხმა მიეცით კანდიდატს. მადლობა.");

        var result = SimulatorTextClassifier.Classify(message, Patterns);

        Assert.Equal(SimulatorTextOutcome.Accepted, result);
    }

    [Fact]
    public void Classify_UnknownText_ReturnsOther()
    {
        var message = CreateTextMessage("random text");

        var result = SimulatorTextClassifier.Classify(message, Patterns);

        Assert.Equal(SimulatorTextOutcome.Other, result);
    }

    [Fact]
    public void ConfirmationTemplate_IsRecognized()
    {
        var message = CreateConfirmationMessage("ACCEPT");

        Assert.True(message.IsConfirmationMessage);
        Assert.NotNull(message.FindConfirmationAcceptButton());
    }

    [Fact]
    public async Task WaitForMessageAsync_PreservesUnmatchedMessages()
    {
        var hub = new SimulatorMessageHub();
        var confirmation = CreateConfirmationMessage("ACCEPT");
        var text = CreateTextMessage("თქვენ ხმა მიეცით კანდიდატს. მადლობა.", sequence: 2);
        hub.Append(confirmation);
        hub.Append(text);

        var first = await hub.WaitForMessageAsync("simulate-user-000001", static message => message.IsTextMessage, TimeSpan.FromSeconds(1), CancellationToken.None);
        var second = await hub.WaitForMessageAsync("simulate-user-000001", static message => message.IsConfirmationMessage, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(text, first);
        Assert.Equal(confirmation, second);
    }

    [Fact]
    public async Task WaitForMessageAsync_PreservesConfirmationWhileWaitingForFinalText()
    {
        var hub = new SimulatorMessageHub();
        var confirmation = CreateConfirmationMessage("ACCEPT");
        var finalText = CreateTextMessage("თქვენ ხმა მიეცით კანდიდატს. მადლობა.", sequence: 2);
        hub.Append(confirmation);
        hub.Append(finalText);

        var text = await hub.WaitForMessageAsync("simulate-user-000001", static message => message.IsTextMessage, TimeSpan.FromSeconds(1), CancellationToken.None);
        var confirmationAfterText = await hub.WaitForMessageAsync("simulate-user-000001", static message => message.IsConfirmationMessage, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(finalText, text);
        Assert.Equal(confirmation, confirmationAfterText);
    }

    [Fact]
    public async Task DiscardPendingNonTextMessages_RemovesOnlyNonTextMessages()
    {
        var hub = new SimulatorMessageHub();
        var confirmation = CreateConfirmationMessage("ACCEPT");
        var text = CreateTextMessage("თქვენ ხმა მიეცით კანდიდატს. მადლობა.", sequence: 2);
        hub.Append(confirmation);
        hub.Append(text);

        hub.DiscardPendingNonTextMessages("simulate-user-000001");

        var remaining = await hub.WaitForMessageAsync("simulate-user-000001", static message => message.IsTextMessage, TimeSpan.FromSeconds(1), CancellationToken.None);
        var discarded = await hub.WaitForMessageAsync("simulate-user-000001", static message => message.IsConfirmationMessage, TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.Equal(text, remaining);
        Assert.Null(discarded);
    }

    private static FakeOutboundMessage CreateTextMessage(string text, long sequence = 1)
        => new(sequence, "simulate-user-000001", "v24.0", "text", text, null, [], []);

    private static FakeOutboundMessage CreateConfirmationMessage(string action, long sequence = 1)
        => new(
            sequence,
            "simulate-user-000001",
            "v24.0",
            "generic",
            null,
            "generic",
            [new FakeTemplateElement("confirm", null, null, [new FakeButton("yes", BuildConfirmationPayload(action), "postback")])],
            []);

    private static string BuildConfirmationPayload(string action)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new { action });
        var base64 = Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"CONFIRM1:{base64}:sig";
    }
}

