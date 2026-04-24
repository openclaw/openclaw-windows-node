using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace OpenClawTray.Infrastructure.Core;

/// <summary>
/// A reference to a WinUI theme resource that resolves at render time.
/// Use via <see cref="Theme"/> tokens or <see cref="Theme.Ref"/> for custom keys.
/// </summary>
public readonly record struct ThemeRef(string ResourceKey)
{
    public override string ToString() => $"ThemeRef({ResourceKey})";

    /// <summary>
    /// Resolves this theme reference using the element's actual theme.
    /// Walks the ThemeDictionaries in Application.Resources and MergedDictionaries
    /// to find the brush matching the element's effective theme (which respects
    /// per-element RequestedTheme overrides, not just the app-level theme).
    /// </summary>
    internal static Brush? Resolve(string resourceKey, FrameworkElement fe)
    {
        var themeName = GetEffectiveThemeName(fe);
        return ResolveForTheme(resourceKey, themeName);
    }

    /// <summary>
    /// Resolves a theme resource using an explicit isDark flag.
    /// Useful for resolving during Render() before controls are in the tree.
    /// </summary>
    public static Brush? Resolve(string resourceKey, bool isDark)
    {
        return ResolveForTheme(resourceKey, isDark ? "Dark" : "Light");
    }

    private static Brush? ResolveForTheme(string resourceKey, string themeName)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return null;

        // WinUI's XamlControlsResources ThemeDictionary keys vary by app configuration:
        //   Keys observed: "Default", "Light", "HighContrast" (no "Dark")
        // "Default" contains the base/dark brushes; "Light" contains light overrides.
        // Try the exact theme name first, then "Default" as the universal fallback.
        if (TryResolveFromThemeDictionaries(resources, resourceKey, themeName, out var brush))
            return brush;
        if (TryResolveFromThemeDictionaries(resources, resourceKey, "Default", out brush))
            return brush;

        // Fallback: non-themed resource lookup (including MergedDictionaries)
        if (TryResolveNonThemed(resources, resourceKey, out var fb))
            return fb;

        return null;
    }

    /// <summary>
    /// Determines the effective theme by walking up the visual tree looking for
    /// the nearest explicit RequestedTheme. This is more reliable than ActualTheme
    /// during reconciliation, because ActualTheme is a dependency property that
    /// may not have propagated yet within the same synchronous update pass.
    /// </summary>
    private static string GetEffectiveThemeName(FrameworkElement fe)
    {
        // Check the element's own RequestedTheme first
        if (fe.RequestedTheme != ElementTheme.Default)
            return fe.RequestedTheme == ElementTheme.Dark ? "Dark" : "Light";

        // Walk up the visual tree for the nearest override
        var parent = VisualTreeHelper.GetParent(fe) as FrameworkElement;
        while (parent is not null)
        {
            if (parent.RequestedTheme != ElementTheme.Default)
                return parent.RequestedTheme == ElementTheme.Dark ? "Dark" : "Light";
            parent = VisualTreeHelper.GetParent(parent) as FrameworkElement;
        }

        // No override found — check ActualTheme (reliable for elements already in the tree)
        if (fe.ActualTheme != ElementTheme.Default)
            return fe.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";

        // Final fallback: application theme
        return Application.Current?.RequestedTheme == ApplicationTheme.Dark ? "Dark" : "Light";
    }

    private static bool TryResolveFromThemeDictionaries(
        ResourceDictionary resources, string key, string themeName, out Brush? brush)
    {
        // Check this dictionary's ThemeDictionaries
        if (resources.ThemeDictionaries.TryGetValue(themeName, out var themeObj)
            && themeObj is ResourceDictionary themeDict
            && themeDict.TryGetValue(key, out var themed)
            && themed is Brush themedBrush)
        {
            brush = themedBrush;
            return true;
        }

        // Check MergedDictionaries (XamlControlsResources is added here)
        foreach (var merged in resources.MergedDictionaries)
        {
            if (TryResolveFromThemeDictionaries(merged, key, themeName, out brush))
                return true;
        }

        brush = null;
        return false;
    }

    private static bool TryResolveNonThemed(ResourceDictionary resources, string key, out Brush? brush)
    {
        if (resources.TryGetValue(key, out var value) && value is Brush b)
        {
            brush = b;
            return true;
        }

        foreach (var merged in resources.MergedDictionaries)
        {
            if (TryResolveNonThemed(merged, key, out brush))
                return true;
        }

        brush = null;
        return false;
    }
}

/// <summary>
/// Provides semantic theme tokens and custom resource references.
/// All tokens resolve from WinUI's resource system and automatically
/// adapt when the theme changes (Light ↔ Dark).
/// <para>
/// Usage: <c>Button("Go").Background(Theme.Accent)</c>
/// </para>
/// </summary>
public static class Theme
{
    // ── Accent / Fill ────────────────────────────────────────────────
    public static ThemeRef Accent            => new("AccentFillColorDefaultBrush");
    public static ThemeRef AccentSecondary   => new("AccentFillColorSecondaryBrush");
    public static ThemeRef AccentTertiary    => new("AccentFillColorTertiaryBrush");
    public static ThemeRef AccentDisabled    => new("AccentFillColorDisabledBrush");

    // ── Text ─────────────────────────────────────────────────────────
    public static ThemeRef PrimaryText       => new("TextFillColorPrimaryBrush");
    public static ThemeRef SecondaryText     => new("TextFillColorSecondaryBrush");
    public static ThemeRef TertiaryText      => new("TextFillColorTertiaryBrush");
    public static ThemeRef DisabledText      => new("TextFillColorDisabledBrush");
    public static ThemeRef AccentText        => new("AccentTextFillColorPrimaryBrush");

    // ── Surfaces / Fill ──────────────────────────────────────────────
    public static ThemeRef SolidBackground   => new("SolidBackgroundFillColorBaseBrush");
    public static ThemeRef CardBackground    => new("CardBackgroundFillColorDefaultBrush");
    public static ThemeRef SmokeFill         => new("SmokeFillColorDefaultBrush");
    public static ThemeRef SubtleFill        => new("SubtleFillColorSecondaryBrush");
    public static ThemeRef LayerFill         => new("LayerFillColorDefaultBrush");

    // ── Control Fill ─────────────────────────────────────────────────
    public static ThemeRef ControlFill              => new("ControlFillColorDefaultBrush");
    public static ThemeRef ControlFillSecondary     => new("ControlFillColorSecondaryBrush");
    public static ThemeRef ControlFillTertiary      => new("ControlFillColorTertiaryBrush");
    public static ThemeRef ControlFillDisabled      => new("ControlFillColorDisabledBrush");
    public static ThemeRef ControlFillInputActive   => new("ControlFillColorInputActiveBrush");

    // ── Stroke / Border ──────────────────────────────────────────────
    public static ThemeRef CardStroke        => new("CardStrokeColorDefaultBrush");
    public static ThemeRef SurfaceStroke     => new("SurfaceStrokeColorDefaultBrush");
    public static ThemeRef DividerStroke     => new("DividerStrokeColorDefaultBrush");
    public static ThemeRef ControlStroke     => new("ControlStrokeColorDefaultBrush");
    public static ThemeRef ControlStrokeSecondary => new("ControlStrokeColorSecondaryBrush");

    // ── Signal ───────────────────────────────────────────────────────
    public static ThemeRef SystemAttention   => new("SystemFillColorAttentionBrush");
    public static ThemeRef SystemSuccess     => new("SystemFillColorSuccessBrush");
    public static ThemeRef SystemCaution     => new("SystemFillColorCautionBrush");
    public static ThemeRef SystemCritical    => new("SystemFillColorCriticalBrush");
    public static ThemeRef SystemNeutral     => new("SystemFillColorNeutralBrush");
    public static ThemeRef SystemSolidNeutral => new("SystemFillColorSolidNeutralBrush");

    public static ThemeRef SystemAttentionBackground => new("SystemFillColorAttentionBackgroundBrush");
    public static ThemeRef SystemSuccessBackground   => new("SystemFillColorSuccessBackgroundBrush");
    public static ThemeRef SystemCautionBackground   => new("SystemFillColorCautionBackgroundBrush");
    public static ThemeRef SystemCriticalBackground  => new("SystemFillColorCriticalBackgroundBrush");
    public static ThemeRef SystemNeutralBackground   => new("SystemFillColorNeutralBackgroundBrush");
    public static ThemeRef SystemSolidAttention       => new("SystemFillColorSolidAttentionBackgroundBrush");

    // ── Custom resource reference ────────────────────────────────────
    /// <summary>
    /// Reference any WinUI theme resource by key name.
    /// The resource must exist in the WinUI resource tree
    /// (e.g., defined in XamlControlsResources or added via app resources).
    /// </summary>
    public static ThemeRef Ref(string resourceKey) => new(resourceKey);
}
