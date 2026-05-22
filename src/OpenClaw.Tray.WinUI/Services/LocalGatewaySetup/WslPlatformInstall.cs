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
    /// <summary>
    /// Fast-detect timeout for the single `wsl --status` probe call. We use a
    /// much shorter bound than the engine's default 30s wsl runner timeout —
    /// when wsl.exe is present but the platform isn't enabled, the not-installed
    /// banner returns within a second or two; longer waits buy nothing and turn
    /// the "click Set up locally" UX into the very hang we are trying to fix.
    /// If the probe call exceeds this, we classify the platform as Unknown and
    /// let the regular preflight code path take over.
    /// </summary>
    public static readonly TimeSpan DefaultStatusProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly IWslCommandRunner _wsl;
    private readonly Func<string, bool> _fileExists;
    private readonly string? _wslExePath;
    private readonly TimeSpan _statusProbeTimeout;

    public WslPlatformProbe(
        IWslCommandRunner wsl,
        Func<string, bool>? fileExists = null,
        string? wslExePath = null,
        TimeSpan? statusProbeTimeout = null)
    {
        _wsl = wsl;
        _fileExists = fileExists ?? File.Exists;
        _wslExePath = wslExePath ?? ResolveDefaultWslExePath();
        _statusProbeTimeout = statusProbeTimeout ?? DefaultStatusProbeTimeout;
    }

    /// <summary>
    /// Returns the canonical Windows System32 path for wsl.exe using
    /// <see cref="Environment.SystemDirectory"/>. We deliberately avoid trusting
    /// the <c>%SystemRoot%</c> environment variable: the installer launches this
    /// path with <c>Verb=runas</c> (elevated), and any code that can mutate the
    /// process environment could otherwise redirect the elevated launch to an
    /// arbitrary binary. <see cref="Environment.SystemDirectory"/> reads from
    /// the kernel, not the env block.
    /// </summary>
    internal static string? ResolveDefaultWslExePath()
    {
        var systemDir = Environment.SystemDirectory;
        if (string.IsNullOrEmpty(systemDir))
            return null;
        return Path.Combine(systemDir, "wsl.exe");
    }

    public async Task<WslPlatformProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (_wslExePath is not null && !_fileExists(_wslExePath))
        {
            return new WslPlatformProbeResult(
                WslPlatformState.NotInstalled,
                Detail: $"wsl.exe not found at {_wslExePath}");
        }

        // Bound the `wsl --status` call independently of the engine-wide wsl
        // runner timeout. On a host without WSL the call returns its banner
        // within ~1s when it returns at all; we should never block the wizard
        // for longer than DefaultStatusProbeTimeout just to discover that.
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(_statusProbeTimeout);

        WslCommandResult status;
        try
        {
            status = await _wsl.RunAsync(["--status"], probeCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WslPlatformProbeResult(
                WslPlatformState.Unknown,
                Detail: $"wsl --status did not respond within {_statusProbeTimeout.TotalSeconds:0}s.");
        }

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
    // ERROR_CANCELLED (1223) is what ShellExecute returns when the user clicks
    // No on the UAC prompt. .NET's Win32Exception exposes that bare Win32 code
    // on `NativeErrorCode` and the wrapped HRESULT (0x800704C7 = SEVERITY_ERROR
    // | FACILITY_WIN32 | 1223) on `HResult`. Some Process.Start failure paths
    // populate only one or the other, so we accept either form.
    private const int ErrorCancelledWin32 = 1223;
    private const int ErrorCancelledHResult = unchecked((int)0x800704C7);
    private const int ErrorSuccessRebootRequired = 3010;

    /// <summary>
    /// Delay between consecutive post-install probe attempts. The lifted-WSL
    /// service / Store finalization on a fresh install can take a few seconds
    /// to expose the platform to a non-elevated `wsl --status`, so we retry
    /// briefly before classifying "exit 0 + probe says NotInstalled" as
    /// restart-required.
    /// </summary>
    public static readonly TimeSpan DefaultPostInstallProbeDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Max number of post-install probe attempts (including the first call).
    /// 6 attempts × (worst-case ~5s per probe + 500ms delay) is a hard upper
    /// bound on the order of ~33s, but in practice the finalize race resolves
    /// in 1-2 probes (~1-2s) on a fresh install or a single probe (~immediate)
    /// when the install genuinely needs a reboot. The first probe outside the
    /// loop runs synchronously without the inter-attempt delay.
    /// </summary>
    public const int DefaultPostInstallProbeAttempts = 6;

    private readonly IWslPlatformProbe _probe;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<int>> _processRunner;
    private readonly IOpenClawLogger _logger;
    private readonly string _wslExePath;
    private readonly TimeSpan _postInstallProbeDelay;
    private readonly int _postInstallProbeAttempts;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public ElevatedWslPlatformInstaller(
        IWslPlatformProbe probe,
        IOpenClawLogger? logger = null,
        Func<ProcessStartInfo, CancellationToken, Task<int>>? processRunner = null,
        string? wslExePath = null,
        TimeSpan? postInstallProbeDelay = null,
        int? postInstallProbeAttempts = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _probe = probe;
        _logger = logger ?? NullLogger.Instance;
        _processRunner = processRunner ?? RunDefaultAsync;
        _wslExePath = wslExePath
            ?? WslPlatformProbe.ResolveDefaultWslExePath()
            ?? @"C:\Windows\System32\wsl.exe";
        // `attempts` of 0 or 1 is observably the same (the first probe runs
        // outside the loop and the loop body is `for (attempt=1; attempt < N; …)`),
        // so we don't clamp it — passing 0 simply means "no retries", which
        // is what the caller asked for. The DELAY value, however, is awaited
        // by `Task.Delay` which throws on negative values; clamp to zero so
        // a mis-configured negative delay degrades to "retry immediately"
        // instead of crashing the wizard.
        _postInstallProbeAttempts = postInstallProbeAttempts ?? DefaultPostInstallProbeAttempts;
        var delay = postInstallProbeDelay ?? DefaultPostInstallProbeDelay;
        _postInstallProbeDelay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        _delayAsync = delayAsync ?? Task.Delay;
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
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelledWin32 || ex.HResult == ErrorCancelledHResult)
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

        // Bounded retry to ride out the post-install finalize race: on a fresh
        // install the Store/lifted-WSL service can take a few seconds to mark
        // the platform as queryable. If we don't retry, exit-0 + NotInstalled
        // looks like "reboot required" when it's actually just "wait 2 more
        // seconds". Round-2 fix: also retry on Unknown — the probe returns
        // Unknown on transient wsl.exe timeouts during lifted-WSL warmup,
        // and bailing early would misclassify a successful install on slow
        // hosts (ARM64, fresh installs) as Failed.
        WslPlatformProbeResult postProbe = await _probe.ProbeAsync(cancellationToken);
        for (int attempt = 1;
             attempt < _postInstallProbeAttempts
                && postProbe.State != WslPlatformState.Installed
                && exitCode == 0;
             attempt++)
        {
            await _delayAsync(_postInstallProbeDelay, cancellationToken);
            postProbe = await _probe.ProbeAsync(cancellationToken);
        }

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
            // Probe still Unknown after exhausting retries AND wsl.exe exited
            // 0 — the install command succeeded but verification is racing
            // lifted-WSL warmup. RequiresRestart is closer to ground truth
            // than Failed (Failed would block the user with a "Try again"
            // re-UAC, RequiresRestart prompts the documented reboot which
            // actually resolves this state on slow hosts).
            (WslPlatformState.Unknown, 0) =>
                new WslPlatformInstallResult(WslPlatformInstallOutcome.InstalledRequiresRestart, exitCode),
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

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            // ShellExecute "runas" returns a Process for a non-elevated
            // launcher; the actual elevated wsl.exe runs under a different
            // token and Process.Kill from this (non-elevated) parent will
            // typically fail with Win32Exception(5 — access denied). We
            // try anyway as a best-effort: it succeeds when the launcher
            // hasn't yet detached, and fails harmlessly otherwise. The
            // important contract is that cancellation propagates (rethrow)
            // so the caller (engine) can mark state Cancelled. Documented
            // limitation: once UAC has been granted, the wsl --install
            // child cannot be reliably cancelled from this process — it
            // may complete in the background and leave the platform
            // installed despite a "cancelled" wizard state. Subsequent
            // launches will see the platform installed and proceed normally.
            try { process.Kill(entireProcessTree: true); }
            catch { /* expected for elevated child; best-effort */ }
            throw;
        }
    }
}
