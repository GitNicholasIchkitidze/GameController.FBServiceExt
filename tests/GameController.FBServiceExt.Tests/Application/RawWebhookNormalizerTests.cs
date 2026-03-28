using GameController.FBServiceExt.Application.Contracts.RawIngress;
using GameController.FBServiceExt.Application.Services;
using GameController.FBServiceExt.Domain.Messaging;

namespace GameController.FBServiceExt.Tests.Application;

public sealed class RawWebhookNormalizerTests
{
    [Fact]
    public async Task NormalizeAsync_ExpandsMessagingAndStandbyEvents()
    {
        const string payload = "{\"object\":\"page\",\"entry\":[{\"messaging\":[{\"sender\":{\"id\":\"user-1\"},\"recipient\":{\"id\":\"page-1\"},\"timestamp\":1710000001000,\"message\":{\"mid\":\"m_1\",\"text\":\"GET_STARTED\"}},{\"sender\":{\"id\":\"user-1\"},\"recipient\":{\"id\":\"page-1\"},\"timestamp\":1710000002000,\"postback\":{\"title\":\"Vote\",\"payload\":\"candidate-1\"}}],\"standby\":[{\"sender\":{\"id\":\"user-2\"},\"recipient\":{\"id\":\"page-1\"},\"timestamp\":1710000003000,\"message\":{\"text\":\"hello\"}}]}]}";

        var envelope = new RawWebhookEnvelope(
            Guid.NewGuid(),
            "facebook-messenger",
            "req-1",
            DateTime.UtcNow,
            new Dictionary<string, string[]>(),
            payload);

        var normalizer = new RawWebhookNormalizer();

        var events = await normalizer.NormalizeAsync(envelope, CancellationToken.None);

        Assert.Equal(3, events.Count);
        Assert.Collection(events,
            first =>
            {
                Assert.Equal(MessengerEventType.Message, first.EventType);
                Assert.Equal("m_1", first.EventId);
            },
            second =>
            {
                Assert.Equal(MessengerEventType.Postback, second.EventType);
                Assert.StartsWith("cmp_", second.EventId);
            },
            third => Assert.Equal(MessengerEventType.Standby, third.EventType));
    }

    [Fact]
    public void Create_UsesMessageId_WhenItExists()
    {
        var id = MessengerEventIdentityFactory.Create(
            "mid-123",
            "user-1",
            "page-1",
            DateTime.UtcNow,
            MessengerEventType.Message,
            "{}");

        Assert.Equal("mid-123", id);
    }
}
