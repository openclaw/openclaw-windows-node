using System.Diagnostics;

namespace OpenClaw.Shared;

public static class WindowsStartupTaskRegistration
{
    public const string TaskName = "OpenClaw Companion";

    public static bool Register(string trayExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(trayExecutablePath) || !File.Exists(trayExecutablePath))
            return false;

        return Run(CreateRegisterProcessStartInfo(trayExecutablePath));
    }

    public static bool Unregister() => Run(CreateUnregisterProcessStartInfo());

    public static bool Exists() => Run(CreateQueryProcessStartInfo());

    internal static ProcessStartInfo CreateRegisterProcessStartInfo(string trayExecutablePath)
    {
        var fullPath = Path.GetFullPath(trayExecutablePath);
        return CreateStartInfo(
            "/Create",
            "/TN", TaskName,
            "/TR", Quote(fullPath),
            "/SC", "ONLOGON",
            "/F");
    }

    internal static ProcessStartInfo CreateUnregisterProcessStartInfo() =>
        CreateStartInfo(
            "/Delete",
            "/TN", TaskName,
            "/F");

    internal static ProcessStartInfo CreateQueryProcessStartInfo() =>
        CreateStartInfo(
            "/Query",
            "/TN", TaskName);

    private static bool Run(ProcessStartInfo startInfo)
    {
        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
                return false;

            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0;
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
