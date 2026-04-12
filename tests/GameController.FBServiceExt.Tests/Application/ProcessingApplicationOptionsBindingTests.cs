using GameController.FBServiceExt.Application;
using GameController.FBServiceExt.Application.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GameController.FBServiceExt.Tests.Application;

public sealed class ProcessingApplicationOptionsBindingTests
{
    [Fact]
    public void AddProcessingApplication_Binds_NormalizedEventStorage_Mode_FromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{VotingWorkflowOptions.SectionName}:ConfirmationTimeout"] = "00:01:00",
                [$"{VotingWorkflowOptions.SectionName}:Cooldown"] = "00:05:00",
                [$"{VotingWorkflowOptions.SectionName}:ProcessedEventRetention"] = "01:00:00",
                [$"{VotingWorkflowOptions.SectionName}:ProcessingLockTimeout"] = "00:00:30",
                [$"{VotingWorkflowOptions.SectionName}:PayloadSignatureSecret"] = "test-secret",
                [$"{VotingWorkflowOptions.SectionName}:VoteStartTokens:0"] = "GET_STARTED",
                [$"{DataErasureOptions.SectionName}:ConfirmationTimeout"] = "00:05:00",
                [$"{DataErasureOptions.SectionName}:ConfirmationPayloadSecret"] = "erase-secret",
                [$"{MessengerContentOptions.SectionName}:ForgetMeTokens:0"] = "FORGET_ME",
                [$"{NormalizedEventStorageOptions.SectionName}:Mode"] = "Disabled"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddProcessingApplication(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<NormalizedEventStorageOptions>>();

        Assert.Equal(NormalizedEventStorageMode.Disabled, options.CurrentValue.Mode);
    }
}