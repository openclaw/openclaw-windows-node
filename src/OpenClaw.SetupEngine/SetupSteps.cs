using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
// PAIRING STEPS
// ═══════════════════════════════════════════════════════════════════

internal static class SetupPairingCredentialPolicy
{
    // A durable device token does not exist until pairing completes. Initial
    // operator and node pairing must therefore use the shared token first,
    // with the one-time bootstrap credential as the fallback.
    public static string? ResolveInitialPairingToken(SetupContext ctx) =>
        ctx.SharedGatewayToken ?? ctx.BootstrapToken;
}

internal static class WindowsGatewayReachability
{
    public static async Task<StepResult> VerifyAsync(SetupContext ctx, string pairingRole, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var healthUri = BuildHealthUri(ctx.GatewayUrl!);
            var resp = await http.GetAsync(healthUri, ct);
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

    internal static Uri BuildHealthUri(string gatewayUrl)
    {
        var gatewayUri = new Uri(gatewayUrl);
        var scheme = gatewayUri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase)
            ? Uri.UriSchemeHttps
            : Uri.UriSchemeHttp;
        return new UriBuilder(gatewayUri) { Scheme = scheme, Port = gatewayUri.Port }.Uri;
    }
}
