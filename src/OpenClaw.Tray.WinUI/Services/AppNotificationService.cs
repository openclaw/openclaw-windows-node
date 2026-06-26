using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClawTray.Services;

public enum AppNotificationSeverity
{
    Informational,
    Success,
    Warning,
    Error
}

public enum AppNotificationPersistence
{
    Persistent,
    AutoDismiss
}

public sealed record AppNotification
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public AppNotificationSeverity Severity { get; init; } = AppNotificationSeverity.Informational;
    public string Source { get; init; } = "";
    public string? Category { get; init; }
    public string? DedupeKey { get; init; }
    public string? ActionLabel { get; init; }
    public string? ActionRoute { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public AppNotificationPersistence Persistence { get; init; } = AppNotificationPersistence.Persistent;
    public int OccurrenceCount { get; init; } = 1;
}

public sealed record AppNotificationSnapshot(
    AppNotification? Current,
    int PendingCount,
    IReadOnlyList<AppNotification> Queued,
    IReadOnlyList<AppNotification> ActiveNotifications)
{
    public bool HasMultipleActiveNotifications => ActiveNotifications.Count > 1;
}

internal static class AppNotificationActionRoutes
{
    private const string ChatPrefix = "chat:";

    public static string Chat(string sessionKey) =>
        ChatPrefix + Uri.EscapeDataString(sessionKey);

    public static bool TryGetChatSessionKey(string? route, out string? sessionKey)
    {
        sessionKey = null;
        if (string.IsNullOrWhiteSpace(route) ||
            !route.StartsWith(ChatPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var encoded = route[ChatPrefix.Length..];
        if (string.IsNullOrWhiteSpace(encoded))
            return false;

        sessionKey = Uri.UnescapeDataString(encoded);
        return !string.IsNullOrWhiteSpace(sessionKey);
    }
}

internal sealed class AppNotificationBannerState
{
    private const string ConnectionSource = "connection";
    private readonly HashSet<string> _hiddenNotificationIds = new(StringComparer.Ordinal);

    public AppNotification? SelectVisibleNotification(AppNotificationSnapshot snapshot, bool revealHiddenIfNeeded = false)
    {
        PruneRemovedNotifications(snapshot);

        var visibleCandidates = snapshot.ActiveNotifications
            .Where(notification => !_hiddenNotificationIds.Contains(notification.Id))
            .ToList();

        // Connection issues are the most actionable banner (they route the user
        // to the Connection page), so surface them ahead of any earlier,
        // unrelated notification that happens to be current. Without this, a
        // connection failure arriving while another notification is showing
        // would be queued behind it and the user couldn't reach Connection.
        // Among connection-source notifications, prefer one that actually has an
        // action: an action-less connection notification (e.g. a transient
        // gateway-host failure) must not mask a real connection error and
        // degrade the banner to "Show more".
        var visible = visibleCandidates.FirstOrDefault(IsActionableConnectionPriority)
            ?? visibleCandidates.FirstOrDefault(IsConnectionPriority)
            ?? visibleCandidates.FirstOrDefault();
        if (visible is not null || !revealHiddenIfNeeded)
            return visible;

        var fallback = snapshot.ActiveNotifications.FirstOrDefault(IsActionableConnectionPriority)
            ?? snapshot.ActiveNotifications.FirstOrDefault(IsConnectionPriority)
            ?? snapshot.ActiveNotifications.FirstOrDefault();
        if (fallback is not null)
            _hiddenNotificationIds.Remove(fallback.Id);
        return fallback;
    }

    private static bool IsConnectionPriority(AppNotification notification) =>
        string.Equals(notification.Source, ConnectionSource, StringComparison.OrdinalIgnoreCase);

    private static bool IsActionableConnectionPriority(AppNotification notification) =>
        IsConnectionPriority(notification)
        && !string.IsNullOrWhiteSpace(notification.ActionLabel)
        && !string.IsNullOrWhiteSpace(notification.ActionRoute);

    public void HideActiveNotifications(AppNotificationSnapshot snapshot)
    {
        PruneRemovedNotifications(snapshot);
        foreach (var notification in snapshot.ActiveNotifications)
            _hiddenNotificationIds.Add(notification.Id);
    }

    private void PruneRemovedNotifications(AppNotificationSnapshot snapshot)
    {
        var activeIds = snapshot.ActiveNotifications
            .Select(notification => notification.Id)
            .ToHashSet(StringComparer.Ordinal);
        _hiddenNotificationIds.RemoveWhere(id => !activeIds.Contains(id));
    }
}

public sealed class AppNotificationChangedEventArgs(AppNotificationSnapshot snapshot) : EventArgs
{
    public AppNotificationSnapshot Snapshot { get; } = snapshot;
}

internal sealed class AppNotificationService
{
    private readonly object _gate = new();
    private readonly List<AppNotification> _queue = new();
    private AppNotification? _current;

    public event EventHandler<AppNotificationChangedEventArgs>? Changed;

    public AppNotificationSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return CreateSnapshotLocked();
            }
        }
    }

    public void Show(AppNotification notification)
    {
        var normalized = Normalize(notification);
        AppNotificationSnapshot snapshot;
        lock (_gate)
        {
            if (TryCoalesceLocked(normalized))
            {
                snapshot = CreateSnapshotLocked();
            }
            else if (_current is null)
            {
                _current = normalized;
                snapshot = CreateSnapshotLocked();
            }
            else
            {
                _queue.Add(normalized);
                snapshot = CreateSnapshotLocked();
            }
        }
        RaiseChanged(snapshot);
    }

    public void DismissCurrent()
    {
        AppNotificationSnapshot snapshot;
        lock (_gate)
        {
            _current = _queue.Count > 0 ? DequeueLocked() : null;
            snapshot = CreateSnapshotLocked();
        }
        RaiseChanged(snapshot);
    }

    public void Dismiss(string notificationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notificationId);

        AppNotificationSnapshot snapshot;
        var changed = false;
        lock (_gate)
        {
            if (_current is not null && string.Equals(_current.Id, notificationId, StringComparison.Ordinal))
            {
                _current = _queue.Count > 0 ? DequeueLocked() : null;
                changed = true;
            }
            else
            {
                for (var i = 0; i < _queue.Count; i++)
                {
                    if (!string.Equals(_queue[i].Id, notificationId, StringComparison.Ordinal))
                        continue;

                    _queue.RemoveAt(i);
                    changed = true;
                    break;
                }
            }

            snapshot = CreateSnapshotLocked();
        }

        if (changed)
            RaiseChanged(snapshot);
    }

    public void DismissByDedupeKey(string dedupeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dedupeKey);

        AppNotificationSnapshot snapshot;
        var changed = false;
        lock (_gate)
        {
            if (_current is not null &&
                string.Equals(_current.DedupeKey, dedupeKey, StringComparison.OrdinalIgnoreCase))
            {
                _current = _queue.Count > 0 ? DequeueLocked() : null;
                changed = true;
            }

            for (var i = _queue.Count - 1; i >= 0; i--)
            {
                if (!string.Equals(_queue[i].DedupeKey, dedupeKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                _queue.RemoveAt(i);
                changed = true;
            }

            snapshot = CreateSnapshotLocked();
        }

        if (changed)
            RaiseChanged(snapshot);
    }

    public void ClearAll()
    {
        AppNotificationSnapshot snapshot;
        var changed = false;
        lock (_gate)
        {
            if (_current is not null || _queue.Count > 0)
            {
                _current = null;
                _queue.Clear();
                changed = true;
            }

            snapshot = CreateSnapshotLocked();
        }

        if (changed)
            RaiseChanged(snapshot);
    }

    public void ShowNext()
    {
        AppNotificationSnapshot snapshot;
        lock (_gate)
        {
            if (_queue.Count > 0)
            {
                if (_current is not null)
                    _queue.Add(_current);
                _current = DequeueLocked();
            }
            snapshot = CreateSnapshotLocked();
        }
        RaiseChanged(snapshot);
    }

    public void ClearSource(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        AppNotificationSnapshot snapshot;
        bool changed = false;
        lock (_gate)
        {
            for (var i = _queue.Count - 1; i >= 0; i--)
            {
                if (!string.Equals(_queue[i].Source, source, StringComparison.OrdinalIgnoreCase))
                    continue;
                _queue.RemoveAt(i);
                changed = true;
            }

            if (_current is not null && string.Equals(_current.Source, source, StringComparison.OrdinalIgnoreCase))
            {
                _current = _queue.Count > 0 ? DequeueLocked() : null;
                changed = true;
            }

            snapshot = CreateSnapshotLocked();
        }

        if (changed)
            RaiseChanged(snapshot);
    }

    private bool TryCoalesceLocked(AppNotification notification)
    {
        if (string.IsNullOrWhiteSpace(notification.DedupeKey))
            return false;

        if (_current is not null && IsSameDedupeKey(_current, notification))
        {
            _current = Coalesce(_current, notification);
            return true;
        }

        for (var i = 0; i < _queue.Count; i++)
        {
            if (!IsSameDedupeKey(_queue[i], notification))
                continue;
            _queue[i] = Coalesce(_queue[i], notification);
            return true;
        }

        return false;
    }

    private static bool IsSameDedupeKey(AppNotification a, AppNotification b) =>
        !string.IsNullOrWhiteSpace(a.DedupeKey) &&
        string.Equals(a.DedupeKey, b.DedupeKey, StringComparison.OrdinalIgnoreCase);

    private static AppNotification Coalesce(AppNotification existing, AppNotification latest) =>
        existing with
        {
            Title = latest.Title,
            Message = latest.Message,
            Severity = latest.Severity,
            ActionLabel = latest.ActionLabel,
            ActionRoute = latest.ActionRoute,
            Persistence = latest.Persistence,
            OccurrenceCount = existing.OccurrenceCount + 1
        };

    private static AppNotification Normalize(AppNotification notification)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Message);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Source);

        return notification with
        {
            Id = string.IsNullOrWhiteSpace(notification.Id) ? Guid.NewGuid().ToString("N") : notification.Id.Trim(),
            Title = notification.Title.Trim(),
            Message = notification.Message.Trim(),
            Source = notification.Source.Trim(),
            DedupeKey = string.IsNullOrWhiteSpace(notification.DedupeKey) ? null : notification.DedupeKey.Trim(),
            CreatedAt = notification.CreatedAt == default ? DateTimeOffset.UtcNow : notification.CreatedAt,
            OccurrenceCount = Math.Max(1, notification.OccurrenceCount)
        };
    }

    private AppNotification DequeueLocked()
    {
        var next = _queue[0];
        _queue.RemoveAt(0);
        return next;
    }

    private AppNotificationSnapshot CreateSnapshotLocked()
    {
        var queued = _queue.ToList();
        var active = _current is null
            ? queued.ToList()
            : new[] { _current }.Concat(queued).ToList();
        return new(_current, _queue.Count, queued, active);
    }

    private void RaiseChanged(AppNotificationSnapshot snapshot) =>
        Changed?.Invoke(this, new AppNotificationChangedEventArgs(snapshot));
}
