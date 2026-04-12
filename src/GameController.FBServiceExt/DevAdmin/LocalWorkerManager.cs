using System.Diagnostics;
using System.Text;
using GameController.FBServiceExt.Application.Abstractions.Observability;
using Microsoft.Extensions.Options;

namespace GameController.FBServiceExt.DevAdmin;

internal sealed class LocalWorkerManager : IAsyncDisposable
{
    private readonly ILocalWorkerProcessFactory _processFactory;
    private readonly IRuntimeMetricsSnapshotReader _runtimeMetricsSnapshotReader;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILogger<LocalWorkerManager> _logger;
    private readonly LocalWorkerControlOptions _options;
    private readonly object _gate = new();
    private readonly List<ManagedLocalWorkerProcess> _managedProcesses = new();

    public LocalWorkerManager(
        ILocalWorkerProcessFactory processFactory,
        IRuntimeMetricsSnapshotReader runtimeMetricsSnapshotReader,
        IHostEnvironment hostEnvironment,
        IOptions<LocalWorkerControlOptions> options,
        ILogger<LocalWorkerManager> logger)
    {
        _processFactory = processFactory;
        _runtimeMetricsSnapshotReader = runtimeMetricsSnapshotReader;
        _hostEnvironment = hostEnvironment;
        _logger = logger;
        _options = options.Value;
    }

    public IReadOnlyList<ManagedLocalWorkerProcessSnapshot> GetManagedProcesses()
    {
        lock (_gate)
        {
            RemoveExitedProcessesUnderLock();
            return _managedProcesses
                .Select(static process => process.ToSnapshot())
                .OrderBy(static process => process.Slot)
                .ToArray();
        }
    }

    public async Task<LocalWorkerControlSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var managedWorkers = GetManagedProcesses();
        var runtimeSnapshots = await _runtimeMetricsSnapshotReader.ListSnapshotsAsync(cancellationToken).ConfigureAwait(false);
        var detectedWorkerInstances = runtimeSnapshots.Count(static snapshot => string.Equals(snapshot.ServiceRole, "Worker", StringComparison.OrdinalIgnoreCase));

        return new LocalWorkerControlSnapshot(
            DesiredManagedWorkerCount: managedWorkers.Count,
            DetectedWorkerInstances: detectedWorkerInstances,
            ManagedWorkers: managedWorkers);
    }

    public async Task<LocalWorkerControlSnapshot> EnsureWorkerCountAsync(int desiredCount, CancellationToken cancellationToken)
    {
        desiredCount = Math.Max(0, desiredCount);

        List<ManagedLocalWorkerProcess> toStop;
        List<int> slotsToStart;

        lock (_gate)
        {
            RemoveExitedProcessesUnderLock();
            toStop = new List<ManagedLocalWorkerProcess>();

            while (_managedProcesses.Count > desiredCount)
            {
                var lastIndex = _managedProcesses.Count - 1;
                toStop.Add(_managedProcesses[lastIndex]);
                _managedProcesses.RemoveAt(lastIndex);
            }

            slotsToStart = Enumerable.Range(1, desiredCount)
                .Except(_managedProcesses.Select(static process => process.Slot))
                .ToList();
        }

        foreach (var process in toStop)
        {
            await StopProcessAsync(process, cancellationToken).ConfigureAwait(false);
        }

        foreach (var slot in slotsToStart)
        {
            var process = await StartProcessAsync(slot, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _managedProcesses.Add(process);
                _managedProcesses.Sort(static (left, right) => left.Slot.CompareTo(right.Slot));
            }
        }

        return await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        List<ManagedLocalWorkerProcess> toStop;
        lock (_gate)
        {
            toStop = _managedProcesses.ToList();
            _managedProcesses.Clear();
        }

        foreach (var process in toStop)
        {
            await StopProcessAsync(process, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync().ConfigureAwait(false);
    }

    private async Task<ManagedLocalWorkerProcess> StartProcessAsync(int slot, CancellationToken cancellationToken)
    {
        var executablePath = ResolveWorkerExecutablePath();
        var workingDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException("Worker executable directory could not be resolved.");
        var environmentName = string.IsNullOrWhiteSpace(_options.WorkerEnvironmentName)
            ? _hostEnvironment.EnvironmentName
            : _options.WorkerEnvironmentName.Trim();

        var process = _processFactory.Start(new LocalWorkerProcessStartInfo(
            executablePath,
            workingDirectory,
            environmentName,
            slot));

        process.Exited += () =>
        {
            _logger.LogInformation(
                "Local managed worker slot {Slot} exited. PID={ProcessId}, ExitCode={ExitCode}",
                slot,
                process.ProcessId,
                process.TryGetExitCode());
        };

        var startedAtUtc = DateTimeOffset.UtcNow;
        await Task.Delay(Math.Max(0, _options.StartupDelayMilliseconds), cancellationToken).ConfigureAwait(false);
        if (process.HasExited)
        {
            process.Dispose();
            throw new InvalidOperationException($"Local managed worker slot {slot} exited immediately with code {process.TryGetExitCode()}.");
        }

        _logger.LogInformation(
            "Local managed worker slot {Slot} started. PID={ProcessId}, Environment={EnvironmentName}",
            slot,
            process.ProcessId,
            environmentName);

        return new ManagedLocalWorkerProcess(slot, process, startedAtUtc, executablePath, environmentName);
    }

    private async Task StopProcessAsync(ManagedLocalWorkerProcess process, CancellationToken cancellationToken)
    {
        try
        {
            if (process.Process.HasExited)
            {
                return;
            }

            process.Process.Kill(entireProcessTree: true);
            await process.Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Local managed worker slot {Slot} stopped. PID={ProcessId}",
                process.Slot,
                process.Process.ProcessId);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Local managed worker slot {Slot} stop warning. PID={ProcessId}",
                process.Slot,
                process.Process.ProcessId);
        }
        finally
        {
            process.Process.Dispose();
        }
    }

    private string ResolveWorkerExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(_options.WorkerExecutablePath))
        {
            if (!File.Exists(_options.WorkerExecutablePath))
            {
                throw new FileNotFoundException("Configured local worker executable was not found.", _options.WorkerExecutablePath);
            }

            return _options.WorkerExecutablePath;
        }

        var runtimeDirectory = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var configurationName = runtimeDirectory.Parent?.Name;
        var srcDirectory = FindAncestor(runtimeDirectory, "src");

        if (srcDirectory is null || string.IsNullOrWhiteSpace(configurationName))
        {
            throw new InvalidOperationException("Local worker executable path could not be derived. Configure LocalWorkerControl:WorkerExecutablePath.");
        }

        var candidates = new[]
        {
            Path.Combine(srcDirectory.FullName, "GameController.FBServiceExt.Worker", "bin", configurationName, "net8.0", "GameController.FBServiceExt.Worker.exe"),
            Path.Combine(srcDirectory.FullName, "GameController.FBServiceExt.Worker", "bin", "Debug", "net8.0", "GameController.FBServiceExt.Worker.exe"),
            Path.Combine(srcDirectory.FullName, "GameController.FBServiceExt.Worker", "bin", "Release", "net8.0", "GameController.FBServiceExt.Worker.exe")
        };

        var resolvedPath = candidates.FirstOrDefault(File.Exists);
        if (resolvedPath is null)
        {
            throw new FileNotFoundException(
                $"Derived local worker executable was not found. Looked in:{Environment.NewLine}{string.Join(Environment.NewLine, candidates)}",
                candidates[0]);
        }

        return resolvedPath;
    }

    private void RemoveExitedProcessesUnderLock()
    {
        for (var index = _managedProcesses.Count - 1; index >= 0; index--)
        {
            if (_managedProcesses[index].Process.HasExited)
            {
                _managedProcesses[index].Process.Dispose();
                _managedProcesses.RemoveAt(index);
            }
        }
    }

    private static DirectoryInfo? FindAncestor(DirectoryInfo? start, string directoryName)
    {
        var current = start;
        while (current is not null)
        {
            if (string.Equals(current.Name, directoryName, StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }

            current = current.Parent;
        }

        return null;
    }

    private sealed record ManagedLocalWorkerProcess(
        int Slot,
        ILocalWorkerProcess Process,
        DateTimeOffset StartedAtUtc,
        string ExecutablePath,
        string EnvironmentName)
    {
        public ManagedLocalWorkerProcessSnapshot ToSnapshot()
            => new(
                Slot,
                Process.ProcessId,
                StartedAtUtc,
                ExecutablePath,
                EnvironmentName,
                !Process.HasExited);
    }
}

internal sealed class LocalWorkerControlOptions
{
    public const string SectionName = "LocalWorkerControl";

    public string WorkerExecutablePath { get; init; } = string.Empty;

    public string WorkerEnvironmentName { get; init; } = string.Empty;

    public int StartupDelayMilliseconds { get; init; } = 1500;
}

internal sealed record WorkerCountUpdateRequest(int ManagedWorkerCount);

internal sealed record LocalWorkerControlSnapshot(
    int DesiredManagedWorkerCount,
    int DetectedWorkerInstances,
    IReadOnlyList<ManagedLocalWorkerProcessSnapshot> ManagedWorkers);

internal sealed record ManagedLocalWorkerProcessSnapshot(
    int Slot,
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    string ExecutablePath,
    string EnvironmentName,
    bool IsRunning);

internal sealed record LocalWorkerProcessStartInfo(
    string ExecutablePath,
    string WorkingDirectory,
    string EnvironmentName,
    int Slot);

internal interface ILocalWorkerProcessFactory
{
    ILocalWorkerProcess Start(LocalWorkerProcessStartInfo startInfo);
}

internal interface ILocalWorkerProcess : IDisposable
{
    int ProcessId { get; }

    bool HasExited { get; }

    event Action? Exited;

    int? TryGetExitCode();

    void Kill(bool entireProcessTree);

    Task WaitForExitAsync(CancellationToken cancellationToken);
}

internal sealed class SystemLocalWorkerProcessFactory : ILocalWorkerProcessFactory
{
    private readonly ILogger<SystemLocalWorkerProcessFactory> _logger;

    public SystemLocalWorkerProcessFactory(ILogger<SystemLocalWorkerProcessFactory> logger)
    {
        _logger = logger;
    }

    public ILocalWorkerProcess Start(LocalWorkerProcessStartInfo startInfo)
    {
        var processStartInfo = new ProcessStartInfo(startInfo.ExecutablePath)
        {
            WorkingDirectory = startInfo.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        processStartInfo.Environment["DOTNET_ENVIRONMENT"] = startInfo.EnvironmentName;
        processStartInfo.Environment["ASPNETCORE_ENVIRONMENT"] = startInfo.EnvironmentName;
        processStartInfo.Environment["FB_DEV_MANAGED_WORKER_SLOT"] = startInfo.Slot.ToString();

        var process = Process.Start(processStartInfo)
            ?? throw new InvalidOperationException("Local managed worker process could not be started.");

        process.EnableRaisingEvents = true;
        process.OutputDataReceived += (_, args) => LogProcessLine(startInfo.Slot, "stdout", args.Data);
        process.ErrorDataReceived += (_, args) => LogProcessLine(startInfo.Slot, "stderr", args.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return new SystemLocalWorkerProcess(process);
    }

    private void LogProcessLine(int slot, string streamName, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        _logger.LogInformation("Local managed worker slot {Slot} {Stream}: {Line}", slot, streamName, line.Trim());
    }
}

internal sealed class SystemLocalWorkerProcess : ILocalWorkerProcess
{
    private readonly Process _process;

    public SystemLocalWorkerProcess(Process process)
    {
        _process = process;
        _process.Exited += (_, _) => Exited?.Invoke();
    }

    public int ProcessId => _process.Id;

    public bool HasExited => _process.HasExited;

    public event Action? Exited;

    public int? TryGetExitCode()
    {
        try
        {
            return _process.ExitCode;
        }
        catch
        {
            return null;
        }
    }

    public void Kill(bool entireProcessTree)
    {
        _process.Kill(entireProcessTree);
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken)
        => _process.WaitForExitAsync(cancellationToken);

    public void Dispose()
    {
        _process.Dispose();
    }
}
