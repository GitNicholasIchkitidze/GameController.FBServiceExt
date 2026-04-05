using GameController.FBServiceExt.Application.Options;
using GameController.FBServiceExt.Infrastructure.Messaging;

namespace GameController.FBServiceExt.Tests.Infrastructure;

public sealed class MetaMessengerClientTests
{
    [Fact]
    public void ResolveBaseUrl_SimulatedRecipient_WithNonLocalBaseUrl_ReroutesToSimulatorEndpoint()
    {
        var options = new MetaMessengerOptions
        {
            GraphApiBaseUrl = "https://graph.facebook.com",
            SimulatorGraphApiBaseUrl = "http://127.0.0.1:5290"
        };

        var result = MetaMessengerClient.ResolveBaseUrl(options, "simulate-user-000031");

        Assert.Equal("http://127.0.0.1:5290", result);
    }

    [Fact]
    public void ResolveBaseUrl_RealRecipient_KeepsConfiguredBaseUrl()
    {
        var options = new MetaMessengerOptions
        {
            GraphApiBaseUrl = "https://graph.facebook.com"
        };

        var result = MetaMessengerClient.ResolveBaseUrl(options, "1234567890");

        Assert.Equal("https://graph.facebook.com", result);
    }
}
