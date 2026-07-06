using System.Diagnostics;

namespace OpenClaw.Shared;

public static class WindowsStartupTaskRegistration
{
    public const string TaskName = "OpenClaw Companion";

    public static bool Register(string trayExecutablePath, string taskName = TaskName)
    {
        if (string.IsNullOrWhiteSpace(trayExecutablePath) || !File.Exists(trayExecutablePath))
            return false;

        return Run(CreateRegisterProcessStartInfo(trayExecutablePath, taskName));
    }

    public static bool Unregister(string taskName = TaskName) => Run(CreateUnregisterProcessStartInfo(taskName));

    public static bool Exists(string taskName = TaskName) => Run(CreateQueryProcessStartInfo(taskName));

    internal static ProcessStartInfo CreateRegisterProcessStartInfo(string trayExecutablePath, string taskName = TaskName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        var fullPath = Path.GetFullPath(trayExecutablePath);
        return CreateStartInfo(
            "/Create",
            "/TN", taskName,
            "/TR", Quote(fullPath),
            "/SC", "ONLOGON",
            "/F");
    }

    internal static ProcessStartInfo CreateUnregisterProcessStartInfo(string taskName = TaskName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        return CreateStartInfo(
            "/Delete",
            "/TN", taskName,
            "/F");
    }

    internal static ProcessStartInfo CreateQueryProcessStartInfo(string taskName = TaskName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        return CreateStartInfo(
            "/Query",
            "/TN", taskName);
    }

    private static bool Run(ProcessStartInfo startInfo)
    {
        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
                return false;

            if (process.WaitForExit(10_000))
                return process.ExitCode == 0;

            try
            {
                process.Kill(entireProcessTree: false);
            }
            catch
            {
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    internal static string ResolveSchtasksPath()
    {
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(systemRoot))
            systemRoot = Environment.GetEnvironmentVariable("SystemRoot");

        return !string.IsNullOrWhiteSpace(systemRoot)
            ? Path.Combine(systemRoot, "System32", "schtasks.exe")
            : Path.Combine("C:\\", "Windows", "System32", "schtasks.exe");
    }

    private static ProcessStartInfo CreateStartInfo(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveSchtasksPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        return startInfo;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
