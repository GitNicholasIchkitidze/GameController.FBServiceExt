using Microsoft.Extensions.Configuration;

namespace GameController.FBServiceExt.FakeFBForSimulate;

internal static class SimulatorManagedWorkerContract
{
    public const string DefaultEnvironmentName = "Simulator";

    public static string ResolveEffectiveEnvironmentName(string? configuredEnvironmentName)
        => string.IsNullOrWhiteSpace(configuredEnvironmentName)
            ? DefaultEnvironmentName
            : configuredEnvironmentName.Trim();

    public static ManagedWorkerContractInfo Probe(SimulatorDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);

        var executablePath = ManagedWorkerProcessManager.ResolveWorkerExecutablePath(defaults);
        return Probe(executablePath, ResolveEffectiveEnvironmentName(defaults.ManagedWorkerEnvironmentName));
    }

    internal static ManagedWorkerContractInfo Probe(string workerExecutablePath, string environmentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);

        if (!File.Exists(workerExecutablePath))
        {
            throw new FileNotFoundException("Managed worker executable was not found.", workerExecutablePath);
        }

        var workerDirectory = Path.GetDirectoryName(workerExecutablePath)
            ?? throw new InvalidOperationException("Managed worker directory could not be resolved.");

        var configuration = BuildWorkerConfiguration(workerDirectory, environmentName.Trim());
        var enabled = configuration.GetValue<bool>("MetaMessenger:Enabled");
        var useNoOpClient = configuration.GetValue<bool>("MetaMessenger:UseNoOpClient");
        var useFakeMetaStoreClient = configuration.GetValue<bool>("MetaMessenger:UseFakeMetaStoreClient");
        var graphApiBaseUrl = configuration["MetaMessenger:GraphApiBaseUrl"] ?? string.Empty;
        var simulatorGraphApiBaseUrl = configuration["MetaMessenger:SimulatorGraphApiBaseUrl"] ?? string.Empty;
        var mode = ResolveOutboundMode(useNoOpClient, useFakeMetaStoreClient);

        return new ManagedWorkerContractInfo(
            workerExecutablePath,
            environmentName.Trim(),
            mode,
            enabled,
            useNoOpClient,
            useFakeMetaStoreClient,
            graphApiBaseUrl,
            simulatorGraphApiBaseUrl);
    }

    public static void EnsureFakeMetaCompatible(SimulatorDefaults defaults, string fakeMetaTransportMode)
    {
        var contract = Probe(defaults);
        if (string.Equals(contract.ResolvedOutboundMode, ManagedWorkerOutboundMode.FakeMetaStore, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Simulator managed worker contract is invalid. FakeFB transport mode is '{fakeMetaTransportMode}', managed worker environment is '{contract.EnvironmentName}', and resolved worker outbound mode is '{contract.ResolvedOutboundMode}'. Configure FakeFB-managed workers to use the Simulator environment so the worker resolves to FakeMetaStore outbound.");
    }

    private static IConfigurationRoot BuildWorkerConfiguration(string workerDirectory, string environmentName)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(workerDirectory);

        foreach (var sharedPath in ResolveSharedConfigPaths(workerDirectory, environmentName))
        {
            builder.AddJsonFile(sharedPath, optional: false, reloadOnChange: false);
        }

        builder
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: false);

        return builder.Build();
    }

    private static IEnumerable<string> ResolveSharedConfigPaths(string contentRootPath, string environmentName)
    {
        foreach (var root in EnumerateCandidateRoots(contentRootPath))
        {
            var shared = Path.Combine(root.FullName, "appsettings.Shared.json");
            if (!File.Exists(shared))
            {
                continue;
            }

            yield return shared;

            var environmentShared = Path.Combine(root.FullName, $"appsettings.Shared.{environmentName}.json");
            if (File.Exists(environmentShared))
            {
                yield return environmentShared;
            }

            yield break;
        }
    }

    private static IEnumerable<DirectoryInfo> EnumerateCandidateRoots(string contentRootPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var startPath in new[]
                 {
                     contentRootPath,
                     AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                 })
        {
            if (string.IsNullOrWhiteSpace(startPath) || !Directory.Exists(startPath))
            {
                continue;
            }

            var current = new DirectoryInfo(startPath);
            while (current is not null && seen.Add(current.FullName))
            {
                yield return current;
                current = current.Parent;
            }
        }
    }

    private static string ResolveOutboundMode(bool useNoOpClient, bool useFakeMetaStoreClient)
    {
        if (useNoOpClient)
        {
            return ManagedWorkerOutboundMode.NoOp;
        }

        if (useFakeMetaStoreClient)
        {
            return ManagedWorkerOutboundMode.FakeMetaStore;
        }

        return ManagedWorkerOutboundMode.MetaMessenger;
    }
}

internal static class ManagedWorkerOutboundMode
{
    public const string NoOp = "NoOp";
    public const string FakeMetaStore = "FakeMetaStore";
    public const string MetaMessenger = "MetaMessenger";
}

internal sealed record ManagedWorkerContractInfo(
    string ExecutablePath,
    string EnvironmentName,
    string ResolvedOutboundMode,
    bool MetaMessengerEnabled,
    bool UseNoOpClient,
    bool UseFakeMetaStoreClient,
    string GraphApiBaseUrl,
    string SimulatorGraphApiBaseUrl);
