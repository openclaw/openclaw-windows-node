using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared.Themes;
using OpenClawTray.Chat;
using OpenClawTray.Services;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace OpenClawTray.Theming;

/// <summary>
/// Applies a Gateway theme to the application's WinUI resources and restores Fluent when cleared.
/// Shared SolidColorBrush instances are recolored in place: the app's own Default (dark) and Light theme
/// dictionary entries, and the framework brush each theme's entry in WinUI's own theme dictionary holds (the one its
/// elements render). Every consumer, including framework control templates that alias them, updates at once.
/// Restoring writes back each brush's color as resolved under its own dictionary's theme.
/// A read-only framework brush in the theme dictionary of the app's own mode is replaced once by a writable copy that
/// the theme recolors, because elements that resolved it never read an application-root override; the other mode's
/// read-only brushes and keys no theme defines get an application-root override. High Contrast resources
/// are never touched, so Windows High Contrast keeps winning. UI thread only.
/// <example><code>
/// if (applicator.Apply(AppearanceResolver.ResolveTheme(settings.Appearance)))
///     RefreshThemeResourceConsumers();
/// </code></example>
/// </summary>
internal sealed class AppThemeApplicator
{
    private static readonly (string Key, ThemeColorMode Mode)[] s_themeDictionaries =
        [("Default", ThemeColorMode.Dark), ("Light", ThemeColorMode.Light)];

    // Windows accent shades and the projection keys of the matching theme shades.
    private static readonly (UIColorType Type, string Key)[] s_accentShades =
    [
        (UIColorType.Accent, "SystemAccentColor"),
        (UIColorType.AccentDark1, "SystemAccentColorDark1"),
        (UIColorType.AccentDark2, "SystemAccentColorDark2"),
        (UIColorType.AccentDark3, "SystemAccentColorDark3"),
        (UIColorType.AccentLight1, "SystemAccentColorLight1"),
        (UIColorType.AccentLight2, "SystemAccentColorLight2"),
        (UIColorType.AccentLight3, "SystemAccentColorLight3"),
    ];

    private readonly Dictionary<SolidColorBrush, Color> _recolored = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SolidColorBrush, double> _opacities = new(ReferenceEqualityComparer.Instance);
    // Assigning Color drops a brush's {ThemeResource SystemAccentColor*} binding, so brushes that showed an
    // accent shade when first captured keep following the Windows accent after the theme is cleared.
    private readonly Dictionary<SolidColorBrush, UIColorType> _accentBound = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<GradientStop, UIColorType> _accentBoundStops = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<GradientStop> _themedStops = new(ReferenceEqualityComparer.Instance);
    private readonly List<(ResourceDictionary Owner, string Key, object? Previous, bool Existed)> _overrides = [];
    private readonly UISettings _uiSettings = new();
    private bool _appliedInHighContrast;
    private (string Body, string Chat, string Mono) _fonts;
    private ThemeRadii? _radii;
    private bool _controlGeometryDecided;
    private int _readOnlyBrushes;

    /// <summary>Whether a brush's color can be set; tests substitute it to simulate a read-only framework brush.</summary>
    internal static Func<SolidColorBrush, bool> IsWritable { get; set; } = static _ => true;

    public ResolvedTheme? Current { get; private set; }

    /// <summary>
    /// Gives WinUI controls the theme's corner radii (ControlCornerRadius, OverlayCornerRadius) for the rest of the
    /// session. Only the first call has an effect: a Fluent control style resolves its radius when the first control of
    /// that type is created and keeps it, so a later change would reshape some control types and not others. Call it
    /// once at startup, before any window exists. Windows default and High Contrast keep Fluent's geometry, and so do
    /// windows whose App theme differs from Windows' mode (see <see cref="Replace"/> for why the other mode's dictionary
    /// is never written); chat surfaces follow every switch through <see cref="Apply"/>.
    /// </summary>
    public void DecideControlGeometry(ResolvedTheme? theme, ThemeColorMode mode)
    {
        if (_controlGeometryDecided)
            return;
        _controlGeometryDecided = true;
        if (theme is null || ChatThemeChanges.IsHighContrast())
        {
            Logger.Info("[Themes] Controls keep Fluent's corner radii this session.");
            return;
        }
        var resources = Application.Current.Resources;
        // A Fluent control style resolves its radius in XamlControlsResources' own theme dictionaries, so that's where
        // the value has to go; an application-root key doesn't reach those styles. Only the app mode's dictionary is
        // written: writing the other mode's binds its brush aliases to the app mode's brushes.
        List<ResourceDictionary> targets = FluentThemeDictionary(resources, AppModeThemeKey()) is { } fluent ? [fluent] : [resources];
        foreach (var (key, radius) in ThemeResourceProjection.CornerRadii(theme.PaletteFor(mode).Radii))
        {
            foreach (var target in targets)
            {
                try
                {
                    target[key] = new CornerRadius(radius);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Themes] Setting {key} failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
                }
            }
        }
        Logger.Info($"[Themes] Controls take theme {theme.Id}'s corner radii this session in windows of the app's " +
            $"{Application.Current.RequestedTheme} mode; windows of the other mode keep Fluent's.");
    }

    /// <summary>
    /// Applies <paramref name="theme"/> (null restores Fluent). Returns false when nothing changed.
    /// Call again when Windows colors or the effective light/dark mode change so High Contrast, accent and
    /// the mode's fonts are honoured. Fonts are application-wide, so they come from <paramref name="fontMode"/>'s palette.
    /// </summary>
    public bool Apply(ResolvedTheme? theme, ThemeColorMode fontMode = ThemeColorMode.Light)
    {
        var highContrast = ChatThemeChanges.IsHighContrast();
        var fonts = theme is null ? default : FontsFor(theme, fontMode);
        // Chat geometry is application-wide like fonts; High Contrast keeps the Windows geometry.
        var radii = theme is null || highContrast ? null : theme.PaletteFor(fontMode).Radii;
        if (Equals(theme, Current) && (theme is null || (highContrast == _appliedInHighContrast && fonts == _fonts && radii == _radii)))
        {
            if (theme is null)
                FollowWindowsAccent();
            return false;
        }
        Restore();
        Current = theme;
        _appliedInHighContrast = highContrast;
        _fonts = fonts;
        _radii = radii;
        ChatVisuals.SetCornerScale(radii?.Scale ?? 1);
        if (theme is null)
        {
            ChatVisuals.SetThemeFonts(null, null);
            ChatVisuals.SetThemeInlineCode(null);
            Logger.Info("[Themes] Restored the Windows default look.");
            return true;
        }

        var resources = Application.Current.Resources;
        int shared = 0, rootOverrides = 0, fluentReplacements = 0, accentFollowers = 0, failures = 0;
        _readOnlyBrushes = 0;
        // A resource the framework refuses to change must not abort the rest of the theme or the caller.
        void Guard(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                if (failures++ < 5)
                    Logger.Warn($"[Themes] {what} failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }
        }

        // Under High Contrast every element resolves the High Contrast dictionaries, so the light and dark ones are
        // left alone: recoloring them would capture High Contrast values as their originals. Leaving High Contrast
        // applies the theme again (see _appliedInHighContrast), which recolors them with their own originals.
        foreach (var (themeKey, mode) in highContrast ? [] : s_themeDictionaries)
        {
            var ownTheme = mode == ThemeColorMode.Dark ? ElementTheme.Dark : ElementTheme.Light;
            var projection = ThemeResourceProjection.Project(theme.PaletteFor(mode));
            var dictionaries = ThemeDictionaries(resources, themeKey).Select(d => (Dictionary: d, Keys: OwnKeys(d))).ToList();
            var root = resources.ThemeDictionaries.TryGetValue(themeKey, out var value) ? value as ResourceDictionary : null;
            var fluent = FluentThemeDictionary(resources, themeKey);
            foreach (var (key, color) in projection.Brushes)
            {
                Guard($"{themeKey}/{key}", () =>
                {
                    var target = ToColor(color);
                    var found = false;
                    foreach (var (dictionary, ownKeys) in dictionaries)
                    {
                        // TryGetValue (and ContainsKey) also resolve merged and global resources, including High Contrast brushes.
                        if (ownKeys.Contains(key) && dictionary.TryGetValue(key, out var resource) && resource is SolidColorBrush brush &&
                            Recolor(brush, target, ownTheme))
                        {
                            found = true;
                            if (projection.Opacities.TryGetValue(key, out var opacity))
                                SetOpacity(brush, opacity);
                        }
                    }
                    // Framework brushes: WinUI's own theme dictionary holds the instance every element of this theme
                    // renders, including aliases such as NavigationViewContentBackground. A probe element's theme
                    // resolution is only a fallback for keys neither dictionary defines.
                    var themed = fluent is not null && fluent.TryGetValue(key, out var own) ? own as SolidColorBrush
                        : found ? null : ResolveThemeBrush(key, ownTheme);
                    if (themed is not null)
                    {
                        if (Recolor(themed, target, ownTheme))
                            found = true;
                        else if (fluent is not null && themeKey == AppModeThemeKey() && Replace(fluent, key, themed) is { } replacement)
                        {
                            // Elements that resolved a read-only brush never read an application-root override.
                            Recolor(replacement, target, ownTheme);
                            if (projection.Opacities.TryGetValue(key, out var opacity))
                                SetOpacity(replacement, opacity);
                            found = true;
                            fluentReplacements++;
                        }
                    }
                    if (found)
                        shared++;
                    else if (root is not null)
                    {
                        Override(root, key, new SolidColorBrush(target));
                        rootOverrides++;
                    }
                });
            }
            if (root is not null)
            {
                foreach (var (key, color) in projection.Colors)
                    Guard($"{themeKey}/{key}", () => Override(root, key, ToColor(color)));
            }

            // App visuals bound to a {ThemeResource SystemAccentColor*} shade (the Hub navigation indicator, usage,
            // systems and automations visuals, voice) hold the shade they resolved at load, so they would keep the
            // Windows accent. They take the matching shade of the theme's accent ramp; keys the projection names keep
            // their projected color, and WinUI's own dictionaries are left to the projection.
            var ramp = AccentRamp(projection);
            foreach (var dictionary in AppThemeDictionaries(resources, themeKey))
            {
                foreach (var key in OwnKeys(dictionary))
                {
                    if (projection.Brushes.ContainsKey(key))
                        continue;
                    Guard($"{themeKey}/{key}", () =>
                    {
                        if (dictionary.TryGetValue(key, out var resource))
                            accentFollowers += FollowThemeAccent(resource, ramp, ownTheme);
                    });
                }
            }
        }

        Guard("ContentControlThemeFontFamily", () =>
            Override(resources, "ContentControlThemeFontFamily", new FontFamily(fonts.Body)));
        ChatVisuals.SetThemeFonts(fonts.Chat, fonts.Mono);
        ChatVisuals.SetThemeInlineCode((ToColor(theme.PaletteFor(ThemeColorMode.Dark).InlineCode),
            ToColor(theme.PaletteFor(ThemeColorMode.Light).InlineCode)));
        Logger.Info($"[Themes] Applied theme {theme.Id}: {shared} shared brushes recolored, {rootOverrides} root overrides, " +
            $"{fluentReplacements} read-only framework brushes replaced, " +
            $"{accentFollowers} accent visuals, {_readOnlyBrushes} read-only brushes, {failures} failures.");
        return true;
    }

    private static (string Body, string Chat, string Mono) FontsFor(ResolvedTheme theme, ThemeColorMode mode)
    {
        var projection = ThemeResourceProjection.Project(theme.PaletteFor(mode));
        return (projection.BodyFontSource, projection.ChatFontSource, projection.MonoFontSource);
    }

    private void Restore()
    {
        foreach (var (brush, color) in _recolored)
        {
            try
            {
                brush.Color = _accentBound.TryGetValue(brush, out var accent) ? _uiSettings.GetColorValue(accent) : color;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Themes] Restoring a brush failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }
        }
        _recolored.Clear();
        foreach (var (brush, opacity) in _opacities)
        {
            try
            {
                brush.Opacity = opacity;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Themes] Restoring a brush opacity failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }
        }
        _opacities.Clear();
        foreach (var stop in _themedStops)
        {
            try
            {
                stop.Color = _uiSettings.GetColorValue(_accentBoundStops[stop]);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Themes] Restoring a gradient stop failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }
        }
        _themedStops.Clear();
        for (var index = _overrides.Count - 1; index >= 0; index--)
        {
            var (owner, key, previous, existed) = _overrides[index];
            try
            {
                if (existed)
                    owner[key] = previous;
                else
                    owner.Remove(key);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Themes] Restoring {key} failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }
        }
        _overrides.Clear();
    }

    private void FollowWindowsAccent()
    {
        foreach (var (brush, accent) in _accentBound)
            brush.Color = _uiSettings.GetColorValue(accent);
        foreach (var (stop, accent) in _accentBoundStops)
            stop.Color = _uiSettings.GetColorValue(accent);
    }

    /// <summary>Windows accent shade colors mapped to the theme's matching shade.</summary>
    private Dictionary<Color, Color> AccentRamp(ThemeProjection projection)
    {
        var ramp = new Dictionary<Color, Color>();
        foreach (var (type, key) in s_accentShades)
            ramp.TryAdd(_uiSettings.GetColorValue(type), ToColor(projection.Colors[key]));
        return ramp;
    }

    /// <summary>Moves a brush or gradient that shows a Windows accent shade onto the theme's shade. Returns the number changed.</summary>
    private int FollowThemeAccent(object resource, Dictionary<Color, Color> ramp, ElementTheme? ownTheme)
    {
        if (ownTheme is { } theme && resource is Brush unthemed && !(unthemed is SolidColorBrush solid && _recolored.ContainsKey(solid)))
            ResolveUnder(unthemed, theme);
        switch (resource)
        {
            case SolidColorBrush brush when ramp.TryGetValue(brush.Color, out var shade):
                return Recolor(brush, shade, null) ? 1 : 0;
            case GradientBrush gradient:
                var changed = 0;
                foreach (var stop in gradient.GradientStops)
                {
                    if (!ramp.TryGetValue(stop.Color, out var stopShade) || AccentTypeOf(stop.Color) is not { } accent)
                        continue;
                    stop.Color = stopShade;
                    _accentBoundStops.TryAdd(stop, accent);
                    _themedStops.Add(stop);
                    changed++;
                }
                return changed;
            default:
                return 0;
        }
    }

    private bool Recolor(SolidColorBrush brush, Color color, ElementTheme? ownTheme)
    {
        if (ownTheme is { } theme && !_recolored.ContainsKey(brush))
            ResolveUnder(brush, theme);
        if (!IsWritable(brush))
        {
            _readOnlyBrushes++;
            return false;
        }
        var original = brush.Color;
        try
        {
            brush.Color = color;
        }
        catch (UnauthorizedAccessException)
        {
            // Some framework brushes are read-only (E_ACCESSDENIED); the caller replaces them in WinUI's theme dictionary.
            _readOnlyBrushes++;
            return false;
        }
        if (_recolored.TryAdd(brush, original) && AccentTypeOf(original) is { } accent)
            _accentBound.TryAdd(brush, accent);
        return true;
    }

    private void SetOpacity(SolidColorBrush brush, double opacity)
    {
        _opacities.TryAdd(brush, brush.Opacity);
        brush.Opacity = opacity;
    }

    // A theme-dictionary brush whose color is a {ThemeResource} reference holds the value of whichever theme last
    // resolved it, not necessarily its own (a dark brush the app never rendered still has the light value). Restore
    // writes the captured color back as a constant, so resolve it under its own theme before capturing. The flip
    // guarantees a theme change, and therefore a resolution, even when the probe already had that theme.
    private static void ResolveUnder(Brush brush, ElementTheme theme)
    {
        try
        {
            var probe = new Border { Background = brush };
            probe.RequestedTheme = theme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            probe.RequestedTheme = theme;
            probe.Background = null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Themes] Resolving a brush under its theme failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    /// <summary>
    /// The brush instance an element of <paramref name="theme"/> resolves for <paramref name="key"/>, through the
    /// theming system, for keys neither the app's nor WinUI's theme dictionaries define. Null when the key is not a brush.
    /// </summary>
    private static SolidColorBrush? ResolveThemeBrush(string key, ElementTheme theme)
    {
        try
        {
            var probe = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                $"<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{{ThemeResource {key}}}'/>");
            probe.RequestedTheme = theme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            probe.RequestedTheme = theme;
            var brush = probe.Background as SolidColorBrush;
            probe.ClearValue(Border.BackgroundProperty);
            return brush;
        }
        catch (Exception)
        {
            // Not a resource any theme defines; the caller overrides it at the application root instead.
            return null;
        }
    }

    private UIColorType? AccentTypeOf(Color color)
    {
        foreach (var (type, _) in s_accentShades)
        {
            if (_uiSettings.GetColorValue(type) == color)
                return type;
        }
        return null;
    }

    /// <summary>
    /// Puts a writable copy of a read-only framework brush in WinUI's theme dictionary, once per session, and keeps it:
    /// later themes recolor the copy and clearing the theme gives it the original's color back. Only the dictionary of
    /// the mode the app runs in is written: writing the other mode's loads it under the app's mode, which binds every
    /// alias in it (such as ButtonBackground or NavigationViewContentBackground) to the app mode's brushes. The other
    /// mode takes an application-root override instead.
    /// </summary>
    private SolidColorBrush? Replace(ResourceDictionary fluent, string key, SolidColorBrush original)
    {
        try
        {
            var replacement = new SolidColorBrush(original.Color) { Opacity = original.Opacity };
            fluent[key] = replacement;
            return replacement;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Themes] Replacing the read-only {key} failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            return null;
        }
    }

    // The theme dictionary WinUI loads for the app's own mode, which follows Windows; the app never sets it.
    private static string AppModeThemeKey() =>
        Application.Current.RequestedTheme == ApplicationTheme.Light ? "Light" : "Default";

    private void Override(ResourceDictionary owner, string key, object value)
    {
        // Restore only the owner's own entry; removing the override uncovers an inherited value again.
        var existed = OwnKeys(owner).Contains(key);
        _overrides.Add((owner, key, existed ? owner[key] : null, existed));
        owner[key] = value;
    }

    private static HashSet<string> OwnKeys(ResourceDictionary dictionary)
    {
        try
        {
            return new HashSet<string>(dictionary.Keys.OfType<string>(), StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Themes] Reading resource keys failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Every non-High-Contrast theme dictionary named <paramref name="themeKey"/>, including merged ones, without
    /// WinUI's XamlControlsResources: enumerating its theme dictionaries resolves every brush in them against the
    /// application theme, so brushes a theme never sets would show that theme's colors in a window of the other one.
    /// Framework brushes are looked up by key in <see cref="FluentThemeDictionary"/> instead.
    /// </summary>
    private static IEnumerable<ResourceDictionary> ThemeDictionaries(ResourceDictionary dictionary, string themeKey)
    {
        if (dictionary is Microsoft.UI.Xaml.Controls.XamlControlsResources)
            yield break;
        if (dictionary.ThemeDictionaries.TryGetValue(themeKey, out var theme) && theme is ResourceDictionary themed)
        {
            yield return themed;
            foreach (var merged in themed.MergedDictionaries)
                yield return merged;
        }
        foreach (var merged in dictionary.MergedDictionaries)
        {
            foreach (var nested in ThemeDictionaries(merged, themeKey))
                yield return nested;
        }
    }

    /// <summary>
    /// WinUI's XamlControlsResources theme dictionary named <paramref name="themeKey"/>, where Fluent styles resolve
    /// framework brushes. Only looked up, never enumerated, so no brush in it is resolved against the app mode.
    /// </summary>
    private static ResourceDictionary? FluentThemeDictionary(ResourceDictionary dictionary, string themeKey)
    {
        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (merged is Microsoft.UI.Xaml.Controls.XamlControlsResources)
                return merged.ThemeDictionaries.TryGetValue(themeKey, out var theme) ? theme as ResourceDictionary : null;
            if (FluentThemeDictionary(merged, themeKey) is { } nested)
                return nested;
        }
        return null;
    }

    /// <summary>The app's own theme dictionaries named <paramref name="themeKey"/>, without WinUI's XamlControlsResources.</summary>
    private static IEnumerable<ResourceDictionary> AppThemeDictionaries(ResourceDictionary dictionary, string themeKey)
    {
        if (dictionary is Microsoft.UI.Xaml.Controls.XamlControlsResources)
            yield break;
        if (dictionary.ThemeDictionaries.TryGetValue(themeKey, out var theme) && theme is ResourceDictionary themed)
            yield return themed;
        foreach (var merged in dictionary.MergedDictionaries)
        {
            foreach (var nested in AppThemeDictionaries(merged, themeKey))
                yield return nested;
        }
    }

    private static Color ToColor(ThemeColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);
}
