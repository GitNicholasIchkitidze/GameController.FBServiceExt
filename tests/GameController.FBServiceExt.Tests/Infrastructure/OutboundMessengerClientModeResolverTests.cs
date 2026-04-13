using GameController.FBServiceExt.Application.Options;
using GameController.FBServiceExt.Infrastructure;

namespace GameController.FBServiceExt.Tests.Infrastructure;

public sealed class OutboundMessengerClientModeResolverTests
{
    [Fact]
    public void ResolveOutboundMessengerClientMode_DevelopmentRealSend_UsesMetaMessenger()
    {
        var options = new MetaMessengerOptions
        {
            Enabled = true,
            UseNoOpClient = false,
            UseFakeMetaStoreClient = false,
            PageAccessToken = "token"
        };

        var result = DependencyInjection.ResolveOutboundMessengerClientMode(options);

        Assert.Equal(DependencyInjection.OutboundMessengerClientMode.MetaMessenger, result);
    }

    [Fact]
    public void ResolveOutboundMessengerClientMode_SimulatorFakeMode_UsesFakeMetaStore()
    {
        var options = new MetaMessengerOptions
        {
            Enabled = true,
            UseNoOpClient = false,
            UseFakeMetaStoreClient = true
        };

        var result = DependencyInjection.ResolveOutboundMessengerClientMode(options);

        Assert.Equal(DependencyInjection.OutboundMessengerClientMode.FakeMetaStore, result);
    }

    [Fact]
    public void ResolveOutboundMessengerClientMode_NoOpEnabled_UsesNoOp()
    {
        var options = new MetaMessengerOptions
        {
            Enabled = true,
            UseNoOpClient = true,
            UseFakeMetaStoreClient = false
        };

        var result = DependencyInjection.ResolveOutboundMessengerClientMode(options);

        Assert.Equal(DependencyInjection.OutboundMessengerClientMode.NoOp, result);
    }
}
