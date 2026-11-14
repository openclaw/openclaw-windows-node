using OpenClaw.Shared.Themes;
using OpenClawTray.Services;

namespace OpenClawTray.Theming;

/// <summary>
/// The branding of the applied theme, read by brand spots and the chat working indicator. AppThemeApplicator
/// sets it on the UI thread whenever the theme changes; the Windows default is <see cref="ThemeBranding.Default"/>.
/// The event is static: subscribers unsubscribe when they unload, or hold themselves weakly (see BrandMark).
/// <example><code>
/// ThemeBrandingState.Changed += OnBrandingChanged;
/// var showMark = !ThemeBrandingState.Current.ShowsClaw;
/// </code></example>
/// </summary>
internal static class ThemeBrandingState
{
    public static ThemeBranding Current { get; private set; } = ThemeBranding.Default;

    /// <summary>Raised on the UI thread after <see cref="Current"/> changes.</summary>
    public static event EventHandler? Changed;

    public static void Set(ThemeBranding branding)
    {
        if (branding == Current)
            return;
        Current = branding;
        // One failing brand spot must not stop the others or the theme being applied.
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler)handler)(null, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Themes] A branding subscriber failed: {ex.GetType().Name} {ex.Message}");
            }
        }
    }
}
