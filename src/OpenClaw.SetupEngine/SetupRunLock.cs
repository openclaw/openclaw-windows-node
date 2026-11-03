namespace OpenClaw.SetupEngine;

/// <summary>
/// Admission for one setup or uninstall run. Holds the per-profile run lock
/// (<c>dataDir\setup.lock</c>) and the shared-target lock (<c>localDataDir\setup-target.lock</c>).
/// Profiles with different OPENCLAW_TRAY_DATA_DIR values share the app identity's WSL distro,
/// Local AI files, and local setup directory, so the target lock serializes setup, uninstall,
/// and setup windows across profiles. A different local data directory with the same distro
/// name is not serialized here; <see cref="CleanupStaleDistroStep"/>'s path-bound ownership
/// check refuses that distro instead. Callers re-check replacement admission after acquiring it.
/// </summary>
public sealed class SetupRunLock : IDisposable
{
    private const string RunLockFileName = "setup.lock";
    private const string TargetLockFileName = "setup-target.lock";

    private readonly FileStream _runStream;
    private readonly string _runPath;
    private readonly FileStream _targetStream;
    private readonly string _targetPath;

    private SetupRunLock(FileStream runStream, string runPath, FileStream targetStream, string targetPath)
    {
        _runStream = runStream;
        _runPath = runPath;
        _targetStream = targetStream;
        _targetPath = targetPath;
    }

    public static bool TryAcquire(string dataDir, string localDataDir, out SetupRunLock? runLock, out string? message)
    {
        runLock = null;
        var runPath = Path.Combine(dataDir, RunLockFileName);
        if (!TryOpen(
                dataDir,
                runPath,
                $"Another OpenClaw setup run appears to be active. Wait for it to finish, then retry. Lock file: {runPath}",
                out var runStream,
                out message))
        {
            return false;
        }

        var targetPath = Path.Combine(localDataDir, TargetLockFileName);
        FileStream? targetStream;
        try
        {
            if (!TryOpen(
                    localDataDir,
                    targetPath,
                    $"Another OpenClaw setup run is using the shared setup data in {localDataDir}. Wait for it to finish, then retry. Lock file: {targetPath}",
                    out targetStream,
                    out message))
            {
                Release(runStream!, runPath);
                return false;
            }
        }
        catch
        {
            // The run lock must not outlive a failed admission.
            Release(runStream!, runPath);
            throw;
        }

        runLock = new SetupRunLock(runStream!, runPath, targetStream!, targetPath);
        return true;
    }

    private static bool TryOpen(
        string directory,
        string path,
        string busyMessage,
        out FileStream? stream,
        out string? message)
    {
        Directory.CreateDirectory(directory);
        try
        {
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            using var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true };
            writer.WriteLine($"pid={Environment.ProcessId}");
            writer.WriteLine($"startedUtc={DateTimeOffset.UtcNow:O}");
            stream.Flush(flushToDisk: true);
            message = null;
            return true;
        }
        catch (IOException)
        {
            stream = null;
            message = busyMessage;
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            stream = null;
            message = $"Cannot create setup lock at {path}: {ex.Message}";
            return false;
        }
    }

    public void Dispose()
    {
        Release(_targetStream, _targetPath);
        Release(_runStream, _runPath);
    }

    private static void Release(FileStream stream, string path)
    {
        stream.Dispose();
        try { File.Delete(path); }
        catch (Exception ex)
        {
            // Best-effort cleanup of the run-lock file. If the delete fails, the
            // next setup attempt will see an orphan lock and surface a confusing
            // "another setup active" error — surface the cause via the diagnostic
            // stderr channel so it is visible in test/CI logs.
            SetupDiagnostics.TryWriteStderrWarning($"SetupRunLock.Dispose: failed to delete lock file '{path}': {ex.GetType().Name}: {ex.Message}");
        }
    }
}
