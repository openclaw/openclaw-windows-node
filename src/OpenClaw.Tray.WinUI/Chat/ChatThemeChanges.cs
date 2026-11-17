using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace OpenClawTray.Chat;

/// <summary>
/// Dispatches system color/contrast changes to mounted chat surfaces, including changes
/// that do not raise ActualThemeChanged. Pair Watch(target, refresh) with Unwatch(target).
/// </summary>
internal static class ChatThemeChanges
{
    private sealed record Subscription(Action Refresh);
    private static readonly object Gate = new();
    private static readonly Dictionary<FrameworkElement, Subscription> Targets = [];
    private static UISettings? _settings;

    internal static Func<bool> IsHighContrast { get; set; } =
        static () => new AccessibilitySettings().HighContrast;

    internal static void Watch(FrameworkElement target, Action refresh)
    {
        lock (Gate)
        {
            if (_settings is null)
            {
                _settings = new UISettings();
                _settings.ColorValuesChanged += OnColorsChanged;
            }
            Targets[target] = new(refresh);
        }
    }

    internal static void Unwatch(FrameworkElement target)
    {
        lock (Gate)
        {
            Targets.Remove(target);
            if (Targets.Count == 0 && _settings is not null)
            {
                _settings.ColorValuesChanged -= OnColorsChanged;
                _settings = null;
            }
        }
    }

    private static void OnColorsChanged(UISettings sender, object args) => Refresh();

    /// <summary>Refreshes registered surfaces on their UI threads without reviving unmounted controls.</summary>
    internal static void Refresh()
    {
        KeyValuePair<FrameworkElement, Subscription>[] targets;
        lock (Gate)
            targets = [.. Targets];
        foreach (var (target, subscription) in targets)
            target.DispatcherQueue.TryEnqueue(() =>
            {
                lock (Gate)
                {
                    if (!Targets.TryGetValue(target, out var current) || !ReferenceEquals(current, subscription))
                        return;
                }
                subscription.Refresh();
            });
    }
}
