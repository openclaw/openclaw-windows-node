using System.Runtime.InteropServices;
using Microsoft.Mxc.Sdk.V1;

namespace OpenClaw.Shared.Mxc;

/// <summary>SDK component readiness, process admission and independent OS session capability.</summary>
public sealed class MxcAvailability
{
    public bool IsAppContainerAvailable { get; }
    public bool IsIsolationSessionAvailable { get; }
    public bool? IsolationSessionCapability { get; }
    public bool IsSdkReady { get; }
    public bool ProbeSuppressedBySkuGate { get; }
    public bool ProbeErrored { get; }
    public string? IsolationTier { get; }
    public bool NeedsDaclAugmentation { get; }
    public bool SupportsProtectedPathDenies { get; }
    /// <summary>Positive OS verdict only. Load errors and request incompatibility never authorize host execution.</summary>
    public bool IsWindowsUnsupported { get; }
    public IReadOnlyList<string> Warnings { get; }
    public IReadOnlyList<string> UnsupportedReasons { get; }
    public bool CanRunSystemRunSandbox => IsSdkReady && IsAppContainerAvailable &&
        IsolationTier == "base-container" && !NeedsDaclAugmentation && SupportsProtectedPathDenies;
    public bool HasAnyBackend => IsSdkReady && (IsAppContainerAvailable || IsIsolationSessionAvailable);
    public IReadOnlyList<string> SystemRunSandboxUnsupportedReasons =>
        CanRunSystemRunSandbox ? [] : UnsupportedReasons.Count > 0 ? UnsupportedReasons :
        ["MXC BaseContainer without host ACL mutation is unavailable. Update Windows or repair the application. Uncertain or failed containment blocks commands."];

    public MxcAvailability(bool isAppContainerAvailable, bool isIsolationSessionAvailable,
        bool isSdkReady, IReadOnlyList<string> unsupportedReasons, bool probeErrored = false,
        string? isolationTier = null, bool needsDaclAugmentation = false,
        IReadOnlyList<string>? warnings = null, bool probeSuppressedBySkuGate = false,
        bool? isolationSessionCapability = null, bool supportsProtectedPathDenies = false,
        bool isWindowsUnsupported = false)
    {
        IsAppContainerAvailable = isAppContainerAvailable;
        IsIsolationSessionAvailable = isIsolationSessionAvailable;
        IsSdkReady = isSdkReady;
        UnsupportedReasons = unsupportedReasons;
        ProbeErrored = probeErrored;
        IsolationTier = isolationTier;
        NeedsDaclAugmentation = needsDaclAugmentation;
        Warnings = warnings ?? [];
        ProbeSuppressedBySkuGate = probeSuppressedBySkuGate;
        IsolationSessionCapability = isolationSessionCapability;
        SupportsProtectedPathDenies = supportsProtectedPathDenies;
        IsWindowsUnsupported = isWindowsUnsupported && !probeErrored;
    }

    public static MxcAvailability Probe(IOpenClawLogger? logger = null) =>
        Probe(logger, null, null, null);

    internal static MxcAvailability Probe(IOpenClawLogger? logger,
        Func<ProbeOutput>? sdkProbe, Func<bool?>? windowsServerProvider, Func<bool>? windowsProvider)
    {
        if (!(windowsProvider?.Invoke() ?? OperatingSystem.IsWindows()))
            return new(false, false, false, ["MXC requires Windows."]);
        var server = (windowsServerProvider ?? DetectWindowsServerSku)();
        if (server != false)
            return new(false, false, false,
                [server == true ? "Windows Server does not support OpenClaw MXC containment." :
                    "The Windows client SKU could not be verified. Commands are blocked."],
                probeErrored: server is null, probeSuppressedBySkuGate: true,
                isWindowsUnsupported: server == true);
        try
        {
            var probe = (sdkProbe ?? (() => MxcContainer.Probe()))();
            var session = probe.Probes.IsolationSessionAvailable;
            var tier = probe.Tier switch
            {
                Microsoft.Mxc.Sdk.V1.IsolationTier.BaseContainer => "base-container",
                Microsoft.Mxc.Sdk.V1.IsolationTier.AppContainerBfs => "appcontainer-bfs",
                Microsoft.Mxc.Sdk.V1.IsolationTier.AppContainerDacl => "appcontainer-dacl",
                _ => null,
            };
            (logger ?? NullLogger.Instance).Info(
                $"[mxc] operation=probe sdkReady=true processTier={tier ?? "unknown"} session={session} error={probe.Error is not null}");
            return new(probe.Tier is not null && probe.Error is null, session, true,
                probe.Error is null ? [] : ["The MXC native probe could not admit process containment. Update Windows or repair the application."],
                probeErrored: probe.Error is not null, isolationTier: tier,
                needsDaclAugmentation: probe.NeedsDaclAugmentation != false,
                warnings: probe.Warnings, isolationSessionCapability: session,
                supportsProtectedPathDenies: probe.Probes.BaseContainerSupportsDenyPaths,
                isWindowsUnsupported: probe.Error is null &&
                    !probe.Probes.BaseContainerApiPresent &&
                    probe.Tier is Microsoft.Mxc.Sdk.V1.IsolationTier.AppContainerBfs or Microsoft.Mxc.Sdk.V1.IsolationTier.AppContainerDacl);
        }
        catch (Exception ex) when (ex is MxcException or DllNotFoundException or EntryPointNotFoundException or
            BadImageFormatException or System.Text.Json.JsonException or TypeInitializationException)
        {
            (logger ?? NullLogger.Instance).Warn($"[mxc] operation=probe-failed error={ex.GetType().Name}");
            return new(false, false, false,
                ["The official MXC SDK native components could not be loaded or probed. Repair the installation and retry."],
                probeErrored: true);
        }
    }

    internal static bool? DetectWindowsServerSku()
    {
        try
        {
            var info = new OsVersionInfoEx
            {
                OsVersionInfoSize = (uint)Marshal.SizeOf<OsVersionInfoEx>(),
                ServicePack = "", ProductType = 1,
            };
            var mask = VerSetConditionMask(0, 0x80, 1);
            return VerifyVersionInfo(ref info, 0x80, mask) ? false :
                Marshal.GetLastWin32Error() == 1150 ? true : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or
            BadImageFormatException or MarshalDirectiveException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OsVersionInfoEx
    {
        public uint OsVersionInfoSize, MajorVersion, MinorVersion, BuildNumber, PlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? ServicePack;
        public ushort ServicePackMajor, ServicePackMinor, SuiteMask;
        public byte ProductType, Reserved;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VerifyVersionInfo(ref OsVersionInfoEx info, uint typeMask, ulong conditionMask);
    [DllImport("kernel32.dll")]
    private static extern ulong VerSetConditionMask(ulong mask, uint typeMask, byte condition);
}
