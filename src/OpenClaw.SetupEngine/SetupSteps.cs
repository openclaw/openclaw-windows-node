using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

// PATH prefix for all openclaw CLI commands in WSL
internal static class WslConstants
{
    public static string GetPathPrefix(string user) =>
        $"""export PATH="/home/{user}/.openclaw/bin:/opt/openclaw/bin:/usr/local/bin:$PATH" """;

    public static string WslExePath
    {
        get
        {
            var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrWhiteSpace(windowsDir))
                windowsDir = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            return Path.Combine(windowsDir, "System32", "wsl.exe");
        }
    }

    public static string SafeWindowsWorkingDirectory
        => Environment.GetFolderPath(Environment.SpecialFolder.System) is { Length: > 0 } systemDir
            ? systemDir
            : Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

    // Default (for backward compat with steps that don't have user context yet)
    public const string PathPrefix = """export PATH="/home/openclaw/.openclaw/bin:/opt/openclaw/bin:/usr/local/bin:$PATH" """;
}

internal static class WslInstallSupport
{
    private static readonly Version s_minDirectNamedInstallVersion = new(2, 4, 4);
    private static readonly System.Text.RegularExpressions.Regex s_wslProductTokenRegex = new(
        @"(?<![A-Za-z0-9])WSL(?![A-Za-z0-9])",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex s_semanticVersionRegex = new(
        @"(?<![\d.])(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?(?![\d.])",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    public const string UpdateUrl = "https://aka.ms/wslstorepage";

    public static string UpdateInstructions
        => $"Update WSL from the Microsoft Store page ({UpdateUrl}), then retry setup.";

    public static IReadOnlyList<string> ParseQuietDistroList(string output)
        => Normalize(output)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim().TrimStart('*').Trim())
            .Where(d => d.Length > 0)
            .ToArray();

    public static bool ContainsDistro(string output, string distroName)
        => ParseQuietDistroList(output).Any(d => d.Equals(distroName, StringComparison.OrdinalIgnoreCase));

    public static bool TryParseWslVersion(string output, out Version version)
    {
        // Match the product token and version shape instead of localized label text.
        // WSL is the stable product acronym; labels around it vary by Windows language
        // and by UTF-16LE/NUL-stripped output shape.
        foreach (var rawLine in Normalize(output).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!s_wslProductTokenRegex.IsMatch(line))
                continue;

            var match = s_semanticVersionRegex.Match(line);
            if (!match.Success)
                continue;

            version = ParseVersionMatch(match);
            return true;
        }

        version = new Version();
        return false;
    }

    private static Version ParseVersionMatch(System.Text.RegularExpressions.Match match)
    {
        var major = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var minor = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        var build = int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        var revision = match.Groups[4].Success
            ? int.Parse(match.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture)
            : -1;
        return revision >= 0
            ? new Version(major, minor, build, revision)
            : new Version(major, minor, build);
    }

    public static bool SupportsDirectNamedInstall(Version version)
        => version.CompareTo(s_minDirectNamedInstallVersion) >= 0;

    // Detects well-known environment problems reported by `wsl --status`
    // (or by other wsl.exe commands that surface the same diagnostic
    // strings). Returns a user-facing remediation message when the output
    // matches a known pattern; returns false otherwise.
    //
    // Only match on text we've actually observed wsl.exe emit. Hex HRESULT
    // codes are stable across UI languages and Windows builds; English
    // sentences are not, and over-broad fallbacks just create false
    // positives.
    public static bool TryGetEnvironmentIssue(string output, out string message)
        => TryGetEnvironmentIssue(output, RuntimeInformation.OSArchitecture, out message);

    // Architecture-aware overload. Internal so tests can exercise both x64
    // and Arm64 wordings without depending on the host process arch.
    internal static bool TryGetEnvironmentIssue(string output, Architecture architecture, out string message)
    {
        var text = Normalize(output);

        // Firmware virtualization off. wsl.exe emits this when the Windows
        // feature is installed but the CPU virtualization extension is
        // turned off; remediation requires a trip into firmware settings,
        // not `wsl --install`. The remediation wording differs by CPU
        // architecture: VT-x/AMD-V/SVM are x86-specific terms that don't
        // exist on Arm64 (Surface Pro X / Pro 9 SQ3 / Pro 11), where the
        // extensions are ARMv8 EL2 and the UEFI label is generic.
        if (Contains(text, "virtualization is not enabled"))
        {
            message = architecture == Architecture.Arm64
                ? "WSL2 requires hardware virtualization, but it is disabled. "
                    + "On ARM64 devices (e.g. Surface), enable virtualization in your device's UEFI "
                    + "settings (look for 'Virtualization Support' or similar). On managed devices this "
                    + "may be controlled by your organization's Intune / device-management policy. "
                    + "Reboot, then retry setup."
                : "WSL2 requires hardware virtualization, but it is disabled in firmware. "
                    + "Enable VT-x/AMD-V (Intel VT or AMD SVM) in your computer's BIOS/UEFI settings, "
                    + "reboot, then retry setup.";
            return true;
        }

        // Observed from `wsl --status` when WSL2 cannot start because the
        // host still needs Virtual Machine Platform and/or firmware
        // virtualization enabled, even though `wsl --version` succeeds.
        if (Contains(text, "WSL2 is not supported with your current machine configuration"))
        {
            var hardwareVirtualizationGuidance = architecture == Architecture.Arm64
                ? "On ARM64 devices (including Surface), also make sure hardware virtualization is allowed by firmware or device-management policy; many devices do not expose a firmware toggle. "
                : "If setup still reports virtualization disabled after enabling the Windows feature, enable VT-x/AMD-V (Intel VT or AMD SVM) in BIOS/UEFI. ";
            message = "WSL2 is not supported with the current machine configuration. "
                + "Enable the Windows 'Virtual Machine Platform' support by running "
                + "`wsl --install --no-distribution` from an elevated PowerShell (or enable "
                + "'Virtual Machine Platform' under 'Turn Windows features on or off'). "
                + hardwareVirtualizationGuidance
                + "Reboot, then retry setup.";
            return true;
        }

        // Required Windows feature missing (Virtual Machine Platform and/or
        // Hyper-V). 0x80370102 = HCS_E_SERVICE_NOT_AVAILABLE, emitted verbatim
        // by wsl.exe as "The virtual machine could not be started because a
        // required feature is not installed." The same remediation
        // (`wsl --install --no-distribution`) addresses both features.
        if (Contains(text, "0x80370102"))
        {
            message = "WSL2 needs the Windows 'Virtual Machine Platform' / Hyper-V platform "
                + "support, which is not currently enabled. Run `wsl --install --no-distribution` "
                + "from an elevated PowerShell (or enable 'Virtual Machine Platform' under 'Turn "
                + "Windows features on or off'), reboot, then retry setup.";
            return true;
        }

        message = string.Empty;
        return false;

        static bool Contains(string haystack, string needle)
            => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    public static string[] BuildDirectInstallArgs(string baseDistro, string distroName, string installPath)
        =>
        [
            "--install",
            "--distribution",
            baseDistro,
            "--name",
            distroName,
            "--location",
            installPath,
            "--no-launch",
            "--web-download"
        ];

    public static bool TryGetDistroVersion(string verboseOutput, string distroName, out int version)
    {
        foreach (var rawLine in Normalize(verboseOutput).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim().TrimStart('*').Trim();
            if (line.Length == 0 || line.StartsWith("NAME", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !parts[0].Equals(distroName, StringComparison.OrdinalIgnoreCase))
                continue;

            return int.TryParse(parts[^1], out version);
        }

        version = 0;
        return false;
    }

    public static string Normalize(string value)
        => value.Replace("\0", "").Replace("\uFEFF", "");
}

// Adapter to bridge SetupLogger → IOpenClawLogger for WebSocket clients
internal sealed class SetupOpenClawLogger(SetupLogger logger) : IOpenClawLogger
{
    public void Info(string message) => logger.Info($"[WS] {message}");
    public void Debug(string message) => logger.Debug($"[WS] {message}");
    // Trace intentionally drops to the default no-op: setup-engine sessions
    // are short-lived and don't normally drive agent-event traffic, and there
    // is no OPENCLAW_TRAY_TRACE-style opt-in gate available here. Letting the
    // interface default (no-op) apply keeps verbose lines out of setup logs.
    public void Trace(string message) { }
    public void Warn(string message) => logger.Warn($"[WS] {message}");
    public void Error(string message, Exception? ex = null) => logger.Error($"[WS] {message}{(ex != null ? $": {ex}" : "")}");
}

// ═══════════════════════════════════════════════════════════════════
// CLEANUP STEPS
// ═══════════════════════════════════════════════════════════════════

public sealed class CleanupStaleDistroStep : SetupStep
{
    public override string Id => "cleanup-distro";
    public override string DisplayName => "Clean up stale WSL distro";
    public override bool CanRetry => false;

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.CleanBeforeRun;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        var wslDir = Path.Combine(ctx.LocalDataDir, "wsl", distro);
        var list = await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--list", "--quiet"], TimeSpan.FromSeconds(15), ct: ct);
        if (list.ExitCode != 0)
            return StepResult.Ok("WSL not available or no distros - nothing to clean");

        var distros = WslInstallSupport.ParseQuietDistroList(list.Stdout);

        ctx.Logger.Debug($"Found WSL distros: [{string.Join(", ", distros)}]");

        if (!distros.Any(d => d.Equals(distro, StringComparison.OrdinalIgnoreCase)))
        {
            // Distro not registered, but disk directory may still exist from prior crash
            if (Directory.Exists(wslDir))
            {
                ctx.Logger.Info($"Removing orphaned WSL directory: {wslDir}");
                var delete = await DeleteDistroDirectoryWithRetries(ctx, wslDir, ct);
                if (!delete.IsSuccess)
                    return delete;
            }
            ctx.Logger.Decision("No stale distro found", "skip cleanup");
            return StepResult.Ok("No stale distro to clean");
        }

        ctx.Logger.Decision($"Found existing distro '{distro}'", "terminating and unregistering");

        // Stop only the app-owned distro. Global WSL shutdown would disrupt unrelated distros.
        await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--terminate", distro], TimeSpan.FromSeconds(30), ct: ct);
        await Task.Delay(2000, ct); // Let port release

        var unregister = await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--unregister", distro], TimeSpan.FromSeconds(60), ct: ct);
        if (unregister.ExitCode != 0)
        {
            ctx.Logger.Warn($"First unregister attempt failed (exit {unregister.ExitCode}); retrying targeted termination");
            await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--terminate", distro], TimeSpan.FromSeconds(30), ct: ct);
            await Task.Delay(3000, ct);
            unregister = await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--unregister", distro], TimeSpan.FromSeconds(60), ct: ct);
        }

        if (unregister.ExitCode == 0)
        {
            // Also remove the on-disk WSL vhdx directory (--import fails if it exists)
            var delete = await DeleteDistroDirectoryWithRetries(ctx, wslDir, ct);
            if (!delete.IsSuccess)
                return delete;

            // Wait for port to be released
            ctx.Logger.Info("Waiting for port release after distro termination...");
            await PreflightPortStep.WaitForPortFreeAsync(ctx.Config.GatewayPort, ctx.Config.Gateway.Bind, ctx.Logger, ct);
            return StepResult.Ok($"Unregistered stale distro '{distro}'");
        }

        return StepResult.Fail($"Failed to unregister distro: {unregister.Stderr}");
    }

    internal static async Task<StepResult> DeleteDistroDirectoryWithRetries(SetupContext ctx, string wslDir, CancellationToken ct)
    {
        Exception? lastError = null;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (File.Exists(wslDir))
                {
                    if (File.GetAttributes(wslDir).HasFlag(FileAttributes.ReparsePoint))
                        return StepResult.Fail($"App-owned WSL path '{wslDir}' is a reparse point; remove it manually and retry setup.");

                    ctx.Logger.Info($"Removing app-owned WSL file at install path: {wslDir}");
                    File.Delete(wslDir);
                }
                else if (Directory.Exists(wslDir))
                {
                    if (new DirectoryInfo(wslDir).Attributes.HasFlag(FileAttributes.ReparsePoint))
                        return StepResult.Fail($"App-owned WSL directory '{wslDir}' is a reparse point; remove it manually and retry setup.");

                    ctx.Logger.Info($"Removing app-owned WSL directory: {wslDir}");
                    Directory.Delete(wslDir, recursive: true);
                }

                var parent = Path.GetDirectoryName(wslDir);
                if (!string.IsNullOrWhiteSpace(parent) &&
                    Directory.Exists(parent) &&
                    !Directory.EnumerateFileSystemEntries(parent).Any())
                {
                    Directory.Delete(parent);
                    ctx.Logger.Info("Deleted empty wsl\\ parent directory");
                }

                return StepResult.Ok("WSL directory removed");
            }
            catch (DirectoryNotFoundException)
            {
                return StepResult.Ok("WSL directory already absent");
            }
            catch (IOException ex)
            {
                lastError = ex;
                if (attempt >= 3)
                    break;

                ctx.Logger.Warn($"VHD directory still locked, retrying in {(attempt + 1) * 2}s...");
                await Task.Delay(TimeSpan.FromSeconds((attempt + 1) * 2), ct);
            }
            catch (UnauthorizedAccessException ex)
            {
                lastError = ex;
                if (attempt >= 3)
                    break;

                ctx.Logger.Warn($"VHD directory access denied, retrying in {(attempt + 1) * 2}s...");
                await Task.Delay(TimeSpan.FromSeconds((attempt + 1) * 2), ct);
            }
        }

        return StepResult.Fail(
            $"Failed to remove app-owned WSL directory '{wslDir}'. Close any process using the OpenClaw WSL distro and retry setup."
            + (lastError is null ? "" : $" Last error: {lastError.Message}"));
    }
}

public sealed class CleanupStaleGatewayStep : SetupStep
{
    public override string Id => "cleanup-gateway";
    public override string DisplayName => "Clean up stale gateway state";
    public override bool CanRetry => false;

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.CleanBeforeRun;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        // Remove stale setup-state.json from AppData (legacy location)
        var stateFile = Path.Combine(ctx.DataDir, "setup-state.json");
        if (File.Exists(stateFile))
        {
            File.Delete(stateFile);
            ctx.Logger.Info("Deleted stale setup-state.json (AppData)");
        }

        // Also remove from LocalAppData (current write location)
        var localStateFile = Path.Combine(ctx.LocalDataDir, "setup-state.json");
        if (File.Exists(localStateFile))
        {
            File.Delete(localStateFile);
            ctx.Logger.Info("Deleted stale setup-state.json (LocalAppData)");
        }

        // Remove stale gateway record for our local URL if it exists
        var registry = new GatewayRegistry(ctx.DataDir, logger: new SetupOpenClawLogger(ctx.Logger));
        registry.Load();
        var existing = registry.FindByUrl(ctx.GatewayUrl!);
        if (existing != null)
        {
            // Preserve non-local records and SSH-tunneled gateways — they may be
            // remote gateways that happen to use localhost as a forwarded port.
            if (!PairOperatorStep.IsSetupManagedLocalRecord(existing, ctx))
            {
                ctx.Logger.Warn($"Skipping cleanup of gateway record {existing.Id}: " +
                    "not a SetupEngine-managed local gateway");
            }
            else
            {
                // Clean identity directory
                var identityDir = registry.GetIdentityDirectory(existing.Id);
                if (Directory.Exists(identityDir))
                {
                    Directory.Delete(identityDir, recursive: true);
                    ctx.Logger.Info($"Deleted stale identity directory: {identityDir}");
                }
                registry.Remove(existing.Id);
                registry.Save();
                ctx.Logger.Info($"Removed stale gateway record for {ctx.GatewayUrl}");
            }
        }

        await Task.CompletedTask;
        return StepResult.Ok("Gateway state cleaned");
    }

    public override Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        // Delete setup-state.json (written by VerifyEndToEndStep)
        var localDataPath = ctx.LocalDataDir;

        var stateFile = Path.Combine(localDataPath, "setup-state.json");
        if (File.Exists(stateFile))
        {
            File.Delete(stateFile);
            ctx.Logger.Info("[Uninstall] Deleted setup-state.json");
        }
        else
        {
            ctx.Logger.Info("[Uninstall] setup-state.json already absent");
        }

        return Task.CompletedTask;
    }
}

// ═══════════════════════════════════════════════════════════════════
// PREFLIGHT STEPS
// ═══════════════════════════════════════════════════════════════════

public sealed class PreflightOsStep : SetupStep
{
    public override string Id => "preflight-os";
    public override string DisplayName => "Verify Windows OS";
    public override bool CanRetry => false;

    public override Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (!Environment.Is64BitOperatingSystem)
            return Task.FromResult(StepResult.Terminal("64-bit Windows required"));

        if (!OperatingSystem.IsWindows())
            return Task.FromResult(StepResult.Terminal("Windows OS required"));

        var version = Environment.OSVersion.Version;
        ctx.Logger.Info($"OS: Windows {version} (64-bit)");

        return Task.FromResult(StepResult.Ok($"Windows {version}"));
    }
}

public sealed class PreflightWslStep : SetupStep
{
    public override string Id => "preflight-wsl";
    public override string DisplayName => "Verify WSL available";
    public override bool CanRetry => false;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var versionResult = await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--version"], TimeSpan.FromSeconds(5), ct: ct);
        if (versionResult.ExitCode != 0 && LooksUnavailable(versionResult))
        {
            var installResult = await InstallWslPlatformAsync(ctx, ct);
            if (!installResult.IsSuccess)
                return installResult;

            versionResult = await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--version"], TimeSpan.FromSeconds(5), ct: ct);
        }

        if (versionResult.ExitCode != 0)
        {
            if (LooksTooOldForVersionCommand(versionResult))
                return StepResult.Terminal($"WSL is installed but too old for clean app-owned gateway setup. {WslInstallSupport.UpdateInstructions}");

            return StepResult.Terminal($"WSL is not available. {FirstUsefulLine(versionResult)}");
        }

        var versionOutput = NormalizeWslOutput($"{versionResult.Stdout}\n{versionResult.Stderr}");
        if (!WslInstallSupport.TryParseWslVersion(versionOutput, out var wslVersion))
            return StepResult.Terminal($"WSL version output did not include a parseable WSL version. {WslInstallSupport.UpdateInstructions}");

        if (!WslInstallSupport.SupportsDirectNamedInstall(wslVersion))
            return StepResult.Terminal($"WSL {wslVersion} cannot create a clean app-owned OpenClaw gateway distro. {WslInstallSupport.UpdateInstructions}");

        ctx.Logger.Info($"WSL version output: {NormalizeWslOutput(versionResult.Stdout).Trim()}");
        ctx.Logger.Info($"WSL direct named install is supported (version {wslVersion})");

        // wsl --version can succeed even when the WSL2 platform itself is
        // unusable (Virtual Machine Platform component disabled, hardware
        // virtualization off in firmware, Hyper-V missing, ...). Surface
        // that diagnostic now so the user gets an actionable message
        // before pipeline reaches the actual `wsl --install` step.
        var statusIssue = await DetectEnvironmentIssueAsync(ctx, ct);
        if (statusIssue != null)
            return StepResult.Terminal(statusIssue);

        return StepResult.Ok("WSL available");
    }

    internal static async Task<string?> DetectEnvironmentIssueAsync(SetupContext ctx, CancellationToken ct)
    {
        var status = await ctx.Commands.RunAsync(
            WslConstants.WslExePath,
            ["--status"],
            TimeSpan.FromSeconds(10),
            ct: ct);

        var combined = $"{status.Stdout}\n{status.Stderr}";
        if (WslInstallSupport.TryGetEnvironmentIssue(combined, out var message))
        {
            ctx.Logger.Warn($"WSL environment issue detected: {NormalizeWslOutput(combined).Trim()}");
            return message;
        }

        return null;
    }

    private static async Task<StepResult> InstallWslPlatformAsync(SetupContext ctx, CancellationToken ct)
    {
        ctx.Logger.Warn("WSL platform appears to be missing; launching elevated WSL platform install");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = WslConstants.WslExePath,
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WorkingDirectory = WslConstants.SafeWindowsWorkingDirectory
            };
            psi.ArgumentList.Add("--install");
            psi.ArgumentList.Add("--no-distribution");

            using var process = Process.Start(psi);
            if (process == null)
                return StepResult.Fail("Could not start elevated WSL platform installer.");

            await process.WaitForExitAsync(ct);

            if (process.ExitCode == 3010)
                return StepResult.Terminal("WSL platform install requires a restart. Reboot Windows, then run setup again.");

            if (process.ExitCode != 0)
                return StepResult.Fail($"WSL platform install failed with exit code {process.ExitCode}.");

            var probe = await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--version"], TimeSpan.FromSeconds(5), ct: ct);
            if (probe.ExitCode != 0 || LooksUnavailable(probe))
                return StepResult.Terminal("WSL platform install completed, but Windows still reports WSL unavailable. Reboot Windows, then run setup again.");

            return StepResult.Ok("WSL platform installed");
        }
        catch (System.ComponentModel.Win32Exception ex) when ((uint)ex.NativeErrorCode == 1223)
        {
            return StepResult.Fail("WSL platform install was cancelled at the elevation prompt.");
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"WSL platform install failed: {ex.Message}", ex);
        }
    }

    private static bool LooksUnavailable(CommandResult result)
    {
        var text = NormalizeWslOutput($"{result.Stdout}\n{result.Stderr}");
        return text.Contains("aka.ms/wslinstall", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Windows Subsystem for Linux has no installed distributions", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not recognized", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not installed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksTooOldForVersionCommand(CommandResult result)
    {
        var text = NormalizeWslOutput($"{result.Stdout}\n{result.Stderr}");
        return text.Contains("Invalid command line option", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unrecognized option", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unknown option", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWslOutput(string value)
        => WslInstallSupport.Normalize(value);

    private static string FirstUsefulLine(CommandResult result)
    {
        var text = NormalizeWslOutput($"{result.Stderr}\n{result.Stdout}");
        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
            ?? "Run wsl --install from an elevated terminal and retry setup.";
    }
}

public sealed class PreflightPortStep : SetupStep
{
    public override string Id => "preflight-port";
    public override string DisplayName => "Check gateway port available";
    public override bool CanRetry => false;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var port = ctx.Config.GatewayPort;
        var addresses = ctx.Config.Gateway.Bind.Equals("lan", StringComparison.OrdinalIgnoreCase)
            ? new[] { IPAddress.Any, IPAddress.IPv6Any }
            : [IPAddress.Loopback];

        // Poll briefly in case WSL port forwarding proxy hasn't fully released the
        // port yet after targeted distro termination in a prior cleanup step.
        await WaitForPortFreeAsync(port, ctx.Config.Gateway.Bind, ctx.Logger, ct, maxWaitSeconds: 10);

        foreach (var address in addresses)
        {
            if (!CanBind(address, port, out var error))
                return StepResult.Fail($"Port {port} is already in use for {DescribeBind(address)} ({error.SocketErrorCode})");
        }

        return StepResult.Ok($"Port {port} is available");
    }

    /// <summary>
    /// Polls until all required addresses for <paramref name="port"/> can be bound,
    /// or until <paramref name="maxWaitSeconds"/> elapses.  Silently returns if the
    /// port never frees — <see cref="ExecuteAsync"/> will still hard-fail in that case.
    /// </summary>
    internal static async Task WaitForPortFreeAsync(
        int port, string bind, SetupLogger logger, CancellationToken ct,
        int maxWaitSeconds = 20)
    {
        var addresses = bind.Equals("lan", StringComparison.OrdinalIgnoreCase)
            ? new[] { IPAddress.Any, IPAddress.IPv6Any }
            : [IPAddress.Loopback];

        var deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);
        var attempt = 0;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (addresses.All(a => CanBind(a, port, out _)))
            {
                if (attempt > 0)
                    logger.Info($"Port {port} became free after {attempt * 500}ms");
                return;
            }

            attempt++;
            await Task.Delay(500, ct);
        }

        logger.Warn($"Port {port} still in use after {maxWaitSeconds}s poll — proceeding to hard check");
    }

    internal static bool CanBind(IPAddress address, int port, out SocketException error)
    {
        var listener = new TcpListener(address, port)
        {
            ExclusiveAddressUse = true
        };

        try
        {
            listener.Start();
            error = null!;
            return true;
        }
        catch (SocketException ex)
        {
            error = ex;
            return false;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string DescribeBind(IPAddress address)
        => address.Equals(IPAddress.Any) ? "LAN IPv4 bind" :
           address.Equals(IPAddress.IPv6Any) ? "LAN IPv6 bind" :
           "loopback bind";
}

// ═══════════════════════════════════════════════════════════════════
// WSL STEPS
// ═══════════════════════════════════════════════════════════════════

public sealed class CreateWslInstanceStep : SetupStep
{
    public override string Id => "wsl-create";
    public override string DisplayName => "Create WSL instance";
    public override bool CanRetry => false;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        var baseDistro = ctx.Config.BaseDistro.Trim();

        if (string.IsNullOrWhiteSpace(baseDistro))
            return StepResult.Terminal("BaseDistro is required for fresh WSL gateway setup.");

        var installPath = Path.Combine(ctx.LocalDataDir, "wsl", distro);
        ctx.Logger.Info($"Creating clean app-owned WSL distro '{distro}' from '{baseDistro}' at '{installPath}'");

        var existing = await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--list", "--quiet"], TimeSpan.FromSeconds(15), ct: ct);
        if (existing.ExitCode != 0)
            return StepResult.Fail($"Failed to list WSL distros before creating '{distro}': {existing.Stderr}");

        if (WslInstallSupport.ContainsDistro(existing.Stdout, distro))
            return StepResult.Fail($"Target WSL distro '{distro}' still exists after cleanup; refusing to create a new gateway over unknown state.");

        var pathCheck = EnsureInstallPathReady(installPath);
        if (!pathCheck.IsSuccess)
            return pathCheck;

        Directory.CreateDirectory(Path.GetDirectoryName(installPath)!);

        var installArgs = WslInstallSupport.BuildDirectInstallArgs(baseDistro, distro, installPath);
        ctx.Logger.Info($"Installing fresh WSL distro with arguments: {string.Join(' ', installArgs)}");
        var install = await ctx.Commands.RunAsync(
            WslConstants.WslExePath,
            installArgs,
            TimeSpan.FromMinutes(15),
            ct: ct);

        if (install.ExitCode != 0)
        {
            var cleanupError = await CleanupPartialInstall(ctx, distro, installPath, ct);
            return StepResult.Fail(
                $"Fresh WSL install failed for '{distro}' from '{baseDistro}' (exit {install.ExitCode}): {FirstNonEmpty(install.Stderr, install.Stdout)}{cleanupError}");
        }

        var verify = await VerifyFreshDistro(ctx, distro, installPath, ct);
        if (!verify.IsSuccess)
        {
            var cleanupError = await CleanupPartialInstall(ctx, distro, installPath, ct);
            return StepResult.Fail($"{verify.Message}{cleanupError}");
        }

        return verify;
    }

    private static StepResult EnsureInstallPathReady(string installPath)
    {
        if (File.Exists(installPath))
        {
            if (File.GetAttributes(installPath).HasFlag(FileAttributes.ReparsePoint))
                return StepResult.Fail($"App-owned WSL install path '{installPath}' is a reparse point; remove it manually and retry setup.");

            File.Delete(installPath);
            return StepResult.Ok();
        }

        if (!Directory.Exists(installPath))
            return StepResult.Ok();

        if (new DirectoryInfo(installPath).Attributes.HasFlag(FileAttributes.ReparsePoint))
            return StepResult.Fail($"App-owned WSL install directory '{installPath}' is a reparse point; remove it manually and retry setup.");

        if (Directory.EnumerateFileSystemEntries(installPath).Any())
        {
            return StepResult.Fail(
                $"App-owned WSL install directory '{installPath}' still contains files after cleanup; refusing to create a new gateway over unknown state.");
        }

        Directory.Delete(installPath);
        return StepResult.Ok();
    }

    private static async Task<StepResult> VerifyFreshDistro(SetupContext ctx, string distro, string installPath, CancellationToken ct)
    {
        var list = await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--list", "--quiet"], TimeSpan.FromSeconds(15), ct: ct);
        if (list.ExitCode != 0 || !WslInstallSupport.ContainsDistro(list.Stdout, distro))
        {
            var environmentIssue = await PreflightWslStep.DetectEnvironmentIssueAsync(ctx, ct);
            var baseMessage = $"Fresh WSL install did not register expected distro '{distro}'.";
            return StepResult.Fail(environmentIssue != null ? $"{baseMessage} {environmentIssue}" : baseMessage);
        }

        var verbose = await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--list", "--verbose"], TimeSpan.FromSeconds(15), ct: ct);
        if (verbose.ExitCode != 0 || !WslInstallSupport.TryGetDistroVersion(verbose.Stdout, distro, out var version))
            return StepResult.Fail($"Fresh WSL install registered '{distro}', but setup could not verify it is WSL2.");

        if (version != 2)
            return StepResult.Fail($"Fresh WSL install registered '{distro}' as WSL{version}; WSL2 is required.");

        var probe = await ctx.Commands.RunAsync(
            WslConstants.WslExePath,
            ["-d", distro, "-u", "root", "--", "sh", "-lc", "id -u && test -d / && echo OPENCLAW_FRESH_WSL_READY"],
            TimeSpan.FromSeconds(30),
            ct: ct);

        if (probe.ExitCode != 0 || !probe.Stdout.Contains("OPENCLAW_FRESH_WSL_READY", StringComparison.Ordinal))
            return StepResult.Fail($"Fresh WSL distro '{distro}' could not run a root verification command: {FirstNonEmpty(probe.Stderr, probe.Stdout)}");

        return StepResult.Ok($"Created clean WSL2 distro '{distro}' at '{installPath}'");
    }

    private static async Task<string> CleanupPartialInstall(SetupContext ctx, string distro, string installPath, CancellationToken ct)
    {
        var cleanupErrors = new List<string>();
        var installPathExists = Directory.Exists(installPath) || File.Exists(installPath);
        var list = await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--list", "--quiet"], TimeSpan.FromSeconds(15), ct: ct);
        var registrationStateKnown = list.ExitCode == 0;
        var distroExists = registrationStateKnown && WslInstallSupport.ContainsDistro(list.Stdout, distro);
        var canDeleteInstallPath = registrationStateKnown && !distroExists;

        if (!registrationStateKnown)
        {
            ctx.Logger.Warn($"Partial install cleanup could not list WSL distros (exit {list.ExitCode}); attempting best-effort unregister for '{distro}' before deleting app-owned files");
            canDeleteInstallPath = await TryUnregisterPartialInstall(ctx, distro, cleanupErrors, ct);
        }
        else if (distroExists)
        {
            canDeleteInstallPath = await TryUnregisterPartialInstall(ctx, distro, cleanupErrors, ct);
        }

        if (!canDeleteInstallPath)
        {
            if (!registrationStateKnown)
            {
                cleanupErrors.Insert(0,
                    $"could not confirm whether distro '{distro}' is still registered: {FirstNonEmpty(list.Stderr, list.Stdout)}");
            }

            if (installPathExists)
            {
                cleanupErrors.Add(
                    $"skipped deleting app-owned install path '{installPath}' until distro '{distro}' is confirmed unregistered");
            }
        }
        else if (installPathExists)
        {
            var delete = await CleanupStaleDistroStep.DeleteDistroDirectoryWithRetries(ctx, installPath, ct);
            if (!delete.IsSuccess)
                cleanupErrors.Add(delete.Message ?? "install directory cleanup failed");
        }

        return cleanupErrors.Count == 0
            ? ""
            : $" Partial app-owned distro cleanup also failed: {string.Join("; ", cleanupErrors)}";
    }

    private static async Task<bool> TryUnregisterPartialInstall(SetupContext ctx, string distro, List<string> cleanupErrors, CancellationToken ct)
    {
        var terminate = await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--terminate", distro], TimeSpan.FromSeconds(30), ct: ct);
        if (terminate.ExitCode != 0 && !IsMissingDistroResult(terminate))
            cleanupErrors.Add($"terminate exit {terminate.ExitCode}: {FirstNonEmpty(terminate.Stderr, terminate.Stdout)}");

        var unregister = await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--unregister", distro], TimeSpan.FromSeconds(60), ct: ct);
        if (unregister.ExitCode == 0 || IsMissingDistroResult(unregister))
            return true;

        ctx.Logger.Warn($"Partial install unregister failed (exit {unregister.ExitCode}); retrying targeted termination");
        terminate = await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--terminate", distro], TimeSpan.FromSeconds(30), ct: ct);
        if (terminate.ExitCode != 0 && !IsMissingDistroResult(terminate))
            cleanupErrors.Add($"retry terminate exit {terminate.ExitCode}: {FirstNonEmpty(terminate.Stderr, terminate.Stdout)}");

        unregister = await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--unregister", distro], TimeSpan.FromSeconds(60), ct: ct);
        if (unregister.ExitCode == 0 || IsMissingDistroResult(unregister))
            return true;

        cleanupErrors.Add($"unregister exit {unregister.ExitCode}: {FirstNonEmpty(unregister.Stderr, unregister.Stdout)}");
        return false;
    }

    private static bool IsMissingDistroResult(CommandResult result)
    {
        if (result.ExitCode == 0)
            return false;

        var output = FirstNonEmpty(result.Stderr, result.Stdout);
        return output.Contains("There is no distribution with the supplied name", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("WSL_E_DISTRO_NOT_FOUND", StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstNonEmpty(params string[] values)
        => values.Select(v => v.Trim()).FirstOrDefault(v => v.Length > 0) ?? "no output";

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--terminate", distro], TimeSpan.FromSeconds(30), ct: ct);
        await Task.Delay(2000, ct); // Let port/VHD locks release
        await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--unregister", distro], TimeSpan.FromSeconds(60), ct: ct);

        // VHD parent dir cleanup (mirrors old uninstall step 5a)
        var localDataPath = ctx.LocalDataDir;
        var vhdDir = Path.Combine(localDataPath, "wsl", distro);
        if (Directory.Exists(vhdDir))
        {
            Directory.Delete(vhdDir, recursive: true);
            ctx.Logger.Info($"[Uninstall] Deleted VHD parent directory: {vhdDir}");
        }

        // WSL parent dir cleanup — remove empty wsl\ directory (mirrors old step 5b)
        var wslDir = Path.Combine(localDataPath, "wsl");
        if (Directory.Exists(wslDir) && !Directory.EnumerateFileSystemEntries(wslDir).Any())
        {
            Directory.Delete(wslDir);
            ctx.Logger.Info("[Uninstall] Deleted empty wsl\\ parent directory");
        }
    }
}

public sealed class ConfigureWslInstanceStep : SetupStep
{
    public override string Id => "wsl-configure";
    public override string DisplayName => "Configure WSL instance";

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        var wsl = ctx.Config.Wsl;

        if (!WslConfig.IsValidLinuxUserName(wsl.User))
            return StepResult.Terminal($"Invalid WSL user '{wsl.User}'. Use a Linux username matching [a-z_][a-z0-9_-]{{0,31}}.");

        // Build wsl.conf from config
        var wslConf = $"""
[boot]
systemd={wsl.Systemd.ToString().ToLower()}

[automount]
enabled={wsl.Automount.ToString().ToLower()}
mountFsTab={wsl.MountFsTab.ToString().ToLower()}

[interop]
enabled={wsl.Interop.ToString().ToLower()}
appendWindowsPath={wsl.AppendWindowsPath.ToString().ToLower()}

[user]
default={wsl.User}

[time]
useWindowsTimezone={wsl.UseWindowsTimezone.ToString().ToLower()}
""";

        // Create user and directories
        var script = $"""
            set -e
            
            # Create user if not exists
            if ! id -u {wsl.User} &>/dev/null; then
                useradd -m -s /bin/bash {wsl.User}
            fi
            
            # Create required directories
            mkdir -p /home/{wsl.User}/.openclaw
            mkdir -p /var/lib/openclaw
            mkdir -p /var/log/openclaw
            mkdir -p /opt/openclaw
            
            chown -R {wsl.User}:{wsl.User} /home/{wsl.User}/.openclaw
            chown -R {wsl.User}:{wsl.User} /var/lib/openclaw
            chown -R {wsl.User}:{wsl.User} /var/log/openclaw
            chown -R {wsl.User}:{wsl.User} /opt/openclaw
            
            # Write wsl.conf
            cat > /etc/wsl.conf << 'WSLCONF'
            {wslConf}
            WSLCONF
            
            echo "CONFIGURED_OK"
            """;

        var result = await ctx.Commands.RunInWslAsync(distro, script, TimeSpan.FromSeconds(60), ct: ct, user: "root");

        if (result.ExitCode != 0 || !result.Stdout.Contains("CONFIGURED_OK"))
            return StepResult.Fail($"Configuration failed: {result.Stderr}");

        // Restart WSL to apply wsl.conf (systemd)
        ctx.Logger.Info("Restarting WSL to apply configuration (systemd)");
        await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--terminate", distro], TimeSpan.FromSeconds(30), ct: ct);
        await Task.Delay(2000, ct); // Let WSL settle

        return StepResult.Ok("WSL instance configured");
    }
}

public sealed class ValidateWslLockdownStep : SetupStep
{
    private const int MaxWslConfReadAttempts = 3;

    public override string Id => "validate-wsl-lockdown";
    public override string DisplayName => "Validate WSL lockdown";
    public override bool CanRetry => false;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        var wsl = ctx.Config.Wsl;

        var readConf = await ReadWslConfWithStartupRetryAsync(ctx, distro, ct);
        if (readConf.ExitCode != 0)
            return StepResult.Terminal("Cannot read /etc/wsl.conf - WSL configuration may not have been applied");

        var errors = ValidateWslConf(readConf.Stdout, wsl);
        if (errors.Count > 0)
        {
            var msg = "WSL lockdown validation failed:\n" + string.Join("\n", errors.Select(e => $"  - {e}"));
            return StepResult.Terminal(msg);
        }

        var requiredDirs = new[]
        {
            $"/home/{wsl.User}/.openclaw",
            "/var/lib/openclaw",
            "/var/log/openclaw",
            "/opt/openclaw"
        };

        // Generate per-directory checks inline (no bash variables).
        // wsl.exe argv variable-expansion pitfall: see docs/WSL_EXE_ARGV_PITFALL.md.
        // `wsl.exe -- bash -c <script>` performs shell-variable expansion on argv
        // before bash sees it, so any $var that isn't defined in the Windows env
        // gets dropped. This step works around the issue by C#-interpolating every
        // value into the script string (no bash variables) — that pattern is fine
        // for short scripts with a small fixed value set and no spaces in values.
        // New multi-line callers should prefer the stdin path:
        //   ctx.Commands.RunInWslAsync(..., inputViaStdin: true)
        // which pipes the script via `bash -s` stdin and bypasses the issue entirely.
        var dirChecks = new System.Text.StringBuilder();
        foreach (var d in requiredDirs)
        {
            dirChecks.AppendLine($"test -d {d} || {{ echo DIR_MISSING:{d}; exit 1; }}");
            dirChecks.AppendLine($"test $(stat -c %U {d} 2>/dev/null) = {wsl.User} || {{ echo OWNER_MISMATCH:{d}:$(stat -c %U {d} 2>/dev/null); exit 1; }}");
        }

        var verifyScript = "set -e\n"
            + $"id -u {wsl.User} &>/dev/null || {{ echo USER_MISSING; exit 1; }}\n"
            + dirChecks
            + "echo LOCKDOWN_VALID\n";

        var verify = await ctx.Commands.RunInWslAsync(distro, verifyScript, TimeSpan.FromSeconds(30), ct: ct);

        ctx.Logger.Debug($"Lockdown verify exit={verify.ExitCode} stdout={verify.Stdout.Trim()} stderr={verify.Stderr.Trim()}");

        if (verify.Stdout.Contains("USER_MISSING", StringComparison.Ordinal))
            return StepResult.Terminal($"User '{wsl.User}' does not exist in distro '{distro}'");

        if (verify.Stdout.Contains("DIR_MISSING:", StringComparison.Ordinal))
        {
            var line = verify.Stdout.Split('\n').FirstOrDefault(l => l.Contains("DIR_MISSING:")) ?? "";
            var dir = line.Trim().Split(':', 2).ElementAtOrDefault(1)?.Trim() ?? "unknown";
            return StepResult.Terminal($"Required directory missing: {dir}");
        }

        if (verify.Stdout.Contains("OWNER_MISMATCH:", StringComparison.Ordinal))
        {
            var line = verify.Stdout.Split('\n').FirstOrDefault(l => l.Contains("OWNER_MISMATCH:")) ?? "";
            var parts = line.Trim().Split(':');
            return StepResult.Terminal($"Directory {parts.ElementAtOrDefault(1)} owned by '{parts.ElementAtOrDefault(2)}', expected '{wsl.User}'");
        }

        if (!verify.Stdout.Contains("LOCKDOWN_VALID", StringComparison.Ordinal))
        {
            var detail = string.IsNullOrWhiteSpace(verify.Stderr) ? verify.Stdout.Trim() : verify.Stderr.Trim();
            return StepResult.Terminal($"WSL lockdown validation failed: {detail}");
        }

        if (!string.IsNullOrEmpty(wsl.Memory))
            ctx.Logger.Warn($"Wsl.Memory='{wsl.Memory}' is set but requires host-level .wslconfig, not per-distro wsl.conf");
        if (!string.IsNullOrEmpty(wsl.Swap))
            ctx.Logger.Warn($"Wsl.Swap='{wsl.Swap}' is set but requires host-level .wslconfig, not per-distro wsl.conf");

        ctx.Logger.Info("WSL lockdown validated: all invariants verified");
        return StepResult.Ok("WSL lockdown validated");
    }

    private static async Task<CommandResult> ReadWslConfWithStartupRetryAsync(
        SetupContext ctx,
        string distro,
        CancellationToken ct)
    {
        CommandResult? last = null;
        for (var attempt = 1; attempt <= MaxWslConfReadAttempts; attempt++)
        {
            last = await ctx.Commands.RunInWslAsync(
                distro,
                "cat /etc/wsl.conf",
                TimeSpan.FromSeconds(30),
                ct: ct);

            if (last.ExitCode == 0)
                return last;

            if (attempt == MaxWslConfReadAttempts)
                break;

            ctx.Logger.Warn(
                $"Reading /etc/wsl.conf failed after WSL restart (attempt {attempt}/{MaxWslConfReadAttempts}, timedOut={last.TimedOut}); retrying");
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        return last ?? new CommandResult(-1, "", "No WSL config read attempts were made.", TimeSpan.Zero, TimedOut: false);
    }

    internal static List<string> ValidateWslConf(string conf, WslConfig wsl)
    {
        var values = ParseWslConf(conf);
        var errors = new List<string>();

        ValidateConfValue(values, "boot", "systemd", wsl.Systemd, errors);
        ValidateConfValue(values, "interop", "enabled", wsl.Interop, errors);
        ValidateConfValue(values, "interop", "appendWindowsPath", wsl.AppendWindowsPath, errors);
        ValidateConfValue(values, "automount", "enabled", wsl.Automount, errors);
        ValidateConfValue(values, "automount", "mountFsTab", wsl.MountFsTab, errors);
        ValidateConfValue(values, "user", "default", wsl.User, errors);

        return errors;
    }

    private static Dictionary<string, Dictionary<string, string>> ParseWslConf(string conf)
    {
        var values = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        string? currentSection = null;

        using var reader = new StringReader(conf);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
                continue;

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                currentSection = trimmed[1..^1].Trim();
                if (!values.ContainsKey(currentSection))
                    values[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            if (currentSection is null)
                continue;

            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = trimmed[..separator].Trim();
            var value = trimmed[(separator + 1)..].Trim();
            values[currentSection][key] = value;
        }

        return values;
    }

    private static void ValidateConfValue(Dictionary<string, Dictionary<string, string>> conf, string section, string key, bool expected, List<string> errors) =>
        ValidateConfValue(conf, section, key, expected.ToString().ToLowerInvariant(), errors);

    private static void ValidateConfValue(Dictionary<string, Dictionary<string, string>> conf, string section, string key, string expected, List<string> errors)
    {
        if (!conf.TryGetValue(section, out var sectionValues) ||
            !sectionValues.TryGetValue(key, out var actual) ||
            !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Expected [{section}] {key}={expected} in wsl.conf");
        }
    }
}

// ═══════════════════════════════════════════════════════════════════
// GATEWAY INSTALL STEPS
// ═══════════════════════════════════════════════════════════════════

public sealed class InstallCliStep : SetupStep
{
    public override string Id => "install-cli";
    public override string DisplayName => "Install OpenClaw CLI";
    public override RetryPolicy Retry => new(MaxAttempts: 2, InitialDelay: TimeSpan.FromSeconds(5));

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        var user = ctx.Config.Wsl.User;

        // Download and run install script (URL configurable)
        var installUrl = ctx.Config.Gateway.InstallUrl ?? GatewayLkgVersion.DefaultInstallUrl;

        // Validate URL is HTTPS to prevent downgrade attacks
        if (!Uri.TryCreate(installUrl, UriKind.Absolute, out var parsedUrl) ||
            !string.Equals(parsedUrl.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return StepResult.Fail($"Installer URL must be HTTPS: {installUrl}");
        }

        string installScript;
        try
        {
            installScript = BuildInstallCommand(installUrl, ctx.Config.Gateway.Version);
        }
        catch (ArgumentException ex)
        {
            return StepResult.Fail(ex.Message);
        }

        var result = await ctx.Commands.RunInWslAsync(distro, installScript, TimeSpan.FromMinutes(5), ct: ct);

        if (result.ExitCode != 0)
            return StepResult.Fail($"CLI install failed (exit {result.ExitCode}): {result.Stderr}");

        var verifyCommands = new (string Command, string? ExecutablePath)[]
        {
            ("openclaw --version", null),
            ($"/home/{user}/.openclaw/bin/openclaw --version", $"/home/{user}/.openclaw/bin/openclaw"),
            ("/opt/openclaw/bin/openclaw --version", "/opt/openclaw/bin/openclaw"),
            ("/usr/local/bin/openclaw --version", "/usr/local/bin/openclaw")
        };

        foreach (var (cmd, executablePath) in verifyCommands)
        {
            var verify = await ctx.Commands.RunInWslAsync(distro, cmd, TimeSpan.FromSeconds(15), ct: ct);
            if (verify.ExitCode == 0 && !string.IsNullOrWhiteSpace(verify.Stdout))
            {
                if (executablePath != null)
                {
                    var pathResult = await EnsureCliOnDefaultPathAsync(ctx, distro, executablePath, ct);
                    if (!pathResult.IsSuccess)
                        return pathResult;
                }

                ctx.Logger.Info($"OpenClaw CLI version: {verify.Stdout.Trim()}");
                return StepResult.Ok($"CLI installed: {verify.Stdout.Trim()}");
            }
        }

        return StepResult.Fail("CLI installed but not found in any known location");
    }

    internal static string BuildInstallCommand(string installUrl, string? requestedVersion)
    {
        var escapedUrl = WslShellQuoting.EscapePosixSingleQuoteInner(installUrl);
        if (string.IsNullOrWhiteSpace(requestedVersion))
            return $"curl -fsSL --proto '=https' --tlsv1.2 '{escapedUrl}' | bash";

        var trimmedVersion = requestedVersion.Trim();
        if (trimmedVersion.Contains('\n') || trimmedVersion.Contains('\r'))
            throw new ArgumentException("Gateway version cannot contain newlines.");

        var escapedVersion = WslShellQuoting.EscapePosixSingleQuoteInner(trimmedVersion);
        return $"curl -fsSL --proto '=https' --tlsv1.2 '{escapedUrl}' | bash -s -- --version '{escapedVersion}'";
    }

    private static async Task<StepResult> EnsureCliOnDefaultPathAsync(
        SetupContext ctx,
        string distro,
        string executablePath,
        CancellationToken ct)
    {
        var user = ctx.Config.Wsl.User;

        if (!executablePath.StartsWith("/", StringComparison.Ordinal) ||
            executablePath.Contains('\'') ||
            executablePath.Contains('\n'))
        {
            return StepResult.Fail($"Refusing to create openclaw PATH symlink for unexpected install path: {executablePath}");
        }

        if (!string.Equals(executablePath, "/usr/local/bin/openclaw", StringComparison.Ordinal))
        {
            var linkCommand = $"""
                set -e
                ln -sfn {executablePath} /usr/local/bin/openclaw
                echo OPENCLAW_PATH_READY
                """;

            var link = await ctx.Commands.RunInWslAsync(
                distro,
                linkCommand,
                TimeSpan.FromSeconds(15),
                ct: ct,
                user: "root");

            if (link.ExitCode != 0 || !link.Stdout.Contains("OPENCLAW_PATH_READY", StringComparison.Ordinal))
                return StepResult.Fail($"Failed to make openclaw available on default PATH: {link.Stderr}");
        }

        var bareVerify = await ctx.Commands.RunInWslAsync(
            distro,
            $"env -i HOME=/home/{user} USER={user} PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin openclaw --version",
            TimeSpan.FromSeconds(15),
            ct: ct);

        if (bareVerify.ExitCode != 0 || string.IsNullOrWhiteSpace(bareVerify.Stdout))
            return StepResult.Fail($"openclaw PATH symlink verification failed: {bareVerify.Stderr}");

        ctx.Logger.Info($"OpenClaw CLI available on default PATH: {bareVerify.Stdout.Trim()}");
        return StepResult.Ok();
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        var user = ctx.Config.Wsl.User;
        await ctx.Commands.RunInWslAsync(ctx.DistroName!, $"rm -rf /opt/openclaw /home/{user}/.openclaw /usr/local/bin/openclaw", TimeSpan.FromSeconds(30), ct: ct, user: "root");
    }
}

public sealed class ConfigureGatewayStep : SetupStep
{
    internal const string DevicePairPublicUrlKey = "plugins.entries.device-pair.config.publicUrl";
    internal const string DevicePairEnabledKey = "plugins.entries.device-pair.enabled";
    // Each `openclaw config set` emitted below spawns the Node CLI fresh inside WSL; on a
    // newly created distro with a cold cache that is ~4-5s apiece. Budget the step by how
    // many config commands we actually emit -- BuildConfigCommands grows with the
    // device-pair keys and every Gateway.ExtraConfig entry -- with a floor so the minimal
    // path keeps generous headroom. A fixed cap silently regresses as the list grows.
    internal static readonly TimeSpan ConfigBaseBudget = TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan PerConfigCommandBudget = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan MinConfigurationTimeout = TimeSpan.FromSeconds(180);

    public override string Id => "configure-gateway";
    public override string DisplayName => "Configure gateway";

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        var port = ctx.Config.GatewayPort;
        var gw = ctx.Config.Gateway;

        // Validate bind value — only "loopback" and "lan" are accepted
        if (gw.Bind is not ("loopback" or "lan"))
            return StepResult.Terminal($"Invalid Gateway.Bind value '{gw.Bind}'. Must be 'loopback' or 'lan'.");

        // Generate a shared gateway token
        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        ctx.SharedGatewayToken = token;
        var env = new Dictionary<string, string> { ["OPENCLAW_GATEWAY_TOKEN"] = token };

        var allowedCommandsJson = JsonSerializer.Serialize(ctx.Config.Capabilities.GetEnabledCommandIds());
        var escapedAllowedCommands = WslShellQuoting.QuotePosixSingleQuote(allowedCommandsJson);
        var extraConfigOverridesAllowCommands = gw.ExtraConfig?.ContainsKey("gateway.nodes.allowCommands") == true;
        if (gw.ExtraConfig is { Count: > 0 })
        {
            foreach (var key in gw.ExtraConfig.Keys)
            {
                if (!IsSafeExtraConfigKey(key))
                    return StepResult.Fail($"Invalid Gateway.ExtraConfig key '{key}'. Keys may contain only letters, digits, '.', '_', and '-'.");
            }
        }

        var configCommands = BuildConfigCommands(gw, port, escapedAllowedCommands);

        ctx.Logger.Info($"Gateway node allowCommands derived from setup capabilities: {allowedCommandsJson}");
        if (extraConfigOverridesAllowCommands)
            ctx.Logger.Warn("Gateway.ExtraConfig overrides derived gateway.nodes.allowCommands");
        if (GetDefaultDevicePairPublicUrl(gw, port) is { } defaultPublicUrl &&
            gw.ExtraConfig?.ContainsKey(DevicePairPublicUrlKey) != true)
        {
            ctx.Logger.Info($"Configured device-pair public URL for loopback gateway: {defaultPublicUrl}");
        }

        var pathPrefix = ctx.WslPathPrefix;
        var script = $"""
            set -e
            {pathPrefix}
            
            {configCommands}
            
            echo "GATEWAY_CONFIGURED"
            """;

        var timeout = ComputeConfigurationTimeout(configCommands);
        var result = await ctx.Commands.RunInWslAsync(distro, script, timeout, env, ct);

        if (result.ExitCode != 0 || !result.Stdout.Contains("GATEWAY_CONFIGURED"))
        {
            if (result.TimedOut)
                return StepResult.Fail(
                    $"Gateway configuration timed out after {timeout.TotalSeconds:0}s while running openclaw config inside WSL.");

            return StepResult.Fail($"Gateway configuration failed (exit {result.ExitCode}): {result.Stderr}");
        }

        ctx.Logger.StateChange("shared_gateway_token", null, "[SET]");
        return StepResult.Ok("Gateway configured");
    }

    internal static string BuildConfigCommands(GatewayConfig gw, int port, string escapedAllowedCommands)
    {
        var configCommands = $"""
            openclaw config set gateway.mode local
            openclaw config set gateway.port {port}
            openclaw config set gateway.bind {gw.Bind}
            openclaw config set gateway.auth.mode {gw.AuthMode}
            openclaw config set gateway.auth.token "$OPENCLAW_GATEWAY_TOKEN"
            openclaw config set gateway.reload.mode {gw.ReloadMode}
            openclaw config set gateway.nodes.allowCommands {escapedAllowedCommands}
            """;

        if (GetDefaultDevicePairPublicUrl(gw, port) is { } defaultPublicUrl &&
            gw.ExtraConfig?.ContainsKey(DevicePairPublicUrlKey) != true)
        {
            configCommands += $"\n            openclaw config set {DevicePairPublicUrlKey} {WslShellQuoting.QuotePosixSingleQuote(defaultPublicUrl)}";
        }

        // The gateway ships the `device-pair` plugin bundled but DISABLED by default.
        // Without it, every scope-upgrade / role-upgrade WS connect (how OAuth providers like
        // Codex request the broader scopes needed to start their auth flow) hangs in
        // "pending approval" forever. The provider CLI errors out before ever printing its
        // verification URL, leaving the wizard stuck. Enable the plugin whenever we know how
        // to reach it (i.e. we either wrote the default loopback URL above, or the user
        // supplied their own publicUrl via ExtraConfig).
        var hasDevicePairPublicUrl =
            GetDefaultDevicePairPublicUrl(gw, port) is not null ||
            gw.ExtraConfig?.ContainsKey(DevicePairPublicUrlKey) == true;
        var devicePairExplicitlyConfigured =
            gw.ExtraConfig?.ContainsKey(DevicePairEnabledKey) == true;
        if (hasDevicePairPublicUrl && !devicePairExplicitlyConfigured)
        {
            configCommands += $"\n            openclaw config set {DevicePairEnabledKey} true";
        }

        // Apply any extra config key/value pairs from config (shell-escape values)
        if (gw.ExtraConfig is { Count: > 0 })
        {
            foreach (var (key, value) in gw.ExtraConfig)
            {
                if (!IsSafeExtraConfigKey(key))
                    throw new ArgumentException($"Invalid Gateway.ExtraConfig key '{key}'. Keys may contain only letters, digits, '.', '_', and '-'.", nameof(gw));

                var escapedValue = WslShellQuoting.QuotePosixSingleQuote(value);
                configCommands += $"\n            openclaw config set {key} {escapedValue}";
            }
        }

        return configCommands;
    }

    // Budget = base + per-command, floored. Scales the WSL timeout with the number of
    // `openclaw config set` invocations the step emits so it cannot silently regress as
    // BuildConfigCommands grows.
    internal static TimeSpan ComputeConfigurationTimeout(string configCommands)
    {
        var budget = ConfigBaseBudget + PerConfigCommandBudget * CountConfigSetCommands(configCommands);
        return budget > MinConfigurationTimeout ? budget : MinConfigurationTimeout;
    }

    private static int CountConfigSetCommands(string configCommands)
    {
        var count = 0;
        foreach (var line in configCommands.Split('\n'))
        {
            if (line.Contains("openclaw config set", StringComparison.Ordinal))
                count++;
        }

        return count;
    }

    internal static string? GetDefaultDevicePairPublicUrl(GatewayConfig gw, int port) =>
        gw.Bind == "loopback" ? $"http://127.0.0.1:{port}" : null;

    internal static bool IsSafeExtraConfigKey(string value)
        => System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9._-]+$");
}

public sealed class InstallGatewayServiceStep : SetupStep
{
    public override string Id => "install-service";
    public override string DisplayName => "Install gateway service";

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;

        var result = await ctx.Commands.RunInWslAsync(
            distro, $"{ctx.WslPathPrefix} && openclaw gateway install --force", TimeSpan.FromSeconds(60), ct: ct);

        if (result.ExitCode != 0)
            return StepResult.Fail($"Service install failed (exit {result.ExitCode}): {result.Stderr}");

        return StepResult.Ok("Gateway service installed");
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        await ctx.Commands.RunInWslAsync(ctx.DistroName!, $"{ctx.WslPathPrefix} && openclaw gateway uninstall", TimeSpan.FromSeconds(30), ct: ct);
    }
}

public sealed class StartGatewayStep : SetupStep
{
    public override string Id => "start-gateway";
    public override string DisplayName => "Start gateway";
    public override RetryPolicy Retry => new(MaxAttempts: 3, InitialDelay: TimeSpan.FromSeconds(3));

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        var pathCmd = ctx.WslPathPrefix;

        // Check for port conflicts before starting
        var portCheck = await ctx.Commands.RunInWslAsync(
            distro, $"ss -tlnp 2>/dev/null | grep ':{ctx.Config.GatewayPort}\\b' || true",
            TimeSpan.FromSeconds(10), ct: ct);

        if (!string.IsNullOrWhiteSpace(portCheck.Stdout) && portCheck.Stdout.Contains($":{ctx.Config.GatewayPort}"))
        {
            if (!portCheck.Stdout.Contains("openclaw", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Logger.Warn($"Port {ctx.Config.GatewayPort} is in use by another process:\n{portCheck.Stdout.Trim()}");
                return StepResult.Fail(
                    $"Port {ctx.Config.GatewayPort} is already in use by another process. Either stop the conflicting process or change GatewayPort in the setup config.");
            }

            ctx.Logger.Info($"Port {ctx.Config.GatewayPort} appears to be in use by openclaw — proceeding");
        }

        // Start the service
        var start = await ctx.Commands.RunInWslAsync(
            distro, $"{pathCmd} && openclaw gateway start", TimeSpan.FromSeconds(30), ct: ct);

        if (start.ExitCode != 0)
        {
            // Check if systemd start-limit-hit
            if (start.Stderr.Contains("start-limit", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Logger.Warn("Start-limit hit, resetting and retrying");
                await ctx.Commands.RunInWslAsync(
                    distro,
                    "systemctl --user reset-failed openclaw-gateway.service",
                    TimeSpan.FromSeconds(10),
                    ct: ct);
                await Task.Delay(2000, ct);
                start = await ctx.Commands.RunInWslAsync(distro, $"{pathCmd} && openclaw gateway start", TimeSpan.FromSeconds(30), ct: ct);
                if (start.ExitCode != 0)
                    return StepResult.Fail($"Gateway start failed after reset: {start.Stderr}");
            }
            else
            {
                return StepResult.Fail($"Gateway start failed (exit {start.ExitCode}): {start.Stderr}");
            }
        }

        // Wait for health endpoint
        ctx.Logger.Info("Waiting for gateway health endpoint...");
        var healthDeadline = DateTimeOffset.UtcNow.Add(TimeSpan.FromSeconds(ctx.Config.Gateway.HealthTimeoutSeconds));

        while (DateTimeOffset.UtcNow < healthDeadline)
        {
            ct.ThrowIfCancellationRequested();

            var status = await ctx.Commands.RunInWslAsync(
                distro, "curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:" + ctx.Config.GatewayPort + "/ --max-time 3",
                TimeSpan.FromSeconds(10), ct: ct);

            if (status.ExitCode == 0 && status.Stdout.Trim() is "200" or "401" or "403")
            {
                ctx.Logger.Info($"Gateway is accepting connections (HTTP {status.Stdout.Trim()})");
                return StepResult.Ok("Gateway running");
            }

            ctx.Logger.Debug($"Gateway not yet accepting connections (curl exit={status.ExitCode}, response={status.Stdout.Trim()})");

            await Task.Delay(2000, ct);
        }

        // Capture service status and journal for diagnostics
        var statusResult = await ctx.Commands.RunInWslAsync(
            distro,
            "systemctl --user status openclaw-gateway.service 2>&1 || true",
            TimeSpan.FromSeconds(10),
            ct: ct);

        var journal = await ctx.Commands.RunInWslAsync(
            distro,
            "journalctl --user-unit openclaw-gateway.service --no-pager -n 30 2>&1 || true",
            TimeSpan.FromSeconds(10),
            ct: ct);

        var redactedStatus = RedactTokens(statusResult.Stdout);
        var redactedJournal = RedactTokens(journal.Stdout);

        ctx.Logger.Error($"Gateway health timeout.\nService status:\n{redactedStatus}\nJournal:\n{redactedJournal}");

        return StepResult.Fail($"Gateway did not become healthy within {ctx.Config.Gateway.HealthTimeoutSeconds}s");
    }

    internal static string RedactTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return System.Text.RegularExpressions.Regex.Replace(
            text,
            @"[0-9a-fA-F]{32,}",
            m => m.Value[..8] + "…[REDACTED]");
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;

        // Check if distro is running before trying systemctl stop
        var list = await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--list", "--quiet"], TimeSpan.FromSeconds(15), ct: ct);
        if (!WslInstallSupport.ContainsDistro(list.Stdout, distro))
        {
            ctx.Logger.Info("[Uninstall] Distro not registered — skipping gateway stop");
            return;
        }

        // Check distro state — only stop if Running
        var verbose = await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--list", "--verbose"], TimeSpan.FromSeconds(15), ct: ct);
        var isRunning = WslInstallSupport.Normalize(verbose.Stdout)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Contains(distro, StringComparison.OrdinalIgnoreCase)
                      && line.Contains("Running", StringComparison.OrdinalIgnoreCase));

        if (!isRunning)
        {
            ctx.Logger.Info("[Uninstall] Distro not running — skipping systemctl stop");
            return;
        }

        // Stop gateway service with 5-second timeout (mirrors old uninstall step 3)
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await ctx.Commands.RunInWslAsync(
                distro, "bash -c 'systemctl --user stop openclaw-gateway 2>&1 || true'",
                TimeSpan.FromSeconds(10), ct: cts.Token);
            ctx.Logger.Info("[Uninstall] Stopped gateway service");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ctx.Logger.Warn("[Uninstall] systemctl stop timed out (5s); distro may be wedged — wsl --unregister will force-terminate");
        }
    }
}

// ═══════════════════════════════════════════════════════════════════
// PAIRING STEPS
// ═══════════════════════════════════════════════════════════════════

public sealed class MintBootstrapTokenStep : SetupStep
{
    public override string Id => "mint-token";
    public override string DisplayName => "Mint bootstrap token";

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;

        // Token was already set by ConfigureGatewayStep
        if (string.IsNullOrWhiteSpace(ctx.SharedGatewayToken))
            return StepResult.Fail("No shared gateway token set by previous step");

        // Mint a bootstrap/QR token
        var env = new Dictionary<string, string>
        {
            ["OPENCLAW_GATEWAY_TOKEN"] = ctx.SharedGatewayToken
        };

        var mint = await ctx.Commands.RunInWslAsync(
            distro, $"{ctx.WslPathPrefix} && openclaw qr --json", TimeSpan.FromSeconds(30), env, ct);

        if (mint.ExitCode == 0 && !string.IsNullOrWhiteSpace(mint.Stdout))
        {
            // Parse bootstrap token from JSON output
            try
            {
                if (TryReadBootstrapToken(mint.Stdout.Trim(), out var bootstrapToken, out var source))
                {
                    ctx.BootstrapToken = bootstrapToken;
                    ctx.Logger.StateChange("bootstrap_token", null, "[SET]");
                    return StepResult.Ok($"Bootstrap token minted from {source}");
                }
            }
            catch (JsonException ex)
            {
                ctx.Logger.Warn($"Failed to parse QR JSON: {ex.Message}");
            }
        }

        ctx.Logger.Warn("QR/bootstrap token mint failed or did not return a bootstrapToken/setupCode");
        return StepResult.Fail("Could not mint bootstrap token; refusing to use the shared gateway token as bootstrap.");
    }

    internal static bool TryReadBootstrapToken(string json, out string? token, out string? source)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var propertyName in new[] { "bootstrapToken", "setupCode" })
        {
            if (doc.RootElement.TryGetProperty(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(property.GetString()))
            {
                token = property.GetString();
                source = propertyName;
                return true;
            }
        }

        token = null;
        source = null;
        return false;
    }
}

internal static class WindowsGatewayReachability
{
    public static async Task<StepResult> VerifyAsync(SetupContext ctx, string pairingRole, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var resp = await http.GetAsync($"http://localhost:{ctx.Config.GatewayPort}/", ct);
            ctx.Logger.Debug($"Gateway health check: HTTP {(int)resp.StatusCode}");
            return StepResult.Ok();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"Gateway not reachable before {pairingRole} pairing: {ex.Message}");
        }
    }
}

public sealed class PairOperatorStep : SetupStep
{
    public override string Id => "pair-operator";
    public override string DisplayName => "Pair operator connection";
    public override RetryPolicy Retry => new(MaxAttempts: 3, InitialDelay: TimeSpan.FromSeconds(3));

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var gatewayUrl = ctx.GatewayUrl!;
        var token = ctx.SharedGatewayToken ?? ctx.BootstrapToken;

        if (string.IsNullOrEmpty(token))
            return StepResult.Terminal("No credential available for operator pairing");

        // Register gateway in registry (only once — reuse across retries)
        var registry = new GatewayRegistry(ctx.DataDir, logger: new SetupOpenClawLogger(ctx.Logger));
        registry.Load();

        string identityPath;
        if (!string.IsNullOrEmpty(ctx.GatewayRecordId))
        {
            var existing = registry.GetById(ctx.GatewayRecordId);
            if (existing == null)
                return StepResult.Fail($"Gateway record {ctx.GatewayRecordId} not found");
            identityPath = registry.GetIdentityDirectory(existing.Id);
            ctx.Logger.Info($"Reusing existing gateway record: id={existing.Id}");
        }
        else
        {
            var record = new GatewayRecord
            {
                Id = Guid.NewGuid().ToString("N")[..16],
                Url = gatewayUrl,
                FriendlyName = $"Local ({ctx.DistroName})",
                SharedGatewayToken = ctx.SharedGatewayToken,
                BootstrapToken = ctx.BootstrapToken,
                IsLocal = true,
                SetupManagedDistroName = ctx.DistroName,
                LastConnected = DateTime.UtcNow
            };

            record = registry.AddOrUpdate(record);
            registry.SetActive(record.Id);
            registry.Save();
            ctx.GatewayRecordId = record.Id;
            identityPath = registry.GetIdentityDirectory(record.Id);
            ctx.Logger.Info($"Gateway record created: id={record.Id}");
        }

        // Initialize device identity
        Directory.CreateDirectory(identityPath);
        var identity = new DeviceIdentity(identityPath);
        identity.Initialize();
        ctx.Logger.Info($"Device identity initialized: {identity.DeviceId[..16]}...");
        ctx.OperatorDeviceId = identity.DeviceId;

        var reachability = await WindowsGatewayReachability.VerifyAsync(ctx, "operator", ct);
        if (!reachability.IsSuccess)
            return reachability;

        // Connect operator WebSocket — handle pairing-required flow
        var wsLogger = new SetupOpenClawLogger(ctx.Logger);
        OpenClawGatewayClient? client = null;

        try
        {
            // Phase 1: Initial connect (may get PAIRING_REQUIRED)
            client = new OpenClawGatewayClient(gatewayUrl, token, logger: wsLogger, identityPath: identityPath);
            client.UseV2Signature = true; // Local gateway uses v2 signature format
            var phase1Result = await WaitForConnectionOrPairing(client, ctx, TimeSpan.FromSeconds(15), ct);

            if (phase1Result == ConnectionOutcome.Connected)
            {
                ctx.Logger.Info("Operator connected directly (no pairing needed)");
                return StepResult.Ok("Operator connected and paired");
            }

            if (phase1Result == ConnectionOutcome.PairingRequired)
            {
                if (!ctx.Config.AutoApprovePairing)
                    return StepResult.Fail("Pairing required but auto-approve is disabled");

                ctx.Logger.Info("Pairing required — auto-approving via CLI");
                var requestId = client.PairingRequiredRequestId;
                await client.DisconnectAsync();
                client.Dispose();
                client = null;

                // Auto-approve the pending pairing request
                var approveResult = await AutoApprovePairing(ctx, requestId, ct);
                if (!approveResult.IsSuccess)
                    return approveResult;

                // Wait for gateway to process the approval
                await Task.Delay(2000, ct);

                // Phase 2: Reconnect — the device should now be approved
                client = new OpenClawGatewayClient(gatewayUrl, token, logger: wsLogger, identityPath: identityPath);
                client.UseV2Signature = true;
                var phase2Result = await WaitForConnectionOrPairing(client, ctx, TimeSpan.FromSeconds(20), ct);

                if (phase2Result == ConnectionOutcome.Connected)
                {
                    ctx.Logger.Info("Operator paired successfully after approval");
                    // Disconnect before finalization
                    await client.DisconnectAsync();
                    client.Dispose();
                    client = null;

                    // Phase 3: Skip operator finalization here — it must happen AFTER node pairing.
                    // The node pairing changes the device's "current metadata" to node/node-host,
                    // so operator finalization (as cli/cli) must come last to match what the tray sends.
                    ctx.Logger.Info("Operator paired — finalization deferred to after node pairing");
                    return StepResult.Ok("Operator paired (finalization deferred)");
                }

                return StepResult.Fail($"Reconnection after approval failed: {phase2Result}");
            }

            return StepResult.Fail($"Operator connection failed: {phase1Result}");
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"Operator pairing failed: {ex.Message}", ex);
        }
        finally
        {
            if (client != null)
            {
                await client.DisconnectAsync();
                client.Dispose();
            }
        }
    }

    /// <summary>
    /// After initial pairing, the gateway knows us via auth.token (shared gateway token).
    /// The tray will connect using auth.deviceToken (the token we just received).
    /// This "finalizes" the transition so the gateway doesn't flag it as metadata-upgrade.
    /// </summary>
    private static async Task<StepResult> FinalizeWithDeviceToken(
        SetupContext ctx, string gatewayUrl, string identityPath, IOpenClawLogger wsLogger, CancellationToken ct)
    {
        ctx.Logger.Info("Finalizing: reconnect with device token (like tray will)");

        // Read the device token we just stored
        var identity = new DeviceIdentity(identityPath);
        identity.Initialize();
        var deviceToken = identity.DeviceToken;

        if (string.IsNullOrEmpty(deviceToken))
        {
            ctx.Logger.Warn("No device token stored after pairing — skipping finalization");
            return StepResult.Ok("Operator paired (no finalization needed)");
        }

        // Wait for the gateway's internal session grace period to expire.
        // Without this delay, the gateway accepts the deviceToken connect within grace
        // but would later reject the tray's identical connect as "metadata-upgrade".
        ctx.Logger.Info("Waiting for gateway grace period to expire before finalization...");
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        // Connect exactly as the tray would: pass deviceToken as the credential
        var finalClient = new OpenClawGatewayClient(gatewayUrl, deviceToken, logger: wsLogger, identityPath: identityPath);
        finalClient.UseV2Signature = true;

        try
        {
            var result = await WaitForConnectionOrPairing(finalClient, ctx, TimeSpan.FromSeconds(15), ct);

            if (result == ConnectionOutcome.Connected)
            {
                ctx.Logger.Info("Finalization connected — tray will connect seamlessly");
                return StepResult.Ok("Operator paired and finalized for tray");
            }

            if (result == ConnectionOutcome.PairingRequired)
            {
                ctx.Logger.Info("Metadata-upgrade detected during finalization — auto-approving");
                var requestId = finalClient.PairingRequiredRequestId;
                await finalClient.DisconnectAsync();
                finalClient.Dispose();
                finalClient = null;

                // Approve the metadata-upgrade
                var approveResult = await AutoApprovePairing(ctx, requestId, ct);
                if (!approveResult.IsSuccess)
                    return StepResult.Fail($"Finalization approval failed: {approveResult.Message}");

                await Task.Delay(2000, ct);

                // One more connect to confirm
                finalClient = new OpenClawGatewayClient(gatewayUrl, deviceToken, logger: wsLogger, identityPath: identityPath);
                finalClient.UseV2Signature = true;
                var finalResult = await WaitForConnectionOrPairing(finalClient, ctx, TimeSpan.FromSeconds(15), ct);

                if (finalResult == ConnectionOutcome.Connected)
                {
                    ctx.Logger.Info("Finalization approved — tray will connect seamlessly");
                    return StepResult.Ok("Operator paired and finalized for tray");
                }

                return StepResult.Fail($"Finalization failed after approval: {finalResult}");
            }

            return StepResult.Fail($"Finalization connect failed: {result}");
        }
        finally
        {
            if (finalClient != null)
            {
                await finalClient.DisconnectAsync();
                finalClient.Dispose();
            }
        }
    }

    internal static async Task<StepResult> AutoApprovePairing(SetupContext ctx, CancellationToken ct)
        => await AutoApprovePairing(ctx, requestId: null, ct);

    internal static async Task<StepResult> AutoApprovePairing(SetupContext ctx, string? requestId, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        var token = ctx.SharedGatewayToken ?? ctx.BootstrapToken ?? throw new InvalidOperationException("No gateway token available for auto-approve");

        var env = new Dictionary<string, string> { ["OPENCLAW_GATEWAY_TOKEN"] = token };

        if (string.IsNullOrWhiteSpace(requestId))
        {
            var preview = await ctx.Commands.RunInWslAsync(
                distro,
                $"""{ctx.WslPathPrefix} && openclaw devices approve --latest --json""",
                TimeSpan.FromSeconds(30), env, ct);

            ctx.Logger.Info($"Approve preview: exit={preview.ExitCode}");

            var parsed = ApprovalRequestHelper.TryReadSelectedRequestId(preview.Stdout.Trim());
            if (!parsed.Success)
            {
                ctx.Logger.Warn($"Could not select pairing request: {parsed.Error}");
                return StepResult.Fail("Could not find a safe pending pairing request to approve");
            }

            requestId = parsed.RequestId;
        }

        if (!ApprovalRequestHelper.IsSafeRequestId(requestId))
        {
            ctx.Logger.Warn("Refusing to approve pairing request with unsafe request ID");
            return StepResult.Fail("Pairing request ID contained unsafe characters");
        }

        ctx.Logger.Info($"Approving pairing request: {requestId}");
        var approvalEnv = ApprovalRequestHelper.AddRequestIdEnvironment(env, requestId!);

        var approve = await ctx.Commands.RunInWslAsync(
            distro,
            $"""{ctx.WslPathPrefix} && {ApprovalRequestHelper.ApprovalCommand(ApprovalRequestKind.Device)}""",
            TimeSpan.FromSeconds(30), approvalEnv, ct);

        ctx.Logger.Info($"Approve result: exit={approve.ExitCode}");

        if (approve.ExitCode != 0)
        {
            var approveOutput = approve.Stdout.Trim();
            if (ApprovalRequestHelper.IsPluginNotFoundError(approveOutput))
                return StepResult.Terminal(ApprovalRequestHelper.PluginNotFoundMessage);
            return StepResult.Fail($"Device approval failed (exit {approve.ExitCode}): {approveOutput}");
        }

        return StepResult.Ok($"Approved request {requestId}");
    }

    internal enum ConnectionOutcome { Connected, PairingRequired, Error, Timeout }

    internal static async Task<ConnectionOutcome> WaitForConnectionOrPairing(
        OpenClawGatewayClient client, SetupContext ctx, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ConnectionOutcome>();

        void OnStatusChanged(object? sender, ConnectionStatus status)
        {
            ctx.Logger.Debug($"Operator connection status: {status}");
            if (status == ConnectionStatus.Connected)
                tcs.TrySetResult(ConnectionOutcome.Connected);
            else if (status == ConnectionStatus.Error)
                tcs.TrySetResult(ConnectionOutcome.Error);
            else if (status == ConnectionStatus.Disconnected)
            {
                // Check if pairing was required — client sets IsPairingRequired before disconnect
                if (client.IsPairingRequired)
                    tcs.TrySetResult(ConnectionOutcome.PairingRequired);
                else
                    tcs.TrySetResult(ConnectionOutcome.Error);
            }
        }

        client.StatusChanged += OnStatusChanged;
        EventHandler<DeviceTokenReceivedEventArgs> onDeviceToken = (_, _) => ctx.Logger.Info("Device token received from gateway");
        client.DeviceTokenReceived += onDeviceToken;

        try
        {
            await client.ConnectAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ConnectionOutcome.Timeout;
        }
        catch (Exception ex)
        {
            ctx.Logger.Warn($"Operator connection failed: {ex.Message}");
            return ConnectionOutcome.Error;
        }
        finally
        {
            client.StatusChanged -= OnStatusChanged;
            client.DeviceTokenReceived -= onDeviceToken;
        }
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        var registry = new GatewayRegistry(ctx.DataDir, logger: new SetupOpenClawLogger(ctx.Logger));
        registry.Load();

        // Find all local gateway records to remove (mirrors old uninstall step 6a)
        var localRecords = registry.GetAll()
            .Where(r => IsSetupManagedLocalRecord(r, ctx))
            .ToList();

        if (localRecords.Count > 0)
        {
            foreach (var record in localRecords)
            {
                // Remove identity directory
                var identityDir = registry.GetIdentityDirectory(record.Id);
                if (Directory.Exists(identityDir))
                {
                    Directory.Delete(identityDir, recursive: true);
                    ctx.Logger.Info($"[Uninstall] Deleted identity directory: {identityDir}");
                }
                registry.Remove(record.Id);
            }
            registry.Save();
            ctx.Logger.Info($"[Uninstall] Removed {localRecords.Count} local gateway record(s)");
        }
        else
        {
            ctx.Logger.Info("[Uninstall] No local gateway records found");
        }

        // Null operator device token (mirrors old uninstall step 7)
        // Check if external gateways remain — if so, preserve root device tokens
        var hasExternalGateways = registry.GetAll().Any(r =>
            !r.IsLocal && !(r.SshTunnel is null && LocalGatewayUrlClassifier.IsLocalGatewayUrl(r.Url)));

        if (hasExternalGateways)
        {
            ctx.Logger.Info("[Uninstall] Preserving root device tokens — external gateway records remain");
        }
        else
        {
            var operatorCleared = DeviceIdentity.TryClearDeviceTokenForRole(ctx.DataDir, "operator");
            ctx.Logger.Info(operatorCleared
                ? "[Uninstall] Cleared operator device token"
                : "[Uninstall] Operator device token already absent");
        }

        // Best-effort revoke operator token via gateway HTTP endpoint (mirrors old step 4)
        await TryRevokeOperatorTokenAsync(ctx, ct);
    }

    internal static bool IsSetupManagedLocalRecord(GatewayRecord record, SetupContext ctx)
    {
        if (!record.IsLocal || record.SshTunnel != null)
            return false;

        if (string.Equals(record.SetupManagedDistroName, ctx.DistroName, StringComparison.Ordinal))
            return true;

        return string.IsNullOrWhiteSpace(record.SetupManagedDistroName)
            && string.Equals(record.Url, ctx.GatewayUrl, StringComparison.OrdinalIgnoreCase)
            && string.Equals(record.FriendlyName, $"Local ({ctx.DistroName})", StringComparison.Ordinal);
    }

    private static async Task TryRevokeOperatorTokenAsync(SetupContext ctx, CancellationToken ct)
    {
        try
        {
            // Read settings.json for legacy token if available
            var settingsPath = Path.Combine(ctx.DataDir, "settings.json");
            if (!File.Exists(settingsPath)) return;

            var settingsJson = await File.ReadAllTextAsync(settingsPath, ct);
            using var doc = JsonDocument.Parse(settingsJson);

            string? token = null;
            if (doc.RootElement.TryGetProperty("Token", out var tokenProp))
                token = tokenProp.GetString();

            if (string.IsNullOrWhiteSpace(token)) return;

            var gatewayUrl = ctx.GatewayUrl ?? "ws://localhost:18789";
            var httpBase = gatewayUrl
                .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)
                .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
                .TrimEnd('/');

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var response = await http.PostAsync($"{httpBase}/api/v1/operator/disconnect", content: null, cts.Token);
            ctx.Logger.Info($"[Uninstall] Revoke operator token: HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            ctx.Logger.Info($"[Uninstall] Best-effort token revoke failed ({ex.GetType().Name}); gateway may be down");
        }
    }
}

public sealed class PairNodeStep : SetupStep
{
    public override string Id => "pair-node";
    public override string DisplayName => "Pair node connection";
    public override RetryPolicy Retry => new(MaxAttempts: 3, InitialDelay: TimeSpan.FromSeconds(3));

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var gatewayUrl = ctx.GatewayUrl!;
        var token = ctx.SharedGatewayToken ?? ctx.BootstrapToken;

        if (string.IsNullOrEmpty(token))
            return StepResult.Terminal("No credential available for node pairing");

        var registry = new GatewayRegistry(ctx.DataDir, logger: new SetupOpenClawLogger(ctx.Logger));
        registry.Load();
        var record = registry.GetById(ctx.GatewayRecordId!);
        if (record == null)
            return StepResult.Fail("Gateway record not found in registry");

        var identityPath = registry.GetIdentityDirectory(record.Id);

        var reachability = await WindowsGatewayReachability.VerifyAsync(ctx, "node", ct);
        if (!reachability.IsSuccess)
            return reachability;

        var drainResult = await VerifyEndToEndStep.DrainPendingDeviceApprovalsAsync(ctx, ct);
        if (!drainResult.IsSuccess)
            return drainResult;

        var wsLogger = new SetupOpenClawLogger(ctx.Logger);
        WindowsNodeClient? client = null;

        try
        {
            // Phase 1: Connect (may get PAIRING_REQUIRED)
            client = new WindowsNodeClient(gatewayUrl, token, identityPath, logger: wsLogger);
            client.UseV2Signature = true;

            // Register capabilities BEFORE connect — gateway stores them from hello message
            RegisterCapabilitiesFromConfig(client, ctx);

            var outcome = await WaitForNodeConnection(client, ctx, TimeSpan.FromSeconds(15), ct);

            if (outcome.Outcome == NodeConnectionOutcome.Connected)
            {
                ctx.NodeDeviceId = client.ShortDeviceId;
                ctx.Logger.Info($"Node connected directly: {ctx.NodeDeviceId}");
                return StepResult.Ok("Node connected and paired");
            }

            if (outcome.Outcome == NodeConnectionOutcome.PairingRequired)
            {
                if (!ctx.Config.AutoApprovePairing)
                    return StepResult.Fail("Node pairing required but auto-approve is disabled");

                ctx.Logger.Info("Node pairing required — auto-approving via CLI");
                await client.DisconnectAsync();
                client.Dispose();
                client = null;

                var approveResult = await AutoApproveNodePairing(ctx, outcome.RequestId, ct);
                if (!approveResult.IsSuccess)
                    return approveResult;

                await Task.Delay(2000, ct);

                // Phase 2: Reconnect after approval
                client = new WindowsNodeClient(gatewayUrl, token, identityPath, logger: wsLogger);
                client.UseV2Signature = true;
                RegisterCapabilitiesFromConfig(client, ctx);

                outcome = await WaitForNodeConnection(client, ctx, TimeSpan.FromSeconds(20), ct);
                if (outcome.Outcome == NodeConnectionOutcome.Connected)
                {
                    ctx.NodeDeviceId = client.ShortDeviceId;
                    ctx.Logger.Info($"Node paired after approval: {ctx.NodeDeviceId}");
                    await client.DisconnectAsync();
                    client.Dispose();
                    client = null;

                    // Skip node finalization — the operator finalization in VerifyEndToEndStep
                    // will be the last connect, ensuring operator metadata is "current".
                    // Node finalization would rotate tokens and potentially invalidate the operator token.
                    ctx.Logger.Info("Node paired — skipping node finalization (operator finalization is last)");
                    return StepResult.Ok("Node paired successfully");
                }

                return StepResult.Fail($"Node reconnection after approval failed: {outcome.Outcome}");
            }

            return StepResult.Fail($"Node connection failed: {outcome.Outcome}");
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"Node pairing failed: {ex.Message}", ex);
        }
        finally
        {
            if (client != null)
            {
                await client.DisconnectAsync();
                client.Dispose();
            }
        }
    }

    /// <summary>
    /// After node pairing, finalize by connecting with the node device token to avoid
    /// metadata-upgrade when the tray reconnects.
    /// </summary>
    private static async Task<StepResult> FinalizeNodeWithDeviceToken(
        SetupContext ctx, string gatewayUrl, string identityPath, IOpenClawLogger wsLogger, CancellationToken ct)
    {
        ctx.Logger.Info("Finalizing node: reconnect with node device token");

        var identity = new DeviceIdentity(identityPath);
        identity.Initialize();
        var nodeToken = identity.NodeDeviceToken;

        if (string.IsNullOrEmpty(nodeToken))
        {
            ctx.Logger.Warn("No node device token stored after pairing — skipping node finalization");
            return StepResult.Ok("Node paired (no finalization needed)");
        }

        // Wait for grace period (same as operator finalization)
        ctx.Logger.Info("Waiting for gateway grace period before node finalization...");
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        var finalClient = new WindowsNodeClient(gatewayUrl, nodeToken, identityPath, logger: wsLogger);
        finalClient.UseV2Signature = true;

        try
        {
            var result = await WaitForNodeConnection(finalClient, ctx, TimeSpan.FromSeconds(15), ct);

            if (result.Outcome == NodeConnectionOutcome.Connected)
            {
                ctx.Logger.Info("Node finalization connected — tray will connect seamlessly");
                return StepResult.Ok("Node paired and finalized for tray");
            }

            if (result.Outcome == NodeConnectionOutcome.PairingRequired)
            {
                ctx.Logger.Info("Node metadata-upgrade detected — auto-approving");
                await finalClient.DisconnectAsync();
                finalClient.Dispose();
                finalClient = null;

                var approveResult = await AutoApproveNodePairing(ctx, result.RequestId, ct);
                if (!approveResult.IsSuccess)
                    return StepResult.Fail($"Node finalization approval failed: {approveResult.Message}");

                await Task.Delay(2000, ct);

                finalClient = new WindowsNodeClient(gatewayUrl, nodeToken, identityPath, logger: wsLogger);
                finalClient.UseV2Signature = true;
                var finalResult = await WaitForNodeConnection(finalClient, ctx, TimeSpan.FromSeconds(15), ct);

                if (finalResult.Outcome == NodeConnectionOutcome.Connected)
                {
                    ctx.Logger.Info("Node finalization approved — tray will connect seamlessly");
                    return StepResult.Ok("Node paired and finalized for tray");
                }

                return StepResult.Fail($"Node finalization failed after approval: {finalResult.Outcome}");
            }

            return StepResult.Fail($"Node finalization failed: {result.Outcome}");
        }
        finally
        {
            if (finalClient != null)
            {
                await finalClient.DisconnectAsync();
                finalClient.Dispose();
            }
        }
    }

    private enum NodeConnectionOutcome { Connected, PairingRequired, Error, Timeout }

    private sealed record NodeConnectionResult(NodeConnectionOutcome Outcome, string? RequestId = null);

    private static async Task<NodeConnectionResult> WaitForNodeConnection(
        WindowsNodeClient client, SetupContext ctx, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<NodeConnectionResult>();
        string? pairingRequestId = null;

        void OnStatusChanged(object? sender, ConnectionStatus status)
        {
            ctx.Logger.Debug($"Node connection status: {status}");
            if (status == ConnectionStatus.Connected)
                tcs.TrySetResult(new NodeConnectionResult(NodeConnectionOutcome.Connected));
            else if (status == ConnectionStatus.Error)
                tcs.TrySetResult(new NodeConnectionResult(NodeConnectionOutcome.Error));
            else if (status == ConnectionStatus.Disconnected)
            {
                if (client.IsPendingApproval)
                    tcs.TrySetResult(new NodeConnectionResult(NodeConnectionOutcome.PairingRequired, pairingRequestId));
                else
                    tcs.TrySetResult(new NodeConnectionResult(NodeConnectionOutcome.Error));
            }
        }

        void OnPairingStatusChanged(object? sender, PairingStatusEventArgs args)
        {
            if (args.Status == PairingStatus.Pending && ApprovalRequestHelper.IsSafeRequestId(args.RequestId))
                pairingRequestId = args.RequestId;
        }

        client.StatusChanged += OnStatusChanged;
        client.PairingStatusChanged += OnPairingStatusChanged;

        try
        {
            await client.ConnectAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return new NodeConnectionResult(NodeConnectionOutcome.Timeout);
        }
        finally
        {
            client.StatusChanged -= OnStatusChanged;
            client.PairingStatusChanged -= OnPairingStatusChanged;
        }
    }

    internal static async Task<StepResult> AutoApproveNodePairing(SetupContext ctx, string? requestId, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        var token = ctx.SharedGatewayToken ?? ctx.BootstrapToken ?? throw new InvalidOperationException("No gateway token available for auto-approve");

        var env = new Dictionary<string, string> { ["OPENCLAW_GATEWAY_TOKEN"] = token };
        var approvalKind = ApprovalRequestKind.Device;

        if (string.IsNullOrWhiteSpace(requestId))
        {
            approvalKind = ApprovalRequestKind.Node;
            var pending = await ctx.Commands.RunInWslAsync(
                distro,
                $"""{ctx.WslPathPrefix} && openclaw nodes list --json""",
                TimeSpan.FromSeconds(30), env, ct);

            ctx.Logger.Info($"Node pending list: exit={pending.ExitCode}");

            if (pending.ExitCode != 0)
            {
                var pendingOutput = pending.Stdout.Trim();
                if (ApprovalRequestHelper.IsPluginNotFoundError(pendingOutput))
                    return StepResult.Terminal(ApprovalRequestHelper.PluginNotFoundMessage);
                return StepResult.Fail($"Could not list pending node pairing requests (exit {pending.ExitCode}): {pendingOutput}");
            }

            var parsed = ApprovalRequestHelper.TryReadSinglePendingRequestId(pending.Stdout.Trim());
            if (!parsed.Success)
            {
                ctx.Logger.Warn($"Could not select node pairing request: {parsed.Error}");
                return StepResult.Fail(parsed.Error ?? "Could not find a safe pending node pairing request");
            }

            requestId = parsed.RequestId;
        }

        if (!ApprovalRequestHelper.IsSafeRequestId(requestId))
            return StepResult.Fail("Node pairing request ID contained unsafe characters");

        ctx.Logger.Info($"Approving node pairing request: {requestId}");
        var approvalEnv = ApprovalRequestHelper.AddRequestIdEnvironment(env, requestId!);

        var approve = await ctx.Commands.RunInWslAsync(
            distro,
            $"""{ctx.WslPathPrefix} && {ApprovalRequestHelper.ApprovalCommand(approvalKind)}""",
            TimeSpan.FromSeconds(30), approvalEnv, ct);

        ctx.Logger.Info($"Node approve result: exit={approve.ExitCode}");

        return approve.ExitCode == 0
            ? StepResult.Ok($"Node approved: {requestId}")
            : ApprovalRequestHelper.IsPluginNotFoundError(approve.Stdout.Trim())
                ? StepResult.Terminal(ApprovalRequestHelper.PluginNotFoundMessage)
                : StepResult.Fail($"Node approval failed (exit {approve.ExitCode}): {approve.Stdout.Trim()}");
    }

    private static void RegisterCapabilitiesFromConfig(WindowsNodeClient client, SetupContext ctx)
    {
        var capabilities = ctx.Config.Capabilities.GetEnabledCapabilities();
        foreach (var (category, commands) in capabilities)
        {
            client.RegisterCapability(new StubNodeCapability(category, commands));
        }
        if (ctx.Config.Settings.NodeCameraEnabled && ctx.Config.Capabilities.Camera)
            client.SetPermission("camera.capture", true);
        if (ctx.Config.Settings.NodeScreenEnabled && ctx.Config.Capabilities.Screen)
            client.SetPermission("screen.record", true);

        ctx.Logger.Info($"Registered {capabilities.Count} capability categories with {capabilities.Sum(c => c.Commands.Length)} total commands");
    }

    public override Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        // Null node device token (mirrors old uninstall step 7 for node role)
        // Only clear if no external gateways remain (same logic as PairOperatorStep)
        var registry = new GatewayRegistry(ctx.DataDir, logger: new SetupOpenClawLogger(ctx.Logger));
        registry.Load();
        var hasExternalGateways = registry.GetAll().Any(r =>
            !r.IsLocal && !(r.SshTunnel is null && LocalGatewayUrlClassifier.IsLocalGatewayUrl(r.Url)));

        if (hasExternalGateways)
        {
            ctx.Logger.Info("[Uninstall] Preserving node device token — external gateway records remain");
        }
        else
        {
            var nodeCleared = DeviceIdentity.TryClearDeviceTokenForRole(ctx.DataDir, "node");
            ctx.Logger.Info(nodeCleared
                ? "[Uninstall] Cleared node device token"
                : "[Uninstall] Node device token already absent");
        }

        return Task.CompletedTask;
    }
}

internal sealed record WindowsNodeContextTarget(string DistroName, string User, string WorkspacePath);

internal sealed class WindowsNodeContextInstallState
{
    public List<WindowsNodeContextTarget> Targets { get; set; } = [];
}

public sealed class WindowsNodeBootstrapContextStep : SetupStep
{
    private const string InstallStateFileName = "windows-node-context.json";
    private WindowsNodeContextTarget? _currentTarget;
    private bool _currentTargetWasNew;
    private bool _executeAttempted;

    public override string Id => "windows-node-context";
    public override string DisplayName => "Inject Windows node context";

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.WindowsNodeContext.Enabled;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        _executeAttempted = true;
        var distro = ctx.DistroName!;
        var user = ctx.Config.Wsl.User;
        var timeout = TimeSpan.FromSeconds(Math.Max(1, ctx.Config.WindowsNodeContext.TimeoutSeconds));

        var home = await ResolveLinuxHomeAsync(ctx, distro, user, ct);
        if (home is null)
            return StepResult.Fail("Could not resolve Linux home directory for openclaw user");

        // Resolve before baseline setup and pass the same absolute path to both
        // setup and injection. The managed gateway starts from this user's home,
        // so relative configured paths are home-relative rather than caller-cwd-relative.
        var workspace = await ResolveWorkspacePathAsync(ctx, distro, user, home, ct);
        if (string.IsNullOrWhiteSpace(workspace))
            return StepResult.Fail("Could not resolve OpenClaw agent workspace path");

        var workspaceOverride = ctx.Config.WindowsNodeContext.WorkspacePath?.Trim();
        var runBaselineSetup = !string.IsNullOrWhiteSpace(workspaceOverride);
        if (!runBaselineSetup)
        {
            var defaultWorkspace = await ResolveConfiguredDefaultWorkspacePathAsync(ctx, distro, user, home, ct);
            if (string.IsNullOrWhiteSpace(defaultWorkspace))
                return StepResult.Fail("Could not resolve OpenClaw default workspace path");

            runBaselineSetup = string.Equals(
                workspace.TrimEnd('/'),
                defaultWorkspace.TrimEnd('/'),
                StringComparison.Ordinal);
        }

        // Per-agent workspaces are already initialized by onboarding/agents add.
        // Running global setup for one would rewrite agents.defaults.workspace.
        if (runBaselineSetup)
        {
            var setupResult = await RunOpenclawSetupAsync(ctx, distro, user, workspace, ct);
            if (!setupResult.IsSuccess)
                return setupResult;
        }

        var target = new WindowsNodeContextTarget(distro, user, workspace);
        try
        {
            _currentTargetWasNew = await RecordAppliedTargetAsync(ctx, target, ct);
            _currentTarget = target;
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"Could not persist Windows node context install state: {ex.Message}", ex);
        }

        var script = BuildApplyScript(workspace);
        // Uses stdin to bypass wsl.exe argv variable-expansion (see docs/WSL_EXE_ARGV_PITFALL.md).
        var result = await ctx.Commands.RunInWslAsync(distro, script, timeout, ct: ct, user: user, inputViaStdin: true);

        if (result.ExitCode != 0 || !result.Stdout.Contains("WINDOWS_NODE_CONTEXT_READY", StringComparison.Ordinal))
        {
            if (_currentTargetWasNew && result.ExitCode is 2 or 4)
            {
                try
                {
                    await RemoveRecordedTargetAsync(ctx, target, ct);
                    _currentTarget = null;
                    _currentTargetWasNew = false;
                }
                catch (Exception ex)
                {
                    return StepResult.Fail(
                        $"Windows node context injection failed and install-state cleanup also failed: {ex.Message}",
                        ex);
                }
            }

            return StepResult.Fail($"Windows node context injection failed (exit {result.ExitCode}): {FirstNonEmpty(result.Stderr, result.Stdout)}");
        }

        ctx.Logger.Info($"Windows node context injected into workspace: {workspace}");
        return StepResult.Ok("Windows node context injected");
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, ctx.Config.WindowsNodeContext.TimeoutSeconds));
        var hasInstallState = File.Exists(InstallStatePath(ctx));
        WindowsNodeContextTarget[] targets;
        if (_currentTarget is { } current)
        {
            targets = [current];
        }
        else if (_executeAttempted)
        {
            // Failed-step rollback for an attempt that never modified a target.
            // Do not reinterpret this as a fresh uninstall of earlier installs.
            return;
        }
        else if (hasInstallState)
        {
            var state = await ReadInstallStateAsync(ctx, ct);
            targets = state.Targets.ToArray();
        }
        else
        {
            var legacyTarget = await ResolveLegacyUninstallTargetAsync(ctx, ct);
            targets = legacyTarget is null ? [] : [legacyTarget];
        }
        if (targets.Length == 0)
            return;

        var failures = new List<string>();
        foreach (var target in targets)
        {
            // Uses stdin to bypass wsl.exe argv variable-expansion (see docs/WSL_EXE_ARGV_PITFALL.md).
            var result = await ctx.Commands.RunInWslAsync(
                target.DistroName,
                BuildRollbackScript(target.WorkspacePath),
                timeout,
                ct: ct,
                user: target.User,
                inputViaStdin: true);

            if (result.ExitCode != 0 && !IsMissingDistroResult(result))
            {
                failures.Add(
                    $"{target.DistroName}:{target.WorkspacePath} (exit {result.ExitCode}): " +
                    FirstNonEmpty(result.Stderr, result.Stdout));
            }
        }

        if (failures.Count > 0)
            throw new InvalidOperationException("Windows node context cleanup failed: " + string.Join("; ", failures));

        if (_currentTarget is { } appliedTarget)
        {
            await RemoveRecordedTargetAsync(ctx, appliedTarget, ct);
        }
        else
        {
            File.Delete(InstallStatePath(ctx));
        }
    }

    private static async Task<WindowsNodeContextTarget?> ResolveLegacyUninstallTargetAsync(
        SetupContext ctx,
        CancellationToken ct)
    {
        var distro = ctx.DistroName;
        if (string.IsNullOrWhiteSpace(distro))
            return null;

        var user = ctx.Config.Wsl.User;
        var (home, result) = await QueryLinuxHomeAsync(ctx, distro, user, ct);
        if (home is null)
        {
            if (IsMissingDistroResult(result))
                return null;
            throw new InvalidOperationException(
                "Could not resolve Linux home directory while cleaning legacy Windows node context: " +
                FirstNonEmpty(result.Stderr, result.Stdout));
        }

        var workspace = await ResolveWorkspacePathAsync(ctx, distro, user, home, ct);
        if (string.IsNullOrWhiteSpace(workspace))
            throw new InvalidOperationException("Could not resolve workspace while cleaning legacy Windows node context");

        return new WindowsNodeContextTarget(distro, user, workspace);
    }

    internal static string InstallStatePath(SetupContext ctx) =>
        Path.Combine(ctx.LocalDataDir, InstallStateFileName);

    internal static async Task<bool> RecordAppliedTargetAsync(
        SetupContext ctx,
        WindowsNodeContextTarget target,
        CancellationToken ct)
    {
        var state = await ReadInstallStateAsync(ctx, ct);
        var exists = state.Targets.Contains(target);
        if (exists)
            return false;

        state.Targets.Add(target);
        var json = JsonSerializer.Serialize(state, SetupConfig.JsonWriteOptions);
        await AtomicFile.WriteAllTextAsync(InstallStatePath(ctx), json, ct);
        return true;
    }

    internal static async Task<WindowsNodeContextInstallState> ReadInstallStateAsync(
        SetupContext ctx,
        CancellationToken ct)
    {
        var path = InstallStatePath(ctx);
        if (!File.Exists(path))
            return new WindowsNodeContextInstallState();

        var json = await File.ReadAllTextAsync(path, ct);
        var state = JsonSerializer.Deserialize<WindowsNodeContextInstallState>(json, SetupConfig.JsonOptions)
            ?? throw new InvalidDataException("Windows node context install state is empty");
        if (state.Targets.Any(target =>
                string.IsNullOrWhiteSpace(target.DistroName) ||
                string.IsNullOrWhiteSpace(target.User) ||
                string.IsNullOrWhiteSpace(target.WorkspacePath) ||
                !target.WorkspacePath.StartsWith('/')))
        {
            throw new InvalidDataException("Windows node context install state contains an invalid target");
        }

        return state;
    }

    private static async Task RemoveRecordedTargetAsync(
        SetupContext ctx,
        WindowsNodeContextTarget target,
        CancellationToken ct)
    {
        var state = await ReadInstallStateAsync(ctx, ct);
        state.Targets.RemoveAll(candidate => candidate == target);
        if (state.Targets.Count == 0)
        {
            File.Delete(InstallStatePath(ctx));
            return;
        }

        var json = JsonSerializer.Serialize(state, SetupConfig.JsonWriteOptions);
        await AtomicFile.WriteAllTextAsync(InstallStatePath(ctx), json, ct);
    }

    internal static bool IsMissingDistroResult(CommandResult result)
    {
        if (result.ExitCode == 0)
            return false;

        var output = FirstNonEmpty(result.Stderr, result.Stdout);
        return output.Contains("There is no distribution with the supplied name", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("WSL_E_DISTRO_NOT_FOUND", StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<string?> ResolveLinuxHomeAsync(SetupContext ctx, string distro, string user, CancellationToken ct)
    {
        var (home, _) = await QueryLinuxHomeAsync(ctx, distro, user, ct);
        return home;
    }

    internal static async Task<(string? Home, CommandResult Result)> QueryLinuxHomeAsync(
        SetupContext ctx,
        string distro,
        string user,
        CancellationToken ct)
    {
        var result = await ctx.Commands.RunInWslAsync(
            distro,
            "getent passwd \"$(id -un)\" | cut -d: -f6",
            TimeSpan.FromSeconds(15),
            ct: ct,
            user: user);

        if (result.ExitCode != 0)
            return (null, result);

        var home = result.Stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0 && line.StartsWith('/'));

        return (string.IsNullOrWhiteSpace(home) ? null : home, result);
    }

    internal static async Task<StepResult> RunOpenclawSetupAsync(SetupContext ctx, string distro, string user, string workspaceAbsolute, CancellationToken ct)
    {
        var workspaceArg = WslShellQuoting.QuotePosixSingleQuote(workspaceAbsolute);

        // The pinned 2026.6.11 CLI uses plain `setup` for baseline initialization;
        // newer/custom CLIs require `--baseline`. Detect the installed contract.
        var script = $"""
            set -e
            {ctx.WslPathPrefix}
            if openclaw setup --help 2>&1 | grep -q -- '--baseline'; then
                openclaw setup --baseline --workspace {workspaceArg} >/dev/null
            else
                openclaw setup --workspace {workspaceArg} >/dev/null
            fi
            """;
        // Uses stdin to bypass wsl.exe argv variable-expansion (the script's
        // PATH prefix references $PATH, which would be expanded to the
        // Windows PATH on the argv path). See docs/WSL_EXE_ARGV_PITFALL.md.
        var result = await ctx.Commands.RunInWslAsync(
            distro,
            script,
            TimeSpan.FromSeconds(Math.Max(30, ctx.Config.WindowsNodeContext.TimeoutSeconds / 2)),
            ct: ct,
            user: user,
            inputViaStdin: true);

        if (result.ExitCode != 0)
            return StepResult.Fail($"openclaw setup failed (exit {result.ExitCode}): {FirstNonEmpty(result.Stderr, result.Stdout)}");

        return StepResult.Ok();
    }

    internal static async Task<string?> ResolveWorkspacePathAsync(SetupContext ctx, string distro, string user, string home, CancellationToken ct)
    {
        var workspaceOverride = ctx.Config.WindowsNodeContext.WorkspacePath?.Trim();
        if (!string.IsNullOrWhiteSpace(workspaceOverride))
            return ExpandLinuxPath(workspaceOverride, home);

        // `agents list` resolves per-agent overrides and returns the effective
        // workspace used by the default/main chat agent.
        var script = $"{ctx.WslPathPrefix}\nopenclaw agents list --json";
        // Uses stdin to bypass wsl.exe argv variable-expansion (the script's
        // PATH prefix references $PATH). See docs/WSL_EXE_ARGV_PITFALL.md.
        var result = await ctx.Commands.RunInWslAsync(
            distro,
            script,
            TimeSpan.FromSeconds(15),
            ct: ct,
            user: user,
            inputViaStdin: true);

        if (result.TimedOut || result.ExitCode != 0)
            return null;

        var raw = ExtractDefaultAgentWorkspaceFromAgentsOutput(result.Stdout);
        return string.IsNullOrWhiteSpace(raw) ? null : ExpandLinuxPath(raw, home);
    }

    internal static async Task<string?> ResolveConfiguredDefaultWorkspacePathAsync(
        SetupContext ctx,
        string distro,
        string user,
        string home,
        CancellationToken ct)
    {
        var script = $"{ctx.WslPathPrefix}\nopenclaw config get agents.defaults.workspace --json";
        var result = await ctx.Commands.RunInWslAsync(
            distro,
            script,
            TimeSpan.FromSeconds(15),
            ct: ct,
            user: user,
            inputViaStdin: true);

        if (result.TimedOut)
            return null;

        var raw = ExtractWorkspaceFromConfigOutput(result.Stdout);
        if (result.ExitCode != 0)
        {
            // Pinned 2026.6.11 reports an absent key with exit 1. Only that
            // known case may select the default; other read failures must not
            // be persisted by the subsequent `setup --workspace` call.
            if (!result.Stderr.Contains(
                    "Config path not found: agents.defaults.workspace",
                    StringComparison.Ordinal))
                return null;

            raw = $"{home.TrimEnd('/')}/.openclaw/workspace";
        }
        else if (string.IsNullOrWhiteSpace(raw))
        {
            // A present JSON null uses OpenClaw's default. Empty or malformed
            // successful output is an operational failure, not evidence that
            // the key is absent.
            if (!result.Stdout
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Any(line => string.Equals(line.Trim(), "null", StringComparison.Ordinal)))
                return null;

            raw = $"{home.TrimEnd('/')}/.openclaw/workspace";
        }

        return ExpandLinuxPath(raw, home);
    }

    internal static string? ExtractDefaultAgentWorkspaceFromAgentsOutput(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;

        var lines = stdout.Split(['\r', '\n'], StringSplitOptions.None);
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith('['))
                continue;

            var candidate = string.Join('\n', lines.Skip(i));
            var end = candidate.LastIndexOf(']');
            if (end < 0)
                continue;

            try
            {
                using var document = JsonDocument.Parse(candidate[..(end + 1)]);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    continue;

                JsonElement? main = null;
                foreach (var agent in document.RootElement.EnumerateArray())
                {
                    if (agent.ValueKind != JsonValueKind.Object)
                        continue;

                    if (agent.TryGetProperty("isDefault", out var isDefault) &&
                        isDefault.ValueKind == JsonValueKind.True)
                    {
                        main = agent;
                        break;
                    }

                    if (main is null &&
                        agent.TryGetProperty("id", out var id) &&
                        string.Equals(id.GetString(), "main", StringComparison.OrdinalIgnoreCase))
                        main = agent;
                }

                if (main is { } selected &&
                    selected.TryGetProperty("workspace", out var workspace) &&
                    workspace.ValueKind == JsonValueKind.String)
                    return workspace.GetString();
            }
            catch (JsonException)
            {
                // Keep scanning in case a warning line started with '['.
            }
        }

        return null;
    }

    internal static string? ExtractWorkspaceFromConfigOutput(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;

        // openclaw config get --json prints a JSON value; warnings may be on stderr (suppressed)
        // or as banner lines on stdout. Walk lines from bottom to find a usable value.
        var lines = stdout
            .Split(['\r', '\n'], StringSplitOptions.None)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var candidate = lines[i];
            // Try JSON string parse first
            if (candidate.StartsWith('"') && candidate.EndsWith('"'))
            {
                try
                {
                    return System.Text.Json.JsonSerializer.Deserialize<string>(candidate);
                }
                catch (System.Text.Json.JsonException)
                {
                    continue;
                }
            }

            if (candidate == "null")
                continue;

            // Plain string (non-JSON output)
            if (candidate.StartsWith('/') || candidate.StartsWith('~'))
                return candidate;
        }

        return null;
    }

    internal static string ExpandLinuxPath(string path, string home)
    {
        var trimmed = path.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed == "null" || trimmed == "undefined")
            return $"{home.TrimEnd('/')}/.openclaw/workspace";

        if (trimmed == "~")
            return home;
        if (trimmed.StartsWith("~/", StringComparison.Ordinal))
            return $"{home.TrimEnd('/')}/{trimmed[2..]}";
        if (trimmed.StartsWith('/'))
            return trimmed;
        return $"{home.TrimEnd('/')}/{trimmed}";
    }

    internal static string BuildApplyScript(string absoluteWorkspacePath)
        => $$"""
            set -e
            set -o pipefail
            workspace={{WslShellQuoting.QuotePosixSingleQuote(absoluteWorkspacePath)}}
            agents="$workspace/AGENTS.md"
            block_b64={{WslShellQuoting.QuotePosixSingleQuote(ManagedBlockBase64())}}
            begin_marker={{WslShellQuoting.QuotePosixSingleQuote(WindowsNodeContextSection.BeginMarker)}}
            end_marker={{WslShellQuoting.QuotePosixSingleQuote(WindowsNodeContextSection.EndMarker)}}
            if [ -L "$agents" ]; then
                echo "AGENTS_SYMLINK:$agents" >&2
                exit 2
            fi
            if [ ! -f "$agents" ]; then
                mkdir -p "$workspace"
                : > "$agents"
                echo "WINDOWS_NODE_CONTEXT_BOOTSTRAP_FALLBACK:$agents"
            fi
            begin_count=$(awk -v M="$begin_marker" '{ marker_line=$0; sub(/\r$/, "", marker_line); if (marker_line == M) count++ } END { print count + 0 }' "$agents")
            end_count=$(awk -v M="$end_marker" '{ marker_line=$0; sub(/\r$/, "", marker_line); if (marker_line == M) count++ } END { print count + 0 }' "$agents")
            if [ "$begin_count" -gt 1 ] || [ "$end_count" -gt 1 ] || [ "$begin_count" != "$end_count" ]; then
                echo "WINDOWS_NODE_CONTEXT_MARKERS_MALFORMED:$agents" >&2
                exit 4
            fi
            if [ "$begin_count" = 1 ]; then
                begin_line=$(awk -v M="$begin_marker" '{ marker_line=$0; sub(/\r$/, "", marker_line); if (marker_line == M) { print NR; exit } }' "$agents")
                end_line=$(awk -v M="$end_marker" '{ marker_line=$0; sub(/\r$/, "", marker_line); if (marker_line == M) { print NR; exit } }' "$agents")
                if [ "$end_line" -lt "$begin_line" ]; then
                    echo "WINDOWS_NODE_CONTEXT_MARKERS_MALFORMED:$agents" >&2
                    exit 4
                fi
            fi
            tmp=$(mktemp "$workspace/.AGENTS.md.openclaw.XXXXXX")
            trap 'rm -f -- "$tmp"' EXIT
            awk -v BEGIN_M="$begin_marker" -v END_M="$end_marker" '
              BEGIN { in_block = 0 }
              { marker_line = $0; sub(/\r$/, "", marker_line) }
              marker_line == BEGIN_M { in_block = 1; next }
              in_block && marker_line == END_M { in_block = 0; next }
              in_block { next }
              /^[[:space:]]*$/ { blank = blank $0 ORS; next }
              { printf "%s%s%s", blank, $0, ORS; blank = "" }
            ' "$agents" > "$tmp"
            if [ -s "$tmp" ]; then
                printf '\n' >> "$tmp"
            fi
            printf '%s' "$block_b64" | base64 -d >> "$tmp"
            printf '\n' >> "$tmp"
            chmod --reference="$agents" "$tmp"
            mv -- "$tmp" "$agents"
            trap - EXIT
            echo "WINDOWS_NODE_CONTEXT_WORKSPACE:$workspace"
            echo "WINDOWS_NODE_CONTEXT_READY"
            """;

    internal static string BuildRollbackScript(string absoluteWorkspacePath)
        => $$"""
            set -e
            set -o pipefail
            workspace={{WslShellQuoting.QuotePosixSingleQuote(absoluteWorkspacePath)}}
            agents="$workspace/AGENTS.md"
            begin_marker={{WslShellQuoting.QuotePosixSingleQuote(WindowsNodeContextSection.BeginMarker)}}
            end_marker={{WslShellQuoting.QuotePosixSingleQuote(WindowsNodeContextSection.EndMarker)}}
            if [ ! -e "$agents" ]; then
                echo "WINDOWS_NODE_CONTEXT_ABSENT"
                exit 0
            fi
            if [ -L "$agents" ]; then
                echo "AGENTS_SYMLINK_ROLLBACK_SKIPPED:$agents"
                exit 5
            fi
            begin_count=$(awk -v M="$begin_marker" '{ marker_line=$0; sub(/\r$/, "", marker_line); if (marker_line == M) count++ } END { print count + 0 }' "$agents")
            end_count=$(awk -v M="$end_marker" '{ marker_line=$0; sub(/\r$/, "", marker_line); if (marker_line == M) count++ } END { print count + 0 }' "$agents")
            if [ "$begin_count" = 0 ] && [ "$end_count" = 0 ]; then
                echo "WINDOWS_NODE_CONTEXT_REMOVED"
                exit 0
            fi
            if [ "$begin_count" != 1 ] || [ "$end_count" != 1 ]; then
                echo "WINDOWS_NODE_CONTEXT_MARKERS_MALFORMED:$agents" >&2
                exit 4
            fi
            begin_line=$(awk -v M="$begin_marker" '{ marker_line=$0; sub(/\r$/, "", marker_line); if (marker_line == M) { print NR; exit } }' "$agents")
            end_line=$(awk -v M="$end_marker" '{ marker_line=$0; sub(/\r$/, "", marker_line); if (marker_line == M) { print NR; exit } }' "$agents")
            if [ "$end_line" -lt "$begin_line" ]; then
                echo "WINDOWS_NODE_CONTEXT_MARKERS_MALFORMED:$agents" >&2
                exit 4
            fi
            tmp=$(mktemp "$workspace/.AGENTS.md.openclaw.XXXXXX")
            trap 'rm -f -- "$tmp"' EXIT
            awk -v BEGIN_M="$begin_marker" -v END_M="$end_marker" '
              BEGIN { in_block = 0 }
              { marker_line = $0; sub(/\r$/, "", marker_line) }
              marker_line == BEGIN_M { in_block = 1; next }
              in_block && marker_line == END_M { in_block = 0; next }
              in_block { next }
              { print }
            ' "$agents" > "$tmp"
            chmod --reference="$agents" "$tmp"
            mv -- "$tmp" "$agents"
            trap - EXIT
            echo "WINDOWS_NODE_CONTEXT_REMOVED"
            """;

    private static string ManagedBlockBase64()
        => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(WindowsNodeContextSection.ManagedBlock));

    private static string FirstNonEmpty(params string[] values)
        => values.Select(v => v.Trim()).FirstOrDefault(v => v.Length > 0) ?? "no output";

    private static string? ReadMarkerValue(string output, string marker)
        => output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith(marker, StringComparison.Ordinal))
            ?[marker.Length..];
}

public sealed class VerifyEndToEndStep : SetupStep
{
    public override string Id => "verify-e2e";
    public override string DisplayName => "Verify end-to-end connectivity";
    public override RetryPolicy Retry => new(MaxAttempts: 2, InitialDelay: TimeSpan.FromSeconds(3));

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        // Verify gateway is still healthy
        var distro = ctx.DistroName!;
        var status = await ctx.Commands.RunInWslAsync(
            distro, $"{ctx.WslPathPrefix} && openclaw gateway status --json", TimeSpan.FromSeconds(15), ct: ct);

        if (status.ExitCode != 0 || !status.Stdout.Contains("running", StringComparison.OrdinalIgnoreCase))
            return StepResult.Fail("Gateway is not running");

        // Verify registry state
        var registry = new GatewayRegistry(ctx.DataDir, logger: new SetupOpenClawLogger(ctx.Logger));
        registry.Load();
        var record = registry.GetById(ctx.GatewayRecordId!);
        if (record == null)
            return StepResult.Fail("Gateway record missing from registry");

        var identityPath = registry.GetIdentityDirectory(record.Id);
        if (!DeviceIdentity.HasStoredDeviceToken(identityPath))
        {
            ctx.Logger.Warn("No stored device token found — tray app may need to re-pair");
        }
        else
        {
            ctx.Logger.Info("Device token present — performing final operator handshake");

            // CRITICAL: The operator finalization must happen AFTER node pairing.
            // Node pairing changes the device's "current metadata" to node-host/node.
            // The tray connects as operator (cli/cli), so we must re-establish operator
            // as the device's last-seen metadata. This prevents "metadata-upgrade" errors.
            var wsLogger = new SetupOpenClawLogger(ctx.Logger);
            var finalResult = await FinalizeOperatorForTray(ctx, ctx.GatewayUrl!, identityPath, wsLogger, ct);
            if (!finalResult.IsSuccess)
                return finalResult;
        }

        // Write setup-state.json so tray knows the distro name for WSL keepalive
        await WriteSetupStateAsync(ctx, ct);

        // Write settings.json with EnableNodeMode + capability toggles from config
        WriteSettingsJson(ctx);

        // Drain any remaining pending approvals (device or node) so tray starts clean
        var drainResult = await DrainPendingApprovalsAsync(ctx, ct);
        if (!drainResult.IsSuccess)
            return drainResult;

        ClearPersistedBootstrapCredentials(ctx);

        return StepResult.Ok("Gateway running; operator finalized; settings written for tray.");
    }

    internal static async Task<StepResult> DrainPendingDeviceApprovalsAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        var token = ctx.SharedGatewayToken ?? ctx.BootstrapToken;
        if (string.IsNullOrWhiteSpace(token))
            return StepResult.Fail("No gateway token available to drain pending device approvals");

        var pathPrefix = ctx.WslPathPrefix;
        var env = new Dictionary<string, string> { ["OPENCLAW_GATEWAY_TOKEN"] = token };
        const int maxDrainIterations = 10;

        for (var i = 0; i < maxDrainIterations; i++)
        {
            var preview = await ctx.Commands.RunInWslAsync(
                distro,
                $"""{pathPrefix} && openclaw devices approve --latest --json""",
                TimeSpan.FromSeconds(15), env, ct);

            if (preview.Stdout.Contains("No pending", StringComparison.OrdinalIgnoreCase) ||
                preview.Stderr.Contains("No pending", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            var parsed = ApprovalRequestHelper.TryReadSelectedRequestId(preview.Stdout.Trim());
            if (parsed.Success)
            {
                ctx.Logger.Info($"Draining pending device approval: {parsed.RequestId}");
                var approvalEnv = ApprovalRequestHelper.AddRequestIdEnvironment(env, parsed.RequestId!);
                var approve = await ctx.Commands.RunInWslAsync(
                    distro,
                    $"""{pathPrefix} && {ApprovalRequestHelper.ApprovalCommand(ApprovalRequestKind.Device)}""",
                    TimeSpan.FromSeconds(15), approvalEnv, ct);

                if (approve.ExitCode != 0)
                    return StepResult.Fail($"Device approval drain failed for {parsed.RequestId} (exit {approve.ExitCode}): {approve.Stdout.Trim()} {approve.Stderr.Trim()}".Trim());

                if (i == maxDrainIterations - 1)
                    return StepResult.Fail("Device approval drain reached its iteration limit; pending approvals may remain");

                continue;
            }

            if (preview.ExitCode == 0)
            {
                var approved = ApprovalRequestHelper.TryReadApprovedRequestId(preview.Stdout.Trim());
                if (approved.Success)
                {
                    ctx.Logger.Info($"Drained pending device approval via latest command: {approved.RequestId}");
                    if (i == maxDrainIterations - 1)
                        return StepResult.Fail("Device approval drain reached its iteration limit; pending approvals may remain");

                    continue;
                }
            }

            return StepResult.Fail($"Could not select pending device approval for drain (exit {preview.ExitCode}): {parsed.Error ?? preview.Stderr.Trim()}");
        }

        return StepResult.Ok("Pending device approvals drained");
    }

    private static async Task<StepResult> DrainPendingApprovalsAsync(SetupContext ctx, CancellationToken ct)
    {
        var deviceDrainResult = await DrainPendingDeviceApprovalsAsync(ctx, ct);
        if (!deviceDrainResult.IsSuccess)
            return deviceDrainResult;

        var distro = ctx.DistroName!;
        var token = ctx.SharedGatewayToken ?? ctx.BootstrapToken;
        if (string.IsNullOrWhiteSpace(token))
            return StepResult.Fail("No gateway token available to drain pending approvals");

        var pathPrefix = ctx.WslPathPrefix;
        var env = new Dictionary<string, string> { ["OPENCLAW_GATEWAY_TOKEN"] = token };
        const int maxDrainIterations = 10;

        for (var i = 0; i < maxDrainIterations; i++)
        {
            var nodeList = await ctx.Commands.RunInWslAsync(
                distro,
                $"""{pathPrefix} && openclaw nodes list --json""",
                TimeSpan.FromSeconds(15), env, ct);

            var parsed = ApprovalRequestHelper.TryReadPendingRequestIds(nodeList.Stdout.Trim());
            if (!parsed.Success)
            {
                if (nodeList.ExitCode != 0)
                    return StepResult.Fail($"Could not list pending node approvals (exit {nodeList.ExitCode}): {nodeList.Stdout.Trim()} {nodeList.Stderr.Trim()}".Trim());

                return StepResult.Fail($"Could not parse pending node approvals: {parsed.Error}");
            }

            if (parsed.RequestIds.Count == 0)
                break;

            foreach (var requestId in parsed.RequestIds)
            {
                ctx.Logger.Info($"Draining pending node approval: {requestId}");
                var approvalEnv = ApprovalRequestHelper.AddRequestIdEnvironment(env, requestId);
                var approve = await ctx.Commands.RunInWslAsync(
                    distro,
                    $"""{pathPrefix} && {ApprovalRequestHelper.ApprovalCommand(ApprovalRequestKind.Node)}""",
                    TimeSpan.FromSeconds(15), approvalEnv, ct);

                if (approve.ExitCode != 0)
                    return StepResult.Fail($"Node approval drain failed for {requestId} (exit {approve.ExitCode}): {approve.Stdout.Trim()} {approve.Stderr.Trim()}".Trim());
            }

            if (i == maxDrainIterations - 1)
                return StepResult.Fail("Node approval drain reached its iteration limit; pending approvals may remain");
        }

        return StepResult.Ok("Pending approvals drained");
    }

    internal static void WriteSettingsJson(SetupContext ctx)
    {
        var settingsPath = Path.Combine(ctx.DataDir, "settings.json");
        ctx.Config.Settings.ApplyCapabilities(ctx.Config.Capabilities);
        ctx.Config.Settings.MergeIntoSettingsFile(settingsPath);
        ctx.Logger.Info($"Wrote settings.json: EnableNodeMode={ctx.Config.Settings.EnableNodeMode}");
    }

    private static void ClearPersistedBootstrapCredentials(SetupContext ctx)
    {
        if (string.IsNullOrWhiteSpace(ctx.GatewayRecordId))
            return;

        var registry = new GatewayRegistry(ctx.DataDir, logger: new SetupOpenClawLogger(ctx.Logger));
        registry.Load();
        var record = registry.GetById(ctx.GatewayRecordId);
        if (record is null)
            return;

        if (string.IsNullOrWhiteSpace(record.BootstrapToken))
        {
            return;
        }

        registry.AddOrUpdate(record with
        {
            BootstrapToken = null
        });
        registry.Save();
        ctx.Logger.Info("Cleared persisted bootstrap gateway credential after device pairing");
    }

    /// <summary>
    /// Final operator connect using device token — establishes operator/cli/cli as the
    /// device's "current metadata" so the tray can connect without metadata-upgrade.
    /// </summary>
    private static async Task<StepResult> FinalizeOperatorForTray(
        SetupContext ctx, string gatewayUrl, string identityPath, IOpenClawLogger wsLogger, CancellationToken ct)
    {
        var identity = new DeviceIdentity(identityPath);
        identity.Initialize();
        var deviceToken = identity.DeviceToken;

        if (string.IsNullOrEmpty(deviceToken))
            return StepResult.Fail("No device token available for operator finalization");

        // Wait for grace period to expire so this connect is treated as a real metadata change
        ctx.Logger.Info("Waiting for grace period before final operator handshake...");
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        var client = new OpenClawGatewayClient(gatewayUrl, deviceToken, logger: wsLogger, identityPath: identityPath);
        client.UseV2Signature = true;

        try
        {
            var result = await PairOperatorStep.WaitForConnectionOrPairing(client, ctx, TimeSpan.FromSeconds(15), ct);

            if (result == PairOperatorStep.ConnectionOutcome.Connected)
            {
                ctx.Logger.Info("Final operator handshake succeeded — tray will connect seamlessly");
                return StepResult.Ok("Operator finalized");
            }

            if (result == PairOperatorStep.ConnectionOutcome.PairingRequired)
            {
                ctx.Logger.Info("Metadata-upgrade detected — auto-approving for tray");
                await client.DisconnectAsync();
                client.Dispose();
                client = null;

                var approveResult = await PairOperatorStep.AutoApprovePairing(ctx, ct);
                if (!approveResult.IsSuccess)
                    return StepResult.Fail($"Operator finalization approval failed: {approveResult.Message}");

                await Task.Delay(2000, ct);

                // After approval, the gateway rotates the device token. The old one is invalid.
                // Clear the stale DeviceToken from the identity file so the client doesn't
                // try to use it (OpenClawGatewayClient prefers stored DeviceToken over constructor token).
                ctx.Logger.Info("Clearing stale operator device token from identity file");
                DeviceIdentity.TryClearDeviceToken(identityPath);

                // Reconnect with the SHARED GATEWAY TOKEN to get a fresh device token.
                ctx.Logger.Info("Reconnecting with shared token to get fresh device token after approval");
                client = new OpenClawGatewayClient(gatewayUrl, ctx.SharedGatewayToken!, logger: wsLogger, identityPath: identityPath);
                client.UseV2Signature = true;
                var confirmResult = await PairOperatorStep.WaitForConnectionOrPairing(client, ctx, TimeSpan.FromSeconds(15), ct);

                if (confirmResult == PairOperatorStep.ConnectionOutcome.Connected)
                {
                    ctx.Logger.Info("Operator finalization approved — fresh device token stored, tray will connect seamlessly");
                    return StepResult.Ok("Operator finalized after approval");
                }

                return StepResult.Fail($"Operator finalization failed after approval: {confirmResult}");
            }

            return StepResult.Fail($"Operator finalization failed: {result}");
        }
        finally
        {
            if (client != null)
            {
                await client.DisconnectAsync();
                client.Dispose();
            }
        }
    }

    private static async Task WriteSetupStateAsync(SetupContext ctx, CancellationToken ct)
    {
        var stateDir = ctx.LocalDataDir;
        Directory.CreateDirectory(stateDir);

        var statePath = Path.Combine(stateDir, "setup-state.json");
        // Phase and Status must be integers matching the tray's LocalGatewaySetupPhase/Status enums.
        // Phase.Complete = 13, Status.Complete = 7
        var state = new
        {
            SchemaVersion = 1,
            RunId = Guid.NewGuid().ToString("N"),
            InstallId = GetStableInstallId(ctx),
            Phase = 13,
            Status = 7,
            DistroName = ctx.DistroName,
            GatewayUrl = ctx.GatewayUrl,
            IsLocalOnly = true,
            FailureCode = (string?)null,
            UserMessage = (string?)null,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Issues = Array.Empty<object>(),
            History = Array.Empty<object>()
        };

        var json = System.Text.Json.JsonSerializer.Serialize(state, SetupConfig.JsonWriteOptions);
        await AtomicFile.WriteAllTextAsync(statePath, json, ct);
        ctx.Logger.Info($"Wrote setup-state.json: DistroName={ctx.DistroName}");
    }

    private static string GetStableInstallId(SetupContext ctx)
        => !string.IsNullOrWhiteSpace(ctx.GatewayRecordId)
            ? $"gateway:{ctx.GatewayRecordId}"
            : $"distro:{ctx.DistroName}";
}

// ─── Step 16: Start WSL Keepalive ───

public sealed class StartKeepaliveStep : SetupStep
{
    public override string Id => "start-keepalive";
    public override string DisplayName => "Start WSL keepalive";

    public override Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        ctx.Logger.Info($"Launching persistent keepalive for distro: {distro}");

        var markerPath = GetKeepaliveMarkerPath(ctx);
        if (TryGetExistingKeepalive(markerPath, distro, out var existingPid, new SetupOpenClawLogger(ctx.Logger)))
        {
            ctx.Logger.Info($"Keepalive already running for distro '{distro}' (PID {existingPid})");
            return Task.FromResult(StepResult.Ok("Keepalive already running"));
        }

        if (File.Exists(markerPath))
        {
            try { File.Delete(markerPath); }
            catch (Exception ex) { ctx.Logger.Debug($"[Keepalive] Stale marker delete failed: {ex.Message}"); }
        }

        // Launch detached keepalive process — keeps the distro alive so port forwarding
        // remains stable until the tray starts its own keepalive.
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = WslConstants.WslExePath,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(distro);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("sleep");
        psi.ArgumentList.Add("infinity");

        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc == null)
        {
            ctx.Logger.Warn("Failed to start keepalive process — tray will start its own");
            return Task.FromResult(StepResult.Ok());
        }

        ctx.Logger.Info($"Keepalive process started (PID {proc.Id}), distro will stay alive for tray launch");

        // Write keepalive marker so tray doesn't spawn a duplicate
        WriteKeepaliveMarker(ctx, markerPath, proc.Id);

        return Task.FromResult(StepResult.Ok());
    }

    private static void WriteKeepaliveMarker(SetupContext ctx, string markerPath, int pid)
    {
        var marker = new
        {
            DistroName = ctx.DistroName,
            Pid = pid,
            StartTimeUtc = DateTimeOffset.UtcNow,
            ProcessName = "wsl"
        };
        var json = System.Text.Json.JsonSerializer.Serialize(marker, SetupConfig.JsonWriteOptions);
        AtomicFile.WriteAllText(markerPath, json);
        ctx.Logger.Info($"Wrote keepalive marker: {markerPath}");
    }

    internal static string GetKeepaliveMarkerPath(SetupContext ctx)
        => Path.Combine(
            ctx.LocalDataDir, "wsl-keepalive", $"{ctx.DistroName}.json");

    internal static bool TryGetExistingKeepalive(string markerPath, string distro, out int pid, IOpenClawLogger? logger = null)
    {
        pid = 0;
        if (!File.Exists(markerPath))
            return false;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(markerPath));
            if (!doc.RootElement.TryGetProperty("Pid", out var pidElement) || !pidElement.TryGetInt32(out pid))
                return false;

            var process = System.Diagnostics.Process.GetProcessById(pid);
            using (process)
            {
                if (process.HasExited)
                    return false;

                return IsKeepaliveCommandLine(GetProcessCommandLine(pid, logger), distro);
            }
        }
        catch (Exception ex)
        {
            // TryGetExistingKeepalive returns false on any failure (file/process
            // missing or unreadable). Static method — no ctx.Logger available.
            // Debug-level via Trace so the failure is visible in dev diagnostics.
            System.Diagnostics.Trace.WriteLine($"[Keepalive] TryGetExistingKeepalive failed: {ex.Message}");
            pid = 0;
            return false;
        }
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName;
        if (string.IsNullOrEmpty(distro))
        {
            ctx.Logger.Info("[Uninstall] No distro name — skipping keepalive cleanup");
            return;
        }

        // Kill keepalive wsl.exe processes for this distro.
        // Pattern: wsl.exe -d <distro> -- sleep infinity
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("wsl")
                .Concat(System.Diagnostics.Process.GetProcessesByName("wsl.exe"));

            foreach (var proc in procs)
            {
                try
                {
                    // Read command line via WMI/CIM
                    var cmdLine = GetProcessCommandLine(proc.Id, new SetupOpenClawLogger(ctx.Logger));
                    if (IsKeepaliveCommandLine(cmdLine, distro))
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(5000);
                        ctx.Logger.Info($"[Uninstall] Killed keepalive process tree PID {proc.Id}");
                    }
                }
                catch (Exception ex) { ctx.Logger.Debug($"[Uninstall] Keepalive proc {proc.Id} cleanup skipped (may have exited): {ex.Message}"); }
                finally { proc.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.Warn($"[Uninstall] Error enumerating keepalive processes: {ex.Message}");
        }

        // Delete keepalive marker file
        var markerPath = GetKeepaliveMarkerPath(ctx);
        var markerDir = Path.GetDirectoryName(markerPath)!;

        if (File.Exists(markerPath))
        {
            File.Delete(markerPath);
            ctx.Logger.Info($"[Uninstall] Deleted keepalive marker: {markerPath}");
        }

        // Clean up empty marker directory
        if (Directory.Exists(markerDir) && !Directory.EnumerateFileSystemEntries(markerDir).Any())
        {
            Directory.Delete(markerDir);
            ctx.Logger.Info("[Uninstall] Deleted empty wsl-keepalive directory");
        }

        await Task.CompletedTask;
    }

    internal static bool IsKeepaliveCommandLine(string? commandLine, string distro)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(distro))
            return false;

        return WslCommandLineMatcher.IsKeepaliveForDistro(commandLine, distro);
    }

    private static string? GetProcessCommandLine(int pid, IOpenClawLogger? logger = null)
    {
        try
        {
            // Use WMI to get the command line
            var result = new System.Diagnostics.Process();
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                $"-NoProfile -Command \"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').CommandLine\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output.Trim();
        }
        catch (Exception ex)
        {
            SetupDiagnostics.TryWriteStderrWarning($"Failed to query command line for process {pid}: {ex.Message}");
            return null;
        }
    }
}

public sealed class RunGatewayWizardStep : SetupStep
{
    public override string Id => "run-wizard";
    public override string DisplayName => "Run gateway wizard";
    public override bool CanRetry => false;

    public override bool CanSkip(SetupContext ctx) => ctx.Config.SkipWizard;

    public override Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var runner = new SetupWizardRunner(ctx);
        return runner.RunAsync(ct);
    }
}
