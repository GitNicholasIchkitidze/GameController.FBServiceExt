using GameController.FBServiceExt.Application.Abstractions.State;
using GameController.FBServiceExt.Application.Contracts.Runtime;
using GameController.FBServiceExt.Options;
using GameController.FBServiceExt.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GameController.FBServiceExt.Tests.Startup;

public sealed class VotingRuntimeDefaultsHostedServiceTests
{
    [Fact]
    public async Task StartAsync_AppliesConfiguredDefaultShow_WhenRuntimeStateIsMissingShow()
    {
        var votingGateService = new InMemoryVotingGateService(new VotingRuntimeState(true, null));
        using var services = new ServiceCollection()
            .AddSingleton<IVotingGateService>(votingGateService)
            .BuildServiceProvider();
        var lifetime = new TestHostApplicationLifetime();
        var hostedService = new VotingRuntimeDefaultsHostedService(
            lifetime,
            services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(new VotingRuntimeDefaultsOptions
            {
                ApplyDefaultActiveShowIdWhenMissing = true,
                DefaultActiveShowId = "show1"
            }),
            NullLogger<VotingRuntimeDefaultsHostedService>.Instance);

        await hostedService.StartAsync(CancellationToken.None);
        lifetime.TriggerStarted();
        await WaitForAsync(() => votingGateService.CurrentState.ActiveShowId == "show1");

        Assert.Equal("show1", votingGateService.CurrentState.ActiveShowId);
    }

    [Fact]
    public async Task StartAsync_DoesNotOverrideExistingActiveShowId()
    {
        var votingGateService = new InMemoryVotingGateService(new VotingRuntimeState(true, "custom-show"));
        using var services = new ServiceCollection()
            .AddSingleton<IVotingGateService>(votingGateService)
            .BuildServiceProvider();
        var lifetime = new TestHostApplicationLifetime();
        var hostedService = new VotingRuntimeDefaultsHostedService(
            lifetime,
            services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(new VotingRuntimeDefaultsOptions
            {
                ApplyDefaultActiveShowIdWhenMissing = true,
                DefaultActiveShowId = "show1"
            }),
            NullLogger<VotingRuntimeDefaultsHostedService>.Instance);

        await hostedService.StartAsync(CancellationToken.None);
        lifetime.TriggerStarted();
        await Task.Delay(50);

        Assert.Equal("custom-show", votingGateService.CurrentState.ActiveShowId);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(condition());
    }

    private sealed class InMemoryVotingGateService : IVotingGateService
    {
        public InMemoryVotingGateService(VotingRuntimeState initialState)
        {
            CurrentState = initialState;
        }

        public VotingRuntimeState CurrentState { get; private set; }

        public ValueTask<VotingRuntimeState> GetStateAsync(CancellationToken cancellationToken) => ValueTask.FromResult(CurrentState);

        public ValueTask SetStateAsync(VotingRuntimeState state, CancellationToken cancellationToken)
        {
            CurrentState = state;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> IsVotingStartedAsync(CancellationToken cancellationToken) => ValueTask.FromResult(CurrentState.VotingStarted);

        public ValueTask SetVotingStartedAsync(bool started, CancellationToken cancellationToken)
        {
            CurrentState = CurrentState with { VotingStarted = started };
            return ValueTask.CompletedTask;
        }

        public ValueTask<string?> GetActiveShowIdAsync(CancellationToken cancellationToken) => ValueTask.FromResult(CurrentState.ActiveShowId);

        public ValueTask SetActiveShowIdAsync(string? activeShowId, CancellationToken cancellationToken)
        {
            CurrentState = CurrentState with { ActiveShowId = activeShowId };
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            if (!_stopping.IsCancellationRequested)
            {
                _stopping.Cancel();
            }
        }

        public void TriggerStarted()
        {
            if (!_started.IsCancellationRequested)
            {
                _started.Cancel();
            }
        }
    }
}


