using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;

namespace OpenClawTray.Services.LocalGatewaySetup;

/// <summary>
/// High-level WSL platform availability state. Distinct from "a given
/// distro is installed" — this only describes whether the WSL platform
/// itself (wsl.exe + the kernel + lifted/Store WSL) is present and usable
/// on this host.
/// </summary>
public enum WslPlatformState
{
    Installed,
    NotInstalled,

    /// <summary>
    /// wsl.exe is reachable but did not return a recognizable success or
    /// not-installed signal — e.g. blocked by policy, transient service
    /// error, or unexpected stderr. Deliberately distinct from
    /// <see cref="NotInstalled"/> so we do not auto-trigger an install on
    /// top of a working-but-degraded WSL.
    /// </summary>
    Unknown,
}

public sealed record WslPlatformProbeResult(
    WslPlatformState State,
    WslCommandResult? StatusResult = null,
    string? Detail = null);

public interface IWslPlatformProbe
{
    Task<WslPlatformProbeResult> ProbeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Fast detection probe for the WSL platform. Combines a cheap on-disk
/// check for <c>wsl.exe</c> with a single <c>wsl --status</c> invocation
/// whose output is pattern-matched against the well-known "WSL is not
/// installed" banner. The aka.ms/wslinstall URL is locale-stable, so we
/// match on it first and fall back to English text only for older
/// wsl.exe builds.
///
/// Lives outside the inline preflight so the call site can short-circuit
/// the more expensive <c>wsl --list --verbose</c> probe and avoid eating
/// a second 30s timeout when WSL is missing entirely.
/// </summary>
public sealed class WslPlatformProbe : IWslPlatformProbe
{
    private readonly IWslCommandRunner _wsl;
    private readonly Func<string, bool> _fileExists;
    private readonly string? _wslExePath;

    public WslPlatformProbe(
        IWslCommandRunner wsl,
        Func<string, bool>? fileExists = null,
        string? wslExePath = null)
    {
        _wsl = wsl;
        _fileExists = fileExists ?? File.Exists;
        _wslExePath = wslExePath ?? ResolveDefaultWslExePath();
    }

    internal static string? ResolveDefaultWslExePath()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (string.IsNullOrEmpty(systemRoot))
            return null;
        return Path.Combine(systemRoot, "System32", "wsl.exe");
    }

    public async Task<WslPlatformProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (_wslExePath is not null && !_fileExists(_wslExePath))
        {
            return new WslPlatformProbeResult(
                WslPlatformState.NotInstalled,
                Detail: $"wsl.exe not found at {_wslExePath}");
        }

        var status = await _wsl.RunAsync(["--status"], cancellationToken);
        var combined = (status.StandardOutput ?? string.Empty) + "\n" + (status.StandardError ?? string.Empty);

        if (LooksLikeNotInstalled(combined))
        {
            return new WslPlatformProbeResult(
                WslPlatformState.NotInstalled,
                StatusResult: status,
                Detail: "wsl.exe reported that the Windows Subsystem for Linux is not installed.");
        }

        if (status.Success)
            return new WslPlatformProbeResult(WslPlatformState.Installed, StatusResult: status);

        return new WslPlatformProbeResult(
            WslPlatformState.Unknown,
            StatusResult: status,
            Detail: "wsl --status failed without a recognized not-installed signal.");
    }

    /// <summary>
    /// Locale-tolerant pattern match for wsl.exe's "WSL is not installed"
    /// banner. We anchor on the aka.ms/wslinstall URL (present in every
    /// localized variant we have observed) and only fall back to English
    /// text for older wsl.exe versions that omit the URL.
    /// </summary>
    public static bool LooksLikeNotInstalled(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return false;
        var clean = output.Replace("\0", string.Empty);
        if (clean.Contains("aka.ms/wslinstall", StringComparison.OrdinalIgnoreCase))
            return true;
        if (clean.Contains("Windows Subsystem for Linux is not installed", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }
}

public enum WslPlatformInstallOutcome
{
    InstalledNoRestart,
    InstalledRequiresRestart,
    UserDeclinedElevation,
    Failed,
}

public sealed record WslPlatformInstallResult(
    WslPlatformInstallOutcome Outcome,
    int ExitCode,
    string? ErrorMessage = null,
    string? Detail = null);

public interface IWslPlatformInstaller
{
    Task<WslPlatformInstallResult> InstallAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs <c>wsl.exe --install --no-distribution</c> elevated via ShellExecute
/// (UAC prompt). Uses <c>--no-distribution</c> so the bootstrap install
/// touches only the WSL platform; the OpenClawGateway distro is then laid
/// down through the normal <see cref="WslStoreInstanceInstaller"/> path.
///
/// After the elevated process exits, we re-probe with
/// <see cref="IWslPlatformProbe"/>:
///   - Probe says Installed → InstalledNoRestart.
///   - Exit 0 but probe still NotInstalled → InstalledRequiresRestart
///     (wsl --install successfully wrote the components but the kernel
///     module / lifted-WSL service won't load until a reboot).
///   - Exit 3010 (ERROR_SUCCESS_REBOOT_REQUIRED) → InstalledRequiresRestart.
///   - Anything else → Failed.
/// </summary>
public sealed class ElevatedWslPlatformInstaller : IWslPlatformInstaller
{
    private const int ErrorCancelledHResult = unchecked((int)0x800704C7);
    private const int ErrorSuccessRebootRequired = 3010;

    private readonly IWslPlatformProbe _probe;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<int>> _processRunner;
    private readonly IOpenClawLogger _logger;
    private readonly string _wslExePath;

    public ElevatedWslPlatformInstaller(
        IWslPlatformProbe probe,
        IOpenClawLogger? logger = null,
        Func<ProcessStartInfo, CancellationToken, Task<int>>? processRunner = null,
        string? wslExePath = null)
    {
        _probe = probe;
        _logger = logger ?? NullLogger.Instance;
        _processRunner = processRunner ?? RunDefaultAsync;
        _wslExePath = wslExePath
            ?? WslPlatformProbe.ResolveDefaultWslExePath()
            ?? @"C:\Windows\System32\wsl.exe";
    }

    public async Task<WslPlatformInstallResult> InstallAsync(CancellationToken cancellationToken = default)
    {
        _logger.Info("[WslInstall] Launching elevated wsl.exe --install --no-distribution");

        var psi = new ProcessStartInfo
        {
            FileName = _wslExePath,
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = false,
        };
        psi.ArgumentList.Add("--install");
        psi.ArgumentList.Add("--no-distribution");

        int exitCode;
        try
        {
            exitCode = await _processRunner(psi, cancellationToken);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelledHResult || ex.NativeErrorCode == 1223 /* ERROR_CANCELLED */)
        {
            _logger.Warn("[WslInstall] User declined the administrator prompt.");
            return new WslPlatformInstallResult(
                WslPlatformInstallOutcome.UserDeclinedElevation,
                ExitCode: -1,
                ErrorMessage: "Administrator approval is required to install Windows Subsystem for Linux.");
        }
        catch (Win32Exception ex)
        {
            _logger.Error($"[WslInstall] Failed to launch elevated wsl.exe: {ex.Message}");
            return new WslPlatformInstallResult(
                WslPlatformInstallOutcome.Failed,
                ExitCode: -1,
                ErrorMessage: "Could not start wsl.exe with administrator privileges.",
                Detail: ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"[WslInstall] Unexpected error launching wsl --install: {ex.Message}");
            return new WslPlatformInstallResult(
                WslPlatformInstallOutcome.Failed,
                ExitCode: -1,
                ErrorMessage: "Unexpected error while trying to install Windows Subsystem for Linux.",
                Detail: ex.Message);
        }

        _logger.Info($"[WslInstall] wsl.exe --install --no-distribution exited with code {exitCode}");

        if (exitCode == ErrorSuccessRebootRequired)
            return new WslPlatformInstallResult(WslPlatformInstallOutcome.InstalledRequiresRestart, exitCode);

        var postProbe = await _probe.ProbeAsync(cancellationToken);
        return (postProbe.State, exitCode) switch
        {
            (WslPlatformState.Installed, _) =>
                new WslPlatformInstallResult(WslPlatformInstallOutcome.InstalledNoRestart, exitCode),
            (WslPlatformState.NotInstalled, 0) =>
                new WslPlatformInstallResult(WslPlatformInstallOutcome.InstalledRequiresRestart, exitCode),
            (WslPlatformState.NotInstalled, _) =>
                new WslPlatformInstallResult(
                    WslPlatformInstallOutcome.Failed,
                    exitCode,
                    "Windows Subsystem for Linux install reported failure.",
                    Detail: postProbe.Detail),
            (WslPlatformState.Unknown, 0) =>
                new WslPlatformInstallResult(WslPlatformInstallOutcome.InstalledNoRestart, exitCode),
            _ =>
                new WslPlatformInstallResult(
                    WslPlatformInstallOutcome.Failed,
                    exitCode,
                    "Windows Subsystem for Linux install completed but post-install verification was inconclusive.",
                    Detail: postProbe.Detail),
        };
    }

    private static async Task<int> RunDefaultAsync(ProcessStartInfo psi, CancellationToken cancellationToken)
    {
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null for elevated wsl.exe --install invocation.");
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }
}
