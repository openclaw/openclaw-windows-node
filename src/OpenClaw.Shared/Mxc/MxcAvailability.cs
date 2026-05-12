using Microsoft.Win32;

namespace OpenClaw.Shared.Mxc;

/// <summary>
/// Per-backend availability probe for MXC. Cached for the process lifetime.
/// </summary>
/// <remarks>
/// Backends checked:
/// <list type="bullet">
/// <item><see cref="IsAppContainerAvailable"/> — Windows 11 build &gt;= 26100, UBR &gt;= 7965 (per @microsoft/mxc-sdk README), x64 / arm64.</item>
/// <item><see cref="IsWxcExecResolvable"/> — wxc-exec.exe found at the expected node_modules path or via override.</item>
/// <item><see cref="IsIsolationSessionAvailable"/> — requires AppContainer plus IsolationProxy.exe in System32.</item>
/// </list>
/// </remarks>
public sealed class MxcAvailability
{
    /// <summary>
    /// Optional override path for <c>wxc-exec.exe</c>. When set, used instead of the
    /// default <c>node_modules/@microsoft/mxc-sdk/bin/&lt;arch&gt;/wxc-exec.exe</c> probe.
    /// Wired through environment variable <c>OPENCLAW_WXC_EXEC</c>.
    /// </summary>
    public const string WxcExecOverrideEnvVar = "OPENCLAW_WXC_EXEC";

    private const int MinSupportedBuild = 26100;

    /// <summary>
    /// Highest base build for which an explicit UBR floor applies. Per the
    /// @microsoft/mxc-sdk README, builds in <c>[26100, 26500]</c> require the
    /// cumulative update bringing UBR ≥ 7965; builds beyond 26500 (e.g.
    /// Canary / Dev channels) ship the feature natively.
    /// </summary>
    private const int UbrCheckMaxBuild = 26500;
    private const int MinSupportedUbrInRange = 7965;

    public bool IsAppContainerAvailable { get; }
    public bool IsIsolationSessionAvailable { get; }
    public bool IsWxcExecResolvable { get; }
    public string? WxcExecPath { get; }

    /// <summary>
    /// Resolved path to <c>tools/mxc/run-command.cjs</c> (the productized Node bridge
    /// for MxcCommandRunner). The tray build copies this under the app base
    /// directory; probing intentionally does not walk parent directories so a
    /// user-writable parent cannot inject a replacement bridge.
    /// </summary>
    public string? RunCommandScriptPath { get; }

    /// <summary>
    /// Human-readable list of reasons MXC may not be available. Empty when fully supported.
    /// Surface to UX so users know why the sandbox toggle is disabled.
    /// </summary>
    public IReadOnlyList<string> UnsupportedReasons { get; }

    /// <summary>True iff at least one MXC backend is supported, the bridge script is found,
    /// AND <c>wxc-exec.exe</c> is resolvable. (Without wxc-exec the executor will refuse
    /// to run, so reporting "available" would lie to the UI.)</summary>
    public bool HasAnyBackend =>
        (IsAppContainerAvailable || IsIsolationSessionAvailable)
        && RunCommandScriptPath is not null
        && IsWxcExecResolvable;

    public MxcAvailability(
        bool isAppContainerAvailable,
        bool isIsolationSessionAvailable,
        bool isWxcExecResolvable,
        string? wxcExecPath,
        string? runCommandScriptPath,
        IReadOnlyList<string> unsupportedReasons)
    {
        IsAppContainerAvailable = isAppContainerAvailable;
        IsIsolationSessionAvailable = isIsolationSessionAvailable;
        IsWxcExecResolvable = isWxcExecResolvable;
        WxcExecPath = wxcExecPath;
        RunCommandScriptPath = runCommandScriptPath;
        UnsupportedReasons = unsupportedReasons;
    }

    /// <summary>
    /// Probe the running environment. Designed to be called once at app startup
    /// and the result cached.
    /// </summary>
    public static MxcAvailability Probe(IOpenClawLogger? logger = null)
    {
        var log = logger ?? NullLogger.Instance;
        var reasons = new List<string>();

        if (!OperatingSystem.IsWindows())
        {
            reasons.Add("MXC requires Windows.");
            return new MxcAvailability(false, false, false, null, null, reasons);
        }

        var (build, ubr) = ReadWindowsBuildAndUbr();
        var buildOk = build >= MinSupportedBuild;
        // UBR floor only applies to builds in the [26100, 26500] window;
        // newer Canary / Dev builds ship MXC primitives natively.
        var ubrCheckApplies = build <= UbrCheckMaxBuild;
        var ubrOk = !ubrCheckApplies || ubr >= MinSupportedUbrInRange;
        if (!buildOk)
            reasons.Add($"Windows build {build} below MXC minimum {MinSupportedBuild}.");
        if (buildOk && !ubrOk)
            reasons.Add(
                $"Windows UBR {ubr} below MXC minimum {MinSupportedUbrInRange} " +
                $"(for builds {MinSupportedBuild}-{UbrCheckMaxBuild}). " +
                "Install latest cumulative update.");

        var isAppContainerSupported = buildOk && ubrOk;

        var (wxcResolvable, wxcPath) = ResolveWxcExec();
        if (!wxcResolvable)
            reasons.Add($"wxc-exec.exe not found. Set {WxcExecOverrideEnvVar} or run `npm ci` at the repository root.");

        var runCommandScriptPath = ResolveRunCommandScript();
        if (runCommandScriptPath is null)
            reasons.Add("tools/mxc/run-command.cjs not found in any expected location.");

        // isolation_session additionally requires Feature_IsoBrokerSessionApis on the OS
        // and IsolationProxy.exe in System32. We currently only check file presence.
        var isolationProxyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "IsolationProxy.exe");
        var isIsolationSessionSupported = isAppContainerSupported
            && wxcResolvable
            && File.Exists(isolationProxyPath);

        log.Info(
            $"[mxc] availability: appcontainer={isAppContainerSupported} " +
            $"isolation_session={isIsolationSessionSupported} " +
            $"wxc-exec={(wxcResolvable ? wxcPath : "<missing>")} " +
            $"run-command.cjs={(runCommandScriptPath ?? "<missing>")} " +
            $"reasons=[{string.Join(", ", reasons)}]");

        return new MxcAvailability(
            isAppContainerSupported,
            isIsolationSessionSupported,
            wxcResolvable,
            wxcPath,
            runCommandScriptPath,
            reasons);
    }

    private static (int build, int ubr) ReadWindowsBuildAndUbr()
    {
        var build = Environment.OSVersion.Version.Build;
        var ubr = 0;
        if (!OperatingSystem.IsWindows())
            return (build, ubr);

        try
        {
#pragma warning disable CA1416 // OperatingSystem.IsWindows() guard above; analyzer doesn't recognize it through callee.
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var value = key?.GetValue("UBR");
            if (value is int ubrInt)
                ubr = ubrInt;
#pragma warning restore CA1416
        }
        catch
        {
            // Best-effort registry read; failure leaves ubr = 0 which fails the gate.
        }

        return (build, ubr);
    }

    private static (bool resolvable, string? path) ResolveWxcExec()
    {
        var overridePath = Environment.GetEnvironmentVariable(WxcExecOverrideEnvVar);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return (true, overridePath);

        var arch = MxcArchHelper.GetSdkArchString();
        var probeRoots = new[]
        {
            AppContext.BaseDirectory,
            Path.GetDirectoryName(typeof(MxcAvailability).Assembly.Location) ?? string.Empty,
        };

        foreach (var root in probeRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            var candidate = Path.Combine(
                root,
                "node_modules", "@microsoft", "mxc-sdk", "bin", arch, "wxc-exec.exe");
            if (File.Exists(candidate))
                return (true, candidate);
        }

        return (false, null);
    }

    private static string? ResolveRunCommandScript()
    {
        var probeRoots = new[]
        {
            AppContext.BaseDirectory,
            Path.GetDirectoryName(typeof(MxcAvailability).Assembly.Location) ?? string.Empty,
        };

        foreach (var root in probeRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            var candidate = Path.Combine(root, "tools", "mxc", "run-command.cjs");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}

internal static class MxcArchHelper
{
    /// <summary>Returns "arm64" or "x64" matching the @microsoft/mxc-sdk bin/&lt;arch&gt;/ layout.</summary>
    public static string GetSdkArchString() => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        _ => "x64",
    };
}
