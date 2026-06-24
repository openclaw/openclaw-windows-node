using Microsoft.Win32;

namespace OpenClaw.Shared.Mxc;

/// <summary>
/// Per-backend availability probe for MXC. Cached for the process lifetime.
/// </summary>
/// <remarks>
/// Backends checked:
/// <list type="bullet">
/// <item><see cref="IsAppContainerAvailable"/> — Windows build &gt;= 26100 with UBR &gt;= 7965 (builds 26100–26500), x64 / arm64.</item>
/// <item><see cref="IsWxcExecResolvable"/> — wxc-exec.exe found in the shipped tray output layout or via override.</item>
/// <item><see cref="IsIsolationSessionAvailable"/> — requires AppContainer plus IsolationProxy.exe in System32.</item>
/// </list>
/// </remarks>
public sealed class MxcAvailability
{
    /// <summary>
    /// Optional override path for <c>wxc-exec.exe</c>. When set, used instead of
    /// probing the shipped <c>tools\mxc\&lt;arch&gt;\wxc-exec.exe</c> layout. Wired
    /// through environment variable <c>OPENCLAW_WXC_EXEC</c>.
    /// </summary>
    public const string WxcExecOverrideEnvVar = "OPENCLAW_WXC_EXEC";

    // Matches @microsoft/mxc-sdk platform.checkWindowsBuildVersion():
    // - Build >= 26100 required.
    // - Builds 26100–26500 require UBR >= 7965.
    // - Builds >= 26500 have no UBR minimum.
    private const int MinBuild = 26100;
    private const int MinSupportedUbr = 7965;
    private const int UbrRelaxedBuild = 26500;

    public bool IsAppContainerAvailable { get; }
    public bool IsIsolationSessionAvailable { get; }
    public bool IsWxcExecResolvable { get; }
    public string? WxcExecPath { get; }

    /// <summary>
    /// Human-readable list of reasons MXC may not be available. Empty when fully supported.
    /// Surface to UX so users know why the sandbox toggle is disabled.
    /// </summary>
    public IReadOnlyList<string> UnsupportedReasons { get; }

    /// <summary>True iff at least one MXC backend is supported AND
    /// <c>wxc-exec.exe</c> is resolvable. (Without wxc-exec the executor will refuse
    /// to run, so reporting "available" would lie to the UI.)</summary>
    public bool HasAnyBackend =>
        (IsAppContainerAvailable || IsIsolationSessionAvailable)
        && IsWxcExecResolvable;

    public MxcAvailability(
        bool isAppContainerAvailable,
        bool isIsolationSessionAvailable,
        bool isWxcExecResolvable,
        string? wxcExecPath,
        IReadOnlyList<string> unsupportedReasons)
    {
        IsAppContainerAvailable = isAppContainerAvailable;
        IsIsolationSessionAvailable = isIsolationSessionAvailable;
        IsWxcExecResolvable = isWxcExecResolvable;
        WxcExecPath = wxcExecPath;
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
            return new MxcAvailability(false, false, false, null, reasons);
        }

        var (build, ubr) = ReadWindowsBuildAndUbr();
        var windowsSupportReason = GetWindowsBuildUnsupportedReason(build, ubr);
        if (windowsSupportReason is not null)
            reasons.Add(windowsSupportReason);

        var isAppContainerSupported = windowsSupportReason is null;

        var (wxcResolvable, wxcPath) = ResolveWxcExec();
        if (!wxcResolvable)
            reasons.Add($"wxc-exec.exe not found. Set {WxcExecOverrideEnvVar} or build the tray app to copy it into the output folder.");

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
            $"reasons=[{string.Join(", ", reasons)}]");

        return new MxcAvailability(
            isAppContainerSupported,
            isIsolationSessionSupported,
            wxcResolvable,
            wxcPath,
            reasons);
    }

    internal static string? GetWindowsBuildUnsupportedReason(int build, int ubr)
    {
        if (build < MinBuild)
            return $"Windows build {build} is below MXC minimum {MinBuild}.";

        if (build < UbrRelaxedBuild && ubr < MinSupportedUbr)
        {
            return
                $"Windows UBR {ubr} below MXC minimum {MinSupportedUbr} " +
                $"(for build {build}). " +
                "Install latest cumulative update.";
        }

        return null;
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
        // slopwatch-ignore: SW003 Audited non-critical fallback is intentional and the caller preserves safe behavior without this work.
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

        var arch = GetSdkArchString();
        var probeRoots = new[]
        {
            AppContext.BaseDirectory,
            Path.GetDirectoryName(typeof(MxcAvailability).Assembly.Location) ?? string.Empty,
        };

        foreach (var root in probeRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            // Preferred: tools/mxc/<arch>/wxc-exec.exe — the layout the build
            // target extracts to so we don't ship a node_modules/ tree.
            var shipped = Path.Combine(root, "tools", "mxc", arch, "wxc-exec.exe");
            if (File.Exists(shipped))
                return (true, shipped);

            // Legacy fallback: developer builds with node_modules/ still around.
            var legacy = Path.Combine(
                root,
                "node_modules", "@microsoft", "mxc-sdk", "bin", arch, "wxc-exec.exe");
            if (File.Exists(legacy))
                return (true, legacy);
        }

        return (false, null);
    }

    /// <summary>Returns "arm64" or "x64" matching the <c>@microsoft/mxc-sdk</c> <c>bin/&lt;arch&gt;/</c> layout.</summary>
    private static string GetSdkArchString() => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        _ => "x64",
    };
}
