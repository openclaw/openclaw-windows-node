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
            message = $"Another OpenClaw setup run appears to be active. Wait for it to finish, then retry. Lock file: {path}";
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
        // slopwatch-ignore: SW003 Cleanup is best-effort; failure cannot improve caller state and the original outcome is preserved.
        try { File.Delete(_path); } catch { }
    }
}
