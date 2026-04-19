using System.Text.Json;
using GameController.FBServiceExt.FakeFBForSimulate;

namespace GameController.FBServiceExt.Tests.Simulator;

public sealed class SimulatorManagedWorkerContractTests
{
    [Fact]
    public void ResolveEffectiveEnvironmentName_Blank_ReturnsSimulator()
    {
        var environmentName = SimulatorManagedWorkerContract.ResolveEffectiveEnvironmentName(string.Empty);

        Assert.Equal("Simulator", environmentName);
    }

    [Fact]
    public void Probe_SimulatorConfig_ResolvesFakeMetaStore()
    {
        var root = CreateTempRoot();
        try
        {
            var workerDirectory = CreateWorkerLayout(root, "Simulator", useFakeMetaStoreClient: true, useNoOpClient: false);
            var executablePath = Path.Combine(workerDirectory, "GameController.FBServiceExt.Worker.exe");
            File.WriteAllText(executablePath, string.Empty);

            var contract = SimulatorManagedWorkerContract.Probe(executablePath, "Simulator");

            Assert.Equal("Simulator", contract.EnvironmentName);
            Assert.Equal(ManagedWorkerOutboundMode.FakeMetaStore, contract.ResolvedOutboundMode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnsureFakeMetaCompatible_MetaMessengerMode_Throws()
    {
        var root = CreateTempRoot();
        try
        {
            var workerDirectory = CreateWorkerLayout(root, "Development", useFakeMetaStoreClient: false, useNoOpClient: false);
            var executablePath = Path.Combine(workerDirectory, "GameController.FBServiceExt.Worker.exe");
            File.WriteAllText(executablePath, string.Empty);

            var defaults = new SimulatorDefaults
            {
                ManagedWorkerExecutablePath = executablePath,
                ManagedWorkerEnvironmentName = "Development"
            };

            var exception = Assert.Throws<InvalidOperationException>(() => SimulatorManagedWorkerContract.EnsureFakeMetaCompatible(defaults, FakeFacebookSimulatorEngine.FakeMetaTransportMode));

            Assert.Contains("FakeFB transport mode is 'RedisStore'", exception.Message);
            Assert.Contains("resolved worker outbound mode is 'MetaMessenger'", exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"fbserviceext-simulator-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateWorkerLayout(string root, string environmentName, bool useFakeMetaStoreClient, bool useNoOpClient)
    {
        var workerDirectory = Path.Combine(root, "worker", "bin", "Debug", "net8.0");
        Directory.CreateDirectory(workerDirectory);

        File.WriteAllText(
            Path.Combine(root, "appsettings.Shared.json"),
            JsonSerializer.Serialize(new
            {
                RabbitMq = new { HostName = "localhost", Password = "secret", RawIngressQueueName = "raw", NormalizedEventQueueName = "normalized" },
                Redis = new { ConnectionString = "localhost:6380" },
                SqlStorage = new { ConnectionString = "Server=.;Database=Db;Trusted_Connection=True;" }
            }));

        File.WriteAllText(Path.Combine(workerDirectory, "appsettings.json"), "{}");
        File.WriteAllText(
            Path.Combine(workerDirectory, $"appsettings.{environmentName}.json"),
            JsonSerializer.Serialize(new
            {
                MetaMessenger = new
                {
                    Enabled = true,
                    UseNoOpClient = useNoOpClient,
                    UseFakeMetaStoreClient = useFakeMetaStoreClient
                }
            }));

        return workerDirectory;
    }
}
