using System.Diagnostics;
using System.Text;

namespace GameController.FBServiceExt.FakeFBForSimulate;

internal sealed class ManagedWorkerProcessManager : IAsyncDisposable
{
    private readonly SimulatorDefaults _defaults;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly List<ManagedWorkerProcess> _managedProcesses = new();

    public ManagedWorkerProcessManager(SimulatorDefaults defaults, Action<string> log)
    {
        _defaults = defaults;
        _log = log;
    }

    public IReadOnlyList<ManagedWorkerProcessSnapshot> GetManagedProcesses()
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

    public async Task EnsureWorkerCountAsync(int desiredCount, CancellationToken cancellationToken = default)
    {
        desiredCount = Math.Max(0, desiredCount);
        List<ManagedWorkerProcess> toStop;
        List<int> slotsToStart;

        lock (_gate)
        {
            RemoveExitedProcessesUnderLock();
            toStop = new List<ManagedWorkerProcess>();
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
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        List<ManagedWorkerProcess> toStop;
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

    internal static string ResolveWorkerExecutablePath(SimulatorDefaults defaults)
    {
        if (!string.IsNullOrWhiteSpace(defaults.ManagedWorkerExecutablePath))
        {
            if (!File.Exists(defaults.ManagedWorkerExecutablePath))
            {
                throw new FileNotFoundException("Configured managed worker executable was not found.", defaults.ManagedWorkerExecutablePath);
            }

            return defaults.ManagedWorkerExecutablePath;
        }

        var runtimeDirectory = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var configurationName = runtimeDirectory.Parent?.Name;
        var srcDirectory = FindAncestor(runtimeDirectory, "src");

        if (srcDirectory is null || string.IsNullOrWhiteSpace(configurationName))
        {
            throw new InvalidOperationException("Managed worker executable path could not be derived. Configure Simulator:ManagedWorkerExecutablePath.");
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
                $"Derived managed worker executable was not found. Looked in:{Environment.NewLine}{string.Join(Environment.NewLine, candidates)}",
                candidates[0]);
        }

        return resolvedPath;
    }

    private async Task<ManagedWorkerProcess> StartProcessAsync(int slot, CancellationToken cancellationToken)
    {
        var executablePath = ResolveWorkerExecutablePath(_defaults);
        var workingDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException("Worker executable directory could not be resolved.");
        var environmentName = SimulatorManagedWorkerContract.ResolveEffectiveEnvironmentName(_defaults.ManagedWorkerEnvironmentName);

        var startInfo = new ProcessStartInfo(executablePath)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.Environment["DOTNET_ENVIRONMENT"] = environmentName;
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = environmentName;
        startInfo.Environment["FB_SIM_MANAGED_WORKER_SLOT"] = slot.ToString();

        _log($"Managed worker slot {slot} starting. Environment={environmentName}, Executable={executablePath}");

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Managed worker process could not be started.");
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => _log($"Managed worker slot {slot} exited. PID={process.Id}, ExitCode={TryGetExitCode(process)}");

        RegisterProcessLogging(slot, process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
        if (process.HasExited)
        {
            throw new InvalidOperationException($"Managed worker slot {slot} exited immediately with code {TryGetExitCode(process)}.");
        }

        _log($"Managed worker slot {slot} started. PID={process.Id}, Environment={environmentName}");
        return new ManagedWorkerProcess(slot, process, DateTimeOffset.UtcNow, executablePath, environmentName);
    }

    private void RegisterProcessLogging(int slot, Process process)
    {
        process.OutputDataReceived += (_, args) => LogProcessLine(slot, "stdout", args.Data);
        process.ErrorDataReceived += (_, args) => LogProcessLine(slot, "stderr", args.Data);
    }

    private void LogProcessLine(int slot, string streamName, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        _log($"Managed worker slot {slot} {streamName}: {line.Trim()}");
    }

    private async Task StopProcessAsync(ManagedWorkerProcess process, CancellationToken cancellationToken)
    {
        try
        {
            if (process.Process.HasExited)
            {
                return;
            }

            process.Process.Kill(entireProcessTree: true);
            await process.Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            _log($"Managed worker slot {process.Slot} stopped. PID={process.Process.Id}");
        }
        catch (Exception exception)
        {
            _log($"Managed worker slot {process.Slot} stop warning: {exception.Message}");
        }
        finally
        {
            process.Process.Dispose();
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

    private static int TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return int.MinValue;
        }
    }

    private sealed record ManagedWorkerProcess(int Slot, Process Process, DateTimeOffset StartedAtUtc, string ExecutablePath, string EnvironmentName)
    {
        public ManagedWorkerProcessSnapshot ToSnapshot()
            => new(Slot, Process.Id, StartedAtUtc, ExecutablePath, !Process.HasExited, EnvironmentName);
    }
}

internal sealed record ManagedWorkerProcessSnapshot(
    int Slot,
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    string ExecutablePath,
    bool IsRunning,
    string EnvironmentName);
