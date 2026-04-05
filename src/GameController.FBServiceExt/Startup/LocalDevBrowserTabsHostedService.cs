using System.Diagnostics;
using GameController.FBServiceExt.Options;
using Microsoft.Extensions.Options;

namespace GameController.FBServiceExt.Startup;

public sealed class LocalDevBrowserTabsHostedService : IHostedService
{
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly IOptionsMonitor<DevLogViewerOptions> _optionsMonitor;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<LocalDevBrowserTabsHostedService> _logger;
    private int _started;

    public LocalDevBrowserTabsHostedService(
        IHostApplicationLifetime applicationLifetime,
        IOptionsMonitor<DevLogViewerOptions> optionsMonitor,
        IWebHostEnvironment environment,
        ILogger<LocalDevBrowserTabsHostedService> logger)
    {
        _applicationLifetime = applicationLifetime;
        _optionsMonitor = optionsMonitor;
        _environment = environment;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsLocalDevLikeEnvironment())
        {
            return Task.CompletedTask;
        }

        _applicationLifetime.ApplicationStarted.Register(() => _ = OpenTabsAsync());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task OpenTabsAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        var options = _optionsMonitor.CurrentValue;
        if (!options.OpenAdditionalTabsOnStart || options.StartupTabs.Length == 0)
        {
            return;
        }

        await Task.Delay(1200);

        foreach (var tab in options.StartupTabs.Where(static value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = tab,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to open startup browser tab {TabUrl}.", tab);
            }
        }
    }

    private bool IsLocalDevLikeEnvironment()
    {
        return _environment.IsDevelopment() ||
               _environment.IsEnvironment("Simulator") ||
               _environment.IsEnvironment("Performance") ||
               _environment.IsEnvironment("PerformanceFakeFb") ||
               _environment.IsEnvironment("PerformanceRealOutbound");
    }
}
