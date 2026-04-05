using System.Text.Json;
using System.Windows.Forms;

namespace GameController.FBServiceExt.FakeFBForSimulate;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!args.Any(static arg => string.Equals(arg, "--headless", StringComparison.OrdinalIgnoreCase)))
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, eventArgs) =>
            {
                MessageBox.Show(eventArgs.Exception.ToString(), "FakeFBForSimulate Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            {
                if (eventArgs.ExceptionObject is Exception exception)
                {
                    MessageBox.Show(exception.ToString(), "FakeFBForSimulate Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
        }

        var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var defaults = SimulatorDefaults.Load(settingsPath);

        if (args.Any(static arg => string.Equals(arg, "--headless", StringComparison.OrdinalIgnoreCase)))
        {
            return HeadlessSimulatorRunner.RunAsync(defaults, args).GetAwaiter().GetResult();
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new Form1(defaults));
        return 0;
    }
}

internal sealed class SimulatorDefaults
{
    public string WebhookUrl { get; init; } = "http://127.0.0.1:5277/api/facebook/webhooks";
    public string ListenerUrl { get; init; } = "http://127.0.0.1:5290";
    public string PageId { get; init; } = "PAGE_ID_SIMULATOR";
    public string AppSecret { get; init; } = string.Empty;
    public string StartToken { get; init; } = "GET_STARTED";
    public int DefaultUserCount { get; init; } = 200;
    public int DefaultDurationSeconds { get; init; } = 300;
    public int DefaultCooldownSeconds { get; init; } = 60;
    public int DefaultStartupJitterSeconds { get; init; } = 30;
    public int DefaultMinThinkMilliseconds { get; init; } = 250;
    public int DefaultMaxThinkMilliseconds { get; init; } = 1000;
    public int DefaultOutboundWaitSeconds { get; init; } = 15;
    public int DefaultFailureBackoffMinSeconds { get; init; } = 3;
    public int DefaultFailureBackoffMaxSeconds { get; init; } = 10;
    public IReadOnlyList<string> CooldownTextFragments { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RejectedTextFragments { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExpiredTextFragments { get; init; } = Array.Empty<string>();
    public string SqlConnectionString { get; init; } = "Server=127.0.0.1,14333;Database=GameControllerFBServiceExt;User ID=sa;Password=FbServiceExt_Strong_2026!;Encrypt=True;TrustServerCertificate=True;";
    public string RedisConnectionString { get; init; } = "localhost:6380";
    public string RedisKeyPrefix { get; init; } = "fbserviceext";
    public int DefaultManagedWorkerCount { get; init; } = 0;
    public string ManagedWorkerExecutablePath { get; init; } = string.Empty;
    public string ManagedWorkerEnvironmentName { get; init; } = "Simulator";

    public static SimulatorDefaults Load(string path)
    {
        if (!File.Exists(path))
        {
            return new SimulatorDefaults();
        }

        var root = JsonSerializer.Deserialize<SimulatorConfigurationRoot>(File.ReadAllText(path)) ?? new SimulatorConfigurationRoot();
        return root.Simulator ?? new SimulatorDefaults();
    }

    private sealed class SimulatorConfigurationRoot
    {
        public SimulatorDefaults? Simulator { get; set; }
    }
}
