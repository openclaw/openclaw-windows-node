using System.Collections.ObjectModel;
using System.Diagnostics;
using OpenClaw.Shared;

namespace OpenClaw.Connection;

public interface IGatewayTerminalLauncher
{
    void Open(GatewayHostAccessPlan accessPlan);

    void OpenGatewayDoctor(GatewayHostAccessPlan accessPlan);
}

public sealed record GatewayTerminalLaunchCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    bool UsesWindowsTerminal);

public static class GatewayTerminalLaunchCommandBuilder
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

    // Runs `openclaw doctor` in the app-managed WSL gateway, then `exec bash`
    // so the terminal stays interactive after doctor exits. doctor is a rich
    // terminal TUI and frequently exits non-zero for advisory findings, so we
    // neither capture its output nor gate the keep-open on its exit code — the
    // `|| true` absorbs the non-zero exit and `exec bash` always runs.
    //
    // IMPORTANT: the keep-open uses `&&` / `|| true &&`, NOT `;`. Windows Terminal
    // treats `;` in its command line as a tab/pane delimiter and splits on it even
    // inside a quoted argument, so `openclaw doctor; exec bash` breaks (wt tries to
    // launch " exec bash" → error 0x80070002). `&&` / `||` are not wt delimiters,
    // so the command passes through wt intact (verified with the app's
    // ProcessStartInfo.ArgumentList quoting), letting doctor open in a themed
    // Windows Terminal tab — matching the Connection page's Open terminal action.
    public static GatewayTerminalLaunchCommand BuildGatewayDoctor(GatewayHostAccessPlan accessPlan, string? windowsTerminalPath)
    {
        if (accessPlan.TerminalTarget != GatewayTerminalTarget.Wsl || string.IsNullOrWhiteSpace(accessPlan.DistroName))
        {
            throw new InvalidOperationException("Running gateway doctor requires an app-managed WSL gateway.");
        }

        var distroName = accessPlan.DistroName!;
        var script = $"{WslGatewayControlCommandBuilder.OpenClawWslPathPrefix} && openclaw doctor || true && exec bash";

        if (!string.IsNullOrWhiteSpace(windowsTerminalPath))
        {
            return new GatewayTerminalLaunchCommand(
                windowsTerminalPath,
                new ReadOnlyCollection<string>([
                    "new-tab",
                    "--title",
                    $"OpenClaw doctor ({distroName})",
                    "wsl.exe",
                    "-d",
                    distroName,
                    "--",
                    "bash",
                    "-lc",
                    script
                ]),
                true);
        }

        return new GatewayTerminalLaunchCommand(
            "wsl.exe",
            new ReadOnlyCollection<string>([
                "-d",
                distroName,
                "--",
                "bash",
                "-lc",
                script
            ]),
            false);
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

public sealed class GatewayTerminalLauncher(IOpenClawLogger logger) : IGatewayTerminalLauncher
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

    public void OpenGatewayDoctor(GatewayHostAccessPlan accessPlan)
    {
        // Mirror Open(): prefer a Windows Terminal tab, fall back to direct wsl.exe.
        var terminalPath = TryFindWindowsTerminalPath();
        var command = GatewayTerminalLaunchCommandBuilder.BuildGatewayDoctor(accessPlan, terminalPath);

        try
        {
            Start(command);
        }
        catch (Exception ex) when (command.UsesWindowsTerminal)
        {
            logger.Warn($"Windows Terminal launch failed; falling back to direct terminal process: {ex.Message}");
            Start(GatewayTerminalLaunchCommandBuilder.BuildGatewayDoctor(accessPlan, null));
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
            // Direct console apps (wsl.exe / ssh.exe — the doctor terminal and the
            // Open-terminal fallback) need ShellExecute so Windows allocates a
            // visible console window. Windows Terminal opens its own window, so it
            // keeps the lighter CreateProcess path.
            UseShellExecute = !command.UsesWindowsTerminal
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
