using OpenClaw.Shared.Themes;
using OpenClawTray.Services;

namespace OpenClawTray.Theming;

/// <summary>
/// Keeps <see cref="AppearanceSettings.FollowedTheme"/> in step with the connected Gateway's selected dashboard
/// theme while "Follow Gateway theme" is on. It only reads the catalog: the Gateway selection is never written.
/// A Gateway theme the Companion can't render leaves the last followed theme in place.
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

    /// <param name="read">The persisted appearance.</param>
    /// <param name="write">Persists an updated appearance without applying it.</param>
    /// <param name="followedChanged">Applies the theme after the catalog moved the followed theme.</param>
    public GatewayThemeFollower(
        IGatewayThemeCatalog catalog, Func<AppearanceSettings?> read, Action<AppearanceSettings> write, Action followedChanged)
    {
        _catalog = catalog;
        _read = read;
        _write = write;
        _followedChanged = followedChanged;
        _catalog.Changed += OnCatalogChanged;
    }

    /// <summary>Records the Gateway's current theme when following. Returns true when the followed theme changed.</summary>
    public bool Sync()
    {
        if (_read() is not { FollowGatewayTheme: true } appearance)
            return false;
        var current = _catalog.GatewayCurrent;
        var next = AppearanceResolver.Follow(appearance, current?.Descriptor, current?.Definition, current?.GatewayId);
        if (next == appearance)
        {
            if (current is not null && next.FollowedTheme?.Id != current.Id)
                Logger.Info($"[Themes] The Gateway theme {current.Id} can't be rendered here; keeping the last followed theme.");
            return false;
        }
        _write(next);
        Logger.Info($"[Themes] Following the Gateway theme {next.FollowedTheme?.Id}.");
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

    public void Dispose() => _catalog.Changed -= OnCatalogChanged;
}
