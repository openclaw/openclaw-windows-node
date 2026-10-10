using OpenClawTray.Services;

namespace OpenClawTray.Presentation;

/// <summary>
/// Observes arrivals, not banner selection. Dismissal, queue rotation and enabling the
/// mascot must never replay old notifications. The app remains the notification owner.
/// </summary>
internal sealed class DesktopCompanionState
{
    private Dictionary<string, int> _seen = new(StringComparer.Ordinal);
    public AppNotification? Current { get; private set; }

    public void Reset(AppNotificationSnapshot snapshot)
    {
        Remember(snapshot);
        Current = null;
    }

    public bool Observe(AppNotificationSnapshot snapshot, bool enabled)
    {
        var next = enabled
            ? snapshot.ActiveNotifications.LastOrDefault(item =>
                !_seen.TryGetValue(item.Id, out var count) || item.OccurrenceCount > count)
            : null;
        Remember(snapshot);
        if (next is not null)
        {
            Current = next;
            return true;
        }

        if (Current is not null && (!enabled || !_seen.ContainsKey(Current.Id)))
        {
            Current = null;
            return true;
        }
        return false;
    }

    private void Remember(AppNotificationSnapshot snapshot) =>
        _seen = snapshot.ActiveNotifications
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(item => item.OccurrenceCount),
                StringComparer.Ordinal);
}

internal readonly record struct DesktopCompanionBounds(int X, int Y, int Width, int Height)
{
    public static DesktopCompanionBounds Clamp(int x, int y, int width, int height,
        DesktopCompanionBounds workArea) => new(
            Math.Clamp(x, workArea.X, workArea.X + Math.Max(0, workArea.Width - width)),
            Math.Clamp(y, workArea.Y, workArea.Y + Math.Max(0, workArea.Height - height)),
            width, height);
}
