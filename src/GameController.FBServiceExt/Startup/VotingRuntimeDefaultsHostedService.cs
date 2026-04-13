using GameController.FBServiceExt.Application.Abstractions.State;
using GameController.FBServiceExt.Options;
using Microsoft.Extensions.Options;

namespace GameController.FBServiceExt.Startup;

public sealed class VotingRuntimeDefaultsHostedService : IHostedService
{
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IOptions<VotingRuntimeDefaultsOptions> _options;
    private readonly ILogger<VotingRuntimeDefaultsHostedService> _logger;
    private int _started;

    public VotingRuntimeDefaultsHostedService(
        IHostApplicationLifetime applicationLifetime,
        IServiceScopeFactory serviceScopeFactory,
        IOptions<VotingRuntimeDefaultsOptions> options,
        ILogger<VotingRuntimeDefaultsHostedService> logger)
    {
        _applicationLifetime = applicationLifetime;
        _serviceScopeFactory = serviceScopeFactory;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _applicationLifetime.ApplicationStarted.Register(() => _ = InitializeAsync());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task InitializeAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        var options = _options.Value;
        var defaultActiveShowId = options.DefaultActiveShowId?.Trim();
        if (!options.ApplyDefaultActiveShowIdWhenMissing || string.IsNullOrWhiteSpace(defaultActiveShowId))
        {
            return;
        }

        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var votingGateService = scope.ServiceProvider.GetRequiredService<IVotingGateService>();
            var state = await votingGateService.GetStateAsync(CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(state.ActiveShowId))
            {
                return;
            }

            await votingGateService.SetActiveShowIdAsync(defaultActiveShowId, CancellationToken.None);
            _logger.LogInformation(
                "Applied default ActiveShowId from configuration because runtime state was empty. ActiveShowId: {ActiveShowId}",
                defaultActiveShowId);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to apply default ActiveShowId from configuration at startup.");
        }
    }
}
