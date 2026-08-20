using System.Diagnostics;

namespace OpenClaw.MSIXHost;

internal static class InstallDirectoryLock
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static string GetPath(string installDirectory)
    {
        string? installRoot = Path.GetDirectoryName(installDirectory);
        if (string.IsNullOrWhiteSpace(installRoot))
        {
            throw new InvalidOperationException(
                "The install directory must have a parent directory.");
        }

        return Path.Combine(
            installRoot,
            $".{Path.GetFileName(installDirectory)}.install.lock");
    }

    public static async Task<FileStream> AcquireAsync(
        string installDirectory,
        CancellationToken cancellationToken)
    {
        string lockPath = GetPath(installDirectory);
        var stopwatch = Stopwatch.StartNew();
        IOException? lastException = null;
        while (stopwatch.Elapsed < Timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException exception)
            {
                lastException = exception;
                await Task.Delay(250, cancellationToken);
            }
        }

        throw new TimeoutException(
            $"Timed out waiting for the installation lock: {lockPath}",
            lastException);
    }
}
