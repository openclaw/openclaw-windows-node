// The Inbox's single data feed: app notifications from AppNotificationService plus
// live Gateway facts from InboxGatewaySource, projected through InboxProjection.
// Hosts (the bell flyout, the full Inbox page and the footer badge) observe this
// instead of either source so rows, tab counts and the badge always agree.
//
// Usage:
//   var feed = new InboxFeed(appNotifications, inboxGatewaySource);
//   feed.Changed += (_, _) => dispatcher.TryEnqueue(() => Render(feed.Snapshot));
//   feed.Dismiss(entry.Id);

using OpenClawTray.Presentation;

namespace OpenClawTray.Services;

internal sealed class InboxFeed
{
    private readonly AppNotificationService _notifications;
    private readonly InboxGatewaySource? _gateway;

    public InboxFeed(AppNotificationService notifications, InboxGatewaySource? gateway)
    {
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _gateway = gateway;
        _notifications.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        if (_gateway is not null)
            _gateway.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when either source changed, possibly off the UI thread.</summary>
    public event EventHandler? Changed;

    public InboxSnapshot Snapshot =>
        InboxProjection.Build(_notifications.Snapshot.ActiveNotifications, _gateway?.Items);

    /// <summary>Only app reminders participate in navigation admission; Gateway facts keep their own lifetime.</summary>
    public Task<bool> NavigateNotificationAsync(string id, Func<string, string?, Task<bool>> tryNavigate) =>
        InboxGatewayItems.IsGatewayItemId(id)
            ? Task.FromResult(false)
            : _notifications.NavigateActionAsync(id, tryNavigate);

    public void Dismiss(string id)
    {
        if (InboxGatewayItems.IsGatewayItemId(id))
            _gateway?.Dismiss(id);
        else
            _notifications.Dismiss(id);
    }
}
