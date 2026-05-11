namespace OpenClawTray.Services;

/// <summary>
/// Pure predicates that decide which optional node capabilities should be
/// advertised based on the user's <see cref="SettingsManager"/> flags.
///
/// Extracted from <c>NodeService.RegisterCapabilities</c> so the gating
/// rules can be unit-tested without standing up the full tray host. Both
/// the gateway client path and the MCP-only path read from the same
/// authoritative capability list, so a regression here would silently drop
/// or leak a capability across both surfaces.
///
/// Defaults: capabilities default ON (a missing or null settings object
/// counts as enabled) except <c>tts.speak</c> and <c>stt.transcribe</c>,
/// which are privacy-sensitive and require an explicit opt-in.
/// </summary>
internal static class NodeCapabilityGating
{
    public static bool ShouldRegisterCanvas(SettingsManager? s)       => s?.NodeCanvasEnabled       != false;
    public static bool ShouldRegisterScreen(SettingsManager? s)       => s?.NodeScreenEnabled       != false;
    public static bool ShouldRegisterCamera(SettingsManager? s)       => s?.NodeCameraEnabled       != false;
    public static bool ShouldRegisterLocation(SettingsManager? s)     => s?.NodeLocationEnabled     != false;
    public static bool ShouldRegisterBrowserProxy(SettingsManager? s) => s?.NodeBrowserProxyEnabled != false;
    public static bool ShouldRegisterTts(SettingsManager? s)          => s?.NodeTtsEnabled          == true;
    public static bool ShouldRegisterStt(SettingsManager? s)          => s?.NodeSttEnabled          == true;
}
