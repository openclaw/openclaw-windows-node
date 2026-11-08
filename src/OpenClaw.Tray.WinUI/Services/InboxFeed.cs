// The Inbox's single data feed: app notifications from AppNotificationService plus
// live Gateway facts from InboxGatewaySource, projected through InboxProjection, and the
// persisted Do not disturb preference. Hosts (the bell flyout, the full Inbox page, the
// footer badge and the settings banner) observe this instead of either source so rows,
// tab counts, the badge and the Do not disturb toggle always agree.
//
// Usage:
//   var feed = new InboxFeed(appNotifications, inboxGatewaySource, approvalDecisions, settingsStore);
//   feed.Changed += (_, _) => dispatcher.TryEnqueue(() => Render(feed.Snapshot));
//   feed.Dismiss(entry.Id);
//   feed.SetDoNotDisturb(true);
//   var outcome = await feed.DecideApprovalAsync(entry.GatewayItem!, GatewayApprovalDecisions.AllowOnce);

using OpenClawTray.Presentation;

namespace OpenClawTray.Services;

internal sealed class InboxFeed
{
    private readonly AppNotificationService _notifications;
    private readonly InboxGatewaySource? _gateway;
    private readonly OperatorApprovalDecisions? _decisions;
    private ISettingsStore? _settings;
    private SettingsWriteOrigin? _settingsOrigin;
    private readonly object _doNotDisturbGate = new();
    private bool _doNotDisturb;
    private long _settingsVersion = long.MinValue;

    public InboxFeed(
        AppNotificationService notifications,
        InboxGatewaySource? gateway,
        OperatorApprovalDecisions? decisions = null,
        ISettingsStore? settings = null)
    {
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _gateway = gateway;
        _decisions = decisions;
        _notifications.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        if (_gateway is not null)
            _gateway.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        if (settings is not null)
            AttachSettings(settings);
    }

    /// <summary>
    /// Connects the settings owner that persists Do not disturb. App attaches it once the
    /// service provider exists, after the feed itself was created; later calls are ignored.
    /// </summary>
    public void AttachSettings(ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_settings is not null)
            return;
        _settings = settings;
        _settingsOrigin = settings.CreateOrigin();
        settings.Changed += OnSettingsChanged;
        ApplyDoNotDisturb(settings.Current, raise: true);
    }

    /// <summary>Raised when either source or Do not disturb changed, possibly off the UI thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised only when Do not disturb turned on or off, possibly off the UI thread.</summary>
    public event EventHandler? DoNotDisturbChanged;

    /// <summary>The persisted Do not disturb preference; false when no settings store is attached.</summary>
    public bool DoNotDisturb
    {
        get
        {
            lock (_doNotDisturbGate)
                return _doNotDisturb;
        }
    }

    public InboxSnapshot Snapshot =>
        InboxProjection.Build(_notifications.Snapshot.ActiveNotifications, _gateway?.Items) with
        {
            DoNotDisturb = DoNotDisturb,
        };

    /// <summary>Persists Do not disturb through the settings owner; a field-scoped save.</summary>
    public void SetDoNotDisturb(bool value)
    {
        if (_settings is not { } settings || DoNotDisturb == value)
            return;
        settings.Update(_settingsOrigin, editor => editor.DoNotDisturb = value);
        ApplyDoNotDisturb(settings.Current, raise: true);
    }

    /// <summary>Sends an approval decision for an Inbox row through the shared decision owner. UI thread.</summary>
    public Task<ApprovalDecisionOutcome> DecideApprovalAsync(InboxGatewayItem item, string decision) =>
        _decisions is not null && item.Approval is { } approval
            ? _decisions.DecideAsync(approval.Id, decision, item.ApprovalObservation)
            : Task.FromResult(ApprovalDecisionOutcome.NotPending);

    public void Dismiss(string id)
    {
        if (InboxGatewayItems.IsGatewayItemId(id))
            _gateway?.Dismiss(id);
        else
            _notifications.Dismiss(id);
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs args) =>
        ApplyDoNotDisturb(args.Snapshot, raise: true);

    // Change events can be delivered after a newer save was already applied; only newer snapshots win.
    private void ApplyDoNotDisturb(SettingsSnapshot snapshot, bool raise)
    {
        lock (_doNotDisturbGate)
        {
            if (snapshot.Version < _settingsVersion)
                return;
            _settingsVersion = snapshot.Version;
            if (_doNotDisturb == snapshot.DoNotDisturb)
                return;
            _doNotDisturb = snapshot.DoNotDisturb;
        }
        if (!raise)
            return;
        DoNotDisturbChanged?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
