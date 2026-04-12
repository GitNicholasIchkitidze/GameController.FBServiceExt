using GameController.FBServiceExt.Application.Abstractions.Observability;
using GameController.FBServiceExt.Application.Contracts.Observability;
using GameController.FBServiceExt.DevAdmin;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GameController.FBServiceExt.Tests.DevAdmin;

public sealed class LocalWorkerManagerTests
{
    [Fact]
    public async Task EnsureWorkerCountAsync_StartsRequestedWorkers()
    {
        var runtimeReader = new FakeRuntimeMetricsSnapshotReader();
        var processFactory = new FakeLocalWorkerProcessFactory();
        var manager = CreateManager(processFactory, runtimeReader);

        var snapshot = await manager.EnsureWorkerCountAsync(3, CancellationToken.None);

        Assert.Equal(3, snapshot.DesiredManagedWorkerCount);
        Assert.Equal(3, snapshot.ManagedWorkers.Count);
        Assert.Equal(new[] { 1, 2, 3 }, snapshot.ManagedWorkers.Select(static worker => worker.Slot));
    }

    [Fact]
    public async Task EnsureWorkerCountAsync_ReducesToRequestedWorkers()
    {
        var runtimeReader = new FakeRuntimeMetricsSnapshotReader();
        var processFactory = new FakeLocalWorkerProcessFactory();
        var manager = CreateManager(processFactory, runtimeReader);

        await manager.EnsureWorkerCountAsync(3, CancellationToken.None);
        var snapshot = await manager.EnsureWorkerCountAsync(1, CancellationToken.None);

        Assert.Equal(1, snapshot.DesiredManagedWorkerCount);
        Assert.Single(snapshot.ManagedWorkers);
        Assert.Equal(2, processFactory.Processes.Count(static process => process.KillCalls > 0));
    }

    [Fact]
    public async Task GetManagedProcesses_RemovesExitedProcesses()
    {
        var runtimeReader = new FakeRuntimeMetricsSnapshotReader();
        var processFactory = new FakeLocalWorkerProcessFactory();
        var manager = CreateManager(processFactory, runtimeReader);

        await manager.EnsureWorkerCountAsync(2, CancellationToken.None);
        processFactory.Processes[0].MarkExited();

        var managedWorkers = manager.GetManagedProcesses();

        Assert.Single(managedWorkers);
        Assert.Equal(2, managedWorkers[0].Slot);
    }

    [Fact]
    public async Task GetSnapshotAsync_ReportsDetectedWorkerInstances()
    {
        var runtimeReader = new FakeRuntimeMetricsSnapshotReader(
            new RuntimeMetricsSnapshot(
                "Worker",
                "worker-1",
                "test-machine",
                "Development",
                1001,
                DateTime.UtcNow,
                [],
                [],
                []),
            new RuntimeMetricsSnapshot(
                "Worker",
                "worker-2",
                "test-machine",
                "Development",
                1002,
                DateTime.UtcNow,
                [],
                [],
                []));
        var processFactory = new FakeLocalWorkerProcessFactory();
        var manager = CreateManager(processFactory, runtimeReader);

        var snapshot = await manager.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(2, snapshot.DetectedWorkerInstances);
    }

    [Fact]
    public async Task EnsureWorkerCountAsync_ThrowsWhenExecutablePathIsMissing()
    {
        var runtimeReader = new FakeRuntimeMetricsSnapshotReader();
        var processFactory = new FakeLocalWorkerProcessFactory();
        var manager = CreateManager(
            processFactory,
            runtimeReader,
            new LocalWorkerControlOptions
            {
                WorkerExecutablePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.exe"),
                StartupDelayMilliseconds = 0
            });

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() => manager.EnsureWorkerCountAsync(1, CancellationToken.None));

        Assert.Contains("Configured local worker executable was not found.", exception.Message);
    }

    private static LocalWorkerManager CreateManager(
        FakeLocalWorkerProcessFactory processFactory,
        FakeRuntimeMetricsSnapshotReader runtimeReader,
        LocalWorkerControlOptions? options = null)
    {
        return new LocalWorkerManager(
            processFactory,
            runtimeReader,
            new FakeHostEnvironment(),
            Microsoft.Extensions.Options.Options.Create(options ?? new LocalWorkerControlOptions
            {
                WorkerExecutablePath = typeof(LocalWorkerManagerTests).Assembly.Location,
                StartupDelayMilliseconds = 0
            }),
            NullLogger<LocalWorkerManager>.Instance);
    }

    private sealed class FakeRuntimeMetricsSnapshotReader : IRuntimeMetricsSnapshotReader
    {
        private readonly IReadOnlyList<RuntimeMetricsSnapshot> _snapshots;

        public FakeRuntimeMetricsSnapshotReader(params RuntimeMetricsSnapshot[] snapshots)
        {
            _snapshots = snapshots;
        }

        public ValueTask<IReadOnlyList<RuntimeMetricsSnapshot>> ListSnapshotsAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(_snapshots);
    }

    private sealed class FakeLocalWorkerProcessFactory : ILocalWorkerProcessFactory
    {
        private int _nextProcessId = 4000;

        public List<FakeLocalWorkerProcess> Processes { get; } = [];

        public ILocalWorkerProcess Start(LocalWorkerProcessStartInfo startInfo)
        {
            var process = new FakeLocalWorkerProcess(_nextProcessId++);
            Processes.Add(process);
            return process;
        }
    }

    private sealed class FakeLocalWorkerProcess : ILocalWorkerProcess
    {
        public FakeLocalWorkerProcess(int processId)
        {
            ProcessId = processId;
        }

        public int ProcessId { get; }

        public bool HasExited { get; private set; }

        public int KillCalls { get; private set; }

        public event Action? Exited;

        public int? TryGetExitCode() => HasExited ? 0 : null;

        public void Kill(bool entireProcessTree)
        {
            KillCalls++;
            MarkExited();
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }

        public void MarkExited()
        {
            HasExited = true;
            Exited?.Invoke();
        }
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "GameController.FBServiceExt.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

