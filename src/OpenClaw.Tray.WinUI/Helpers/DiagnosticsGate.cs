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
/// Gates the Diagnostics page. Diagnostics remain visible by default for
/// compatibility with existing installs; users can explicitly hide or show them
/// via Settings (SettingsManager.ShowDiagnosticsOverride).
/// </summary>
internal static class DiagnosticsGate
{
    public static bool BuildDefault =>
        true;

    /// <summary>Effective visibility: user override if set, else the build default.</summary>
    public static bool IsVisible =>
        (Application.Current as OpenClawTray.App)?.SettingsOrNull?.ShowDiagnosticsEffective ?? BuildDefault;
}
#endif
