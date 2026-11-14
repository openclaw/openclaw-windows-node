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
/// dictionary entries, and the framework brush an element of each theme resolves. Every consumer, including
/// framework control templates that alias them, updates at once.
/// Restoring writes back each brush's color as resolved under its own dictionary's theme.
/// Keys with no reachable or writable instance get an application-root override. High Contrast resources
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
    // Assigning Color drops a brush's {ThemeResource SystemAccentColor*} binding, so brushes that showed an
    // accent shade when first captured keep following the Windows accent after the theme is cleared.
    private readonly Dictionary<SolidColorBrush, UIColorType> _accentBound = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<GradientStop, UIColorType> _accentBoundStops = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<GradientStop> _themedStops = new(ReferenceEqualityComparer.Instance);
    private readonly List<(ResourceDictionary Owner, string Key, object? Previous, bool Existed)> _overrides = [];
    private readonly UISettings _uiSettings = new();
    private bool _appliedInHighContrast;
    private (string Body, string Chat, string Mono) _fonts;
    private int _readOnlyBrushes;

    public ResolvedTheme? Current { get; private set; }

    /// <summary>
    /// Applies <paramref name="theme"/> (null restores Fluent). Returns false when nothing changed.
    /// Call again when Windows colors or the effective light/dark mode change so High Contrast, accent and
    /// the mode's fonts are honoured. Fonts are application-wide, so they come from <paramref name="fontMode"/>'s palette.
    /// </summary>
    public bool Apply(ResolvedTheme? theme, ThemeColorMode fontMode = ThemeColorMode.Light)
    {
        var highContrast = ChatThemeChanges.IsHighContrast();
        var fonts = theme is null ? default : FontsFor(theme, fontMode);
        if (Equals(theme, Current) && (theme is null || (highContrast == _appliedInHighContrast && fonts == _fonts)))
        {
            if (theme is null)
                FollowWindowsAccent();
            return false;
        }
        Restore();
        Current = theme;
        _appliedInHighContrast = highContrast;
        _fonts = fonts;
        if (theme is null)
        {
            ChatVisuals.SetThemeFonts(null, null);
            ThemeBrandingState.Set(ThemeBranding.Default);
            Logger.Info("[Themes] Restored the Windows default look.");
            return true;
        }

        var resources = Application.Current.Resources;
        int shared = 0, rootOverrides = 0, accentFollowers = 0, failures = 0;
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
            foreach (var (key, color) in projection.Brushes)
            {
                Guard($"{themeKey}/{key}", () =>
                {
                    var target = ToColor(color);
                    var found = false;
                    foreach (var (dictionary, ownKeys) in dictionaries)
                    {
                        // TryGetValue (and ContainsKey) also resolve merged and global resources, including High Contrast brushes.
                        if (ownKeys.Contains(key) && dictionary.TryGetValue(key, out var resource) && resource is SolidColorBrush brush)
                            found |= Recolor(brush, target, ownTheme);
                    }
                    // Framework brushes: the instance an element of this theme resolves.
                    if (ResolveThemeBrush(key, ownTheme) is { } themed)
                        found |= Recolor(themed, target, ownTheme);
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
        // Brand spots and the working indicator follow the theme's branding once its colors are in place;
        // High Contrast only changes colors.
        ThemeBrandingState.Set(theme.Branding);
        Logger.Info($"[Themes] Applied theme {theme.Id}: {shared} shared brushes recolored, {rootOverrides} root overrides, " +
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
        var original = brush.Color;
        try
        {
            brush.Color = color;
        }
        catch (UnauthorizedAccessException)
        {
            // Some framework brushes are read-only (E_ACCESSDENIED); the caller falls back to a root override.
            _readOnlyBrushes++;
            return false;
        }
        if (_recolored.TryAdd(brush, original) && AccentTypeOf(original) is { } accent)
            _accentBound.TryAdd(brush, accent);
        return true;
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
    /// theming system rather than by reading a framework dictionary. Null when the key is not a brush.
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
    /// Framework brushes are reached through <see cref="ResolveThemeBrush"/> instead.
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
