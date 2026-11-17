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

/// <summary>Retains one dedicated conversation per Gateway, including a creation still awaiting acknowledgment.</summary>
internal sealed class DesktopCompanionChatSessions
{
    private readonly Dictionary<string, Task<string>> _sessions = new(StringComparer.Ordinal);

    public Task<string> GetOrCreateAsync(string gatewayId, Func<Task<string>> create, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Task<string> session;
        lock (_sessions)
        {
            if (!_sessions.TryGetValue(gatewayId, out session!))
            {
                var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                session = completion.Task;
                _sessions.Add(gatewayId, session);
                _ = CreateAsync(gatewayId, create, completion);
            }
        }
        // Closing the bubble cancels the caller, not a possibly accepted sessions.create.
        return session.WaitAsync(ct);
    }

    public void Select(string gatewayId, string sessionKey)
    {
        lock (_sessions) _sessions[gatewayId] = Task.FromResult(sessionKey);
    }

    private async Task CreateAsync(string gatewayId, Func<Task<string>> create, TaskCompletionSource<string> completion)
    {
        try
        {
            var key = await create();
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException("The Gateway did not return a dedicated conversation.");
            completion.TrySetResult(key);
        }
        catch (Exception ex)
        {
            lock (_sessions)
                if (_sessions.TryGetValue(gatewayId, out var current) && ReferenceEquals(current, completion.Task))
                    _sessions.Remove(gatewayId);
            completion.TrySetException(ex);
        }
    }
}
