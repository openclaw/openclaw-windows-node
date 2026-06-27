#if OPENCLAW_TRAY_TESTS
namespace OpenClawTray.Helpers;

internal static class DiagnosticsGate
{
    public static bool BuildDefault => true;
    public static bool IsVisible => BuildDefault;
}
#else
using Microsoft.UI.Xaml;

namespace OpenClawTray.Helpers;

/// <summary>
/// Gates the Diagnostics page. The build sets the DEFAULT (shown on local/Debug
/// builds and unpackaged Release; hidden on the shipped MSIX). Users can override
/// the default via Settings (SettingsManager.ShowDiagnosticsOverride).
/// </summary>
internal static class DiagnosticsGate
{
    public static bool BuildDefault =>
#if DEBUG
        true;
#else
        !PackageHelper.IsPackaged;
#endif

    /// <summary>Effective visibility: user override if set, else the build default.</summary>
    public static bool IsVisible =>
        (Application.Current as OpenClawTray.App)?.SettingsOrNull?.ShowDiagnosticsEffective ?? BuildDefault;
}
#endif
