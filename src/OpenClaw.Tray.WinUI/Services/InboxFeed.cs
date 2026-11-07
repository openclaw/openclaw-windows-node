// The Inbox's single data feed: app notifications from AppNotificationService plus
// live Gateway facts from InboxGatewaySource, projected through InboxProjection.
// Hosts (the bell flyout, the full Inbox page and the footer badge) observe this
// instead of either source so rows, tab counts and the badge always agree.
//
// Usage:
//   var feed = new InboxFeed(appNotifications, inboxGatewaySource, approvalDecisions);
//   feed.Changed += (_, _) => dispatcher.TryEnqueue(() => Render(feed.Snapshot));
//   feed.Dismiss(entry.Id);
//   var outcome = await feed.DecideApprovalAsync(entry.GatewayItem!, GatewayApprovalDecisions.AllowOnce);

using OpenClawTray.Presentation;

namespace OpenClawTray.Services;

internal sealed class InboxFeed
{
    private readonly AppNotificationService _notifications;
    private readonly InboxGatewaySource? _gateway;
    private readonly OperatorApprovalDecisions? _decisions;

    public InboxFeed(AppNotificationService notifications, InboxGatewaySource? gateway, OperatorApprovalDecisions? decisions = null)
    {
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _gateway = gateway;
        _decisions = decisions;
        _notifications.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        if (_gateway is not null)
            _gateway.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when either source changed, possibly off the UI thread.</summary>
    public event EventHandler? Changed;

    public InboxSnapshot Snapshot =>
        InboxProjection.Build(_notifications.Snapshot.ActiveNotifications, _gateway?.Items);

    /// <summary>Sends an approval decision for an Inbox row through the shared decision owner. UI thread.</summary>
    public Task<ApprovalDecisionOutcome> DecideApprovalAsync(InboxGatewayItem item, string decision) =>
        _decisions is not null && item.Approval is { } approval
            ? _decisions.DecideAsync(approval.Id, decision)
            : Task.FromResult(ApprovalDecisionOutcome.NotPending);

    public void Dismiss(string id)
    {
        if (InboxGatewayItems.IsGatewayItemId(id))
            _gateway?.Dismiss(id);
        else
            _notifications.Dismiss(id);
    }
}
