namespace OpenClaw.SetupEngine;

public sealed class SetupRunLock : IDisposable
{
    private readonly FileStream _stream;
    private readonly string _path;

    private SetupRunLock(FileStream stream, string path)
    {
        _stream = stream;
        _path = path;
    }

    public static bool TryAcquire(string dataDir, out SetupRunLock? runLock, out string? message)
    {
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, "setup.lock");

        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            using var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true };
            writer.WriteLine($"pid={Environment.ProcessId}");
            writer.WriteLine($"startedUtc={DateTimeOffset.UtcNow:O}");
            stream.Flush(flushToDisk: true);

            runLock = new SetupRunLock(stream, path);
            message = null;
            return true;
        }
        catch (IOException)
        {
            runLock = null;
            message = $"Another OpenClaw setup run appears to be active. If setup is not running, delete the stale lock file and retry: {path}";
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            runLock = null;
            message = $"Cannot create setup lock at {path}: {ex.Message}";
            return false;
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        try { File.Delete(_path); } catch { }
    }
}
