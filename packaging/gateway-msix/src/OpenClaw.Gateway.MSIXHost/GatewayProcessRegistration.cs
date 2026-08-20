using System.Diagnostics;

namespace OpenClaw.MSIXHost;

internal sealed class GatewayProcessRegistration : IDisposable
{
    private const string FileName = "gateway-process.txt";
    private readonly string _path;
    private readonly int _processId;
    private readonly long _startTimeUtcTicks;

    private GatewayProcessRegistration(
        string path,
        int processId,
        long startTimeUtcTicks)
    {
        _path = path;
        _processId = processId;
        _startTimeUtcTicks = startTimeUtcTicks;
    }

    public static GatewayProcessRegistration Create(
        Process process,
        string installDirectory)
    {
        string path = GetPath(installDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        long startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
        string processPath = process.MainModule?.FileName ??
            throw new InvalidOperationException(
                "Unable to resolve the OpenClaw gateway process executable.");
        string temporaryPath = path + ".tmp";
        File.WriteAllLines(
            temporaryPath,
            [
                process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                startTimeUtcTicks.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                Path.GetFullPath(processPath)
            ]);
        File.Move(temporaryPath, path, overwrite: true);
        return new GatewayProcessRegistration(path, process.Id, startTimeUtcTicks);
    }

    public static async Task<bool> StopRegisteredGatewayAsync(
        string installDirectory,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        string path = GetPath(installDirectory);
        if (!File.Exists(path))
        {
            return false;
        }

        string[] lines = await File.ReadAllLinesAsync(path, cancellationToken);
        if (lines.Length != 3 ||
            !int.TryParse(lines[0], out int processId) ||
            !long.TryParse(lines[1], out long startTimeUtcTicks))
        {
            log("The saved gateway process record was invalid and was removed.");
            File.Delete(path);
            return false;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            File.Delete(path);
            return false;
        }

        using (process)
        {
            bool matches;
            try
            {
                string? actualProcessPath = process.MainModule?.FileName;
                matches =
                    process.StartTime.ToUniversalTime().Ticks == startTimeUtcTicks &&
                    actualProcessPath is not null &&
                    string.Equals(
                        Path.GetFullPath(actualProcessPath),
                        Path.GetFullPath(lines[2]),
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        Path.GetFileName(actualProcessPath),
                        "node.exe",
                        StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                System.ComponentModel.Win32Exception)
            {
                log(
                    $"The recorded gateway process could not be inspected: " +
                    $"{exception.Message}");
                File.Delete(path);
                return false;
            }

            if (!matches)
            {
                log(
                    "The saved gateway PID no longer identifies the packaged Node.js " +
                    "process; it will not be terminated.");
                File.Delete(path);
                return false;
            }

            log($"Stopping the recorded OpenClaw gateway process (PID {processId}).");
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken);
        }

        File.Delete(path);
        return true;
    }

    public void Dispose()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        string[] lines = File.ReadAllLines(_path);
        if (lines.Length >= 2 &&
            int.TryParse(lines[0], out int processId) &&
            long.TryParse(lines[1], out long startTimeUtcTicks) &&
            processId == _processId &&
            startTimeUtcTicks == _startTimeUtcTicks)
        {
            File.Delete(_path);
        }
    }

    private static string GetPath(string installDirectory)
    {
        string? root = Path.GetDirectoryName(installDirectory);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException(
                "The install directory must have a parent directory.");
        }

        return Path.Combine(root, FileName);
    }
}
