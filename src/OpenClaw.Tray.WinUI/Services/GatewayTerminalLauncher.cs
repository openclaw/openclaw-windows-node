using System.Collections.ObjectModel;
using System.Diagnostics;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal interface IGatewayTerminalLauncher
{
    void Open(GatewayHostAccessPlan accessPlan);
}

internal sealed record GatewayTerminalLaunchCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    bool UsesWindowsTerminal);

internal static class GatewayTerminalLaunchCommandBuilder
{
    public static GatewayTerminalLaunchCommand Build(GatewayHostAccessPlan accessPlan, string? windowsTerminalPath)
    {
        if (!accessPlan.CanOpenTerminal)
        {
            throw new InvalidOperationException(accessPlan.DisabledReason ?? "Gateway terminal access is not available.");
        }

        return accessPlan.TerminalTarget switch
        {
            GatewayTerminalTarget.Wsl => BuildWslCommand(accessPlan, windowsTerminalPath),
            GatewayTerminalTarget.Ssh => BuildSshCommand(accessPlan, windowsTerminalPath),
            _ => throw new InvalidOperationException("Gateway terminal access is not available.")
        };
    }

    private static GatewayTerminalLaunchCommand BuildWslCommand(GatewayHostAccessPlan accessPlan, string? windowsTerminalPath)
    {
        var distroName = RequireValue(accessPlan.DistroName, "WSL distro name is required.");
        if (!string.IsNullOrWhiteSpace(windowsTerminalPath))
        {
            return new GatewayTerminalLaunchCommand(
                windowsTerminalPath,
                new ReadOnlyCollection<string>([
                    "new-tab",
                    "--title",
                    $"OpenClaw Gateway ({distroName})",
                    "wsl.exe",
                    "-d",
                    distroName
                ]),
                true);
        }

        return new GatewayTerminalLaunchCommand(
            "wsl.exe",
            new ReadOnlyCollection<string>(["-d", distroName]),
            false);
    }

    private static GatewayTerminalLaunchCommand BuildSshCommand(GatewayHostAccessPlan accessPlan, string? windowsTerminalPath)
    {
        var user = RequireValue(accessPlan.SshUser, "SSH user is required.");
        var host = RequireValue(accessPlan.SshHost, "SSH host is required.");
        var endpoint = $"{user}@{host}";

        if (!string.IsNullOrWhiteSpace(windowsTerminalPath))
        {
            return new GatewayTerminalLaunchCommand(
                windowsTerminalPath,
                new ReadOnlyCollection<string>([
                    "new-tab",
                    "--title",
                    $"OpenClaw SSH Gateway ({host})",
                    "ssh.exe",
                    endpoint
                ]),
                true);
        }

        return new GatewayTerminalLaunchCommand(
            "ssh.exe",
            new ReadOnlyCollection<string>([endpoint]),
            false);
    }

    private static string RequireValue(string? value, string message)
    {
        return string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(message) : value;
    }
}

internal sealed class GatewayTerminalLauncher(IOpenClawLogger logger) : IGatewayTerminalLauncher
{
    public void Open(GatewayHostAccessPlan accessPlan)
    {
        var terminalPath = TryFindWindowsTerminalPath();
        var command = GatewayTerminalLaunchCommandBuilder.Build(accessPlan, terminalPath);

        try
        {
            Start(command);
        }
        catch (Exception ex) when (command.UsesWindowsTerminal)
        {
            logger.Warn($"Windows Terminal launch failed; falling back to direct terminal process: {ex.Message}");
            Start(GatewayTerminalLaunchCommandBuilder.Build(accessPlan, null));
        }
    }

    internal static string? TryFindWindowsTerminalPath()
    {
        foreach (var candidate in GetWindowsTerminalCandidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetWindowsTerminalCandidates()
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Microsoft", "WindowsApps", "wt.exe");
        }

        var specialLocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(specialLocalAppData))
        {
            yield return Path.Combine(specialLocalAppData, "Microsoft", "WindowsApps", "wt.exe");
        }
    }

    private static void Start(GatewayTerminalLaunchCommand command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            UseShellExecute = false
        };

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException($"Unable to launch {command.FileName}.");
        }
    }
}
