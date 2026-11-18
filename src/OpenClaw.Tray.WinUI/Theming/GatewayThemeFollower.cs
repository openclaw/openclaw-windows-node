using OpenClaw.Shared.Themes;
using OpenClawTray.Services;

namespace OpenClawTray.Theming;

/// <summary>
/// Keeps <see cref="AppearanceSettings.FollowedTheme"/>, <see cref="AppearanceSettings.FollowedAccent"/> and
/// <see cref="AppearanceSettings.FollowedBackground"/> in step with the connected Gateway's selected dashboard theme
/// and the person's dashboard accent and background while "Follow Gateway theme" is on. It only reads the catalog: the Gateway selection is never written. A Gateway theme the Companion can't
/// render, or preferences it can't read, leave the last followed values in place. A built-in renders when the connected
/// Gateway's own palettes or the bundled table style it, so the follower re-checks when those palettes arrive.
/// <example><code>
/// var follower = new GatewayThemeFollower(catalog, () => settings.Appearance, Persist, ApplyThemeToOpenWindows);
/// follower.Sync(); // also after the setting is turned on
/// </code></example>
/// </summary>
internal sealed class GatewayThemeFollower : IDisposable
{
    private readonly IGatewayThemeCatalog _catalog;
    private readonly Func<AppearanceSettings?> _read;
    private readonly Action<AppearanceSettings> _write;
    private readonly Action _followedChanged;
    private readonly IGatewayBuiltinThemes? _builtins;

    /// <param name="read">The persisted appearance.</param>
    /// <param name="write">Persists an updated appearance without applying it.</param>
    /// <param name="followedChanged">Applies the theme after the catalog moved the followed theme.</param>
    /// <param name="builtins">Built-in palettes; the bundled table when null.</param>
    public GatewayThemeFollower(
        IGatewayThemeCatalog catalog, Func<AppearanceSettings?> read, Action<AppearanceSettings> write, Action followedChanged,
        IGatewayBuiltinThemes? builtins = null)
    {
        _catalog = catalog;
        _read = read;
        _write = write;
        _followedChanged = followedChanged;
        _builtins = builtins;
        _catalog.Changed += OnCatalogChanged;
        if (_builtins is not null)
            _builtins.Changed += OnCatalogChanged;
    }

    /// <summary>Records the Gateway's current theme when following. Returns true when the followed theme changed.</summary>
    public bool Sync()
    {
        if (_read() is not { FollowGatewayTheme: true } appearance)
            return false;
        var current = _catalog.GatewayCurrent;
        var next = AppearanceResolver.Follow(appearance, current?.Descriptor, current?.Definition, current?.GatewayId, _builtins);
        if (current is not null && next.FollowedTheme?.Id != current.Id)
            Logger.Info($"[Themes] The Gateway theme {current.Id} can't be rendered here; keeping the last followed theme.");
        next = AppearanceResolver.FollowPreferences(next, _catalog.GatewayPreferences);
        if (next == appearance)
            return false;
        _write(next);
        Logger.Info($"[Themes] Following the Gateway theme {next.FollowedTheme?.Id} with " +
            $"{(next.FollowedAccent is null ? "its own accent" : "the dashboard accent")}.");
        return true;
    }

    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        bool changed;
        try
        {
            changed = Sync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A failed settings save must not escape the catalog event and starve its other subscribers.
            Logger.Warn($"[Themes] Recording the followed Gateway theme failed: {ex.GetType().Name} {ex.Message}");
            return;
        }
        if (changed)
            _followedChanged();
    }

    public void Dispose()
    {
        _catalog.Changed -= OnCatalogChanged;
        if (_builtins is not null)
            _builtins.Changed -= OnCatalogChanged;
    }
}
