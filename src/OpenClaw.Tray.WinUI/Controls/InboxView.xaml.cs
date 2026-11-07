// The Inbox list shared by the footer bell flyout and the full Inbox page: tabs,
// action-first rows that expand in place, approval decisions (Allow once / Always allow /
// Deny, sent through InboxFeed to OperatorApprovalDecisions), "Dismiss shown", the empty
// state and the notification settings shortcut. Data comes from InboxFeed (app notifications
// plus live Gateway facts); navigation and "Ask OpenClaw" are supplied by the hosting window.
//
// Usage:
//   inboxView.Initialize(app.InboxFeed, new InboxViewCommands(
//       OpenRoute: route => window.OpenNotificationRoute(route),
//       OpenSettings: () => window.OpenSettings("notification-preferences"),
//       OpenPage: () => window.ShowInboxPage(),
//       AskOpenClaw: question => window.AskOpenClaw(question)));
//   ...
//   inboxView.Unbind();

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Services;
using OpenClawTray.ViewModels;
using System.Collections.ObjectModel;

namespace OpenClawTray.Controls;

/// <summary>
/// Navigation supplied by the Inbox host. <paramref name="OpenPage"/> is null on the page itself;
/// <paramref name="AskOpenClaw"/> sends a failure's explain prompt to OpenClaw and shows the reply.
/// </summary>
internal sealed record InboxViewCommands(
    Action<string> OpenRoute,
    Action OpenSettings,
    Action? OpenPage = null,
    Action<string>? AskOpenClaw = null);

public sealed partial class InboxView : UserControl
{
    private const string PageMarkerAutomationId = "NotificationsPageMarker";
    private static readonly TimeSpan CountdownInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AgeInterval = TimeSpan.FromSeconds(30);
    private readonly ObservableCollection<InboxItemViewModel> _items = new();
    private readonly DispatcherQueueTimer _clock;
    private InboxFeed? _feed;
    private InboxViewCommands? _commands;
    private InboxSnapshot _snapshot = InboxSnapshot.Empty;
    private InboxTab _tab = InboxTab.All;

    public InboxView()
    {
        InitializeComponent();
        ItemsList.ItemsSource = _items;
        _clock = DispatcherQueue.CreateTimer();
        _clock.IsRepeating = true;
        _clock.Tick += (_, _) => RefreshClock();
        // Hosts own the binding lifetime (flyout Closed, page Unloaded). Unbinding here would let a
        // late Unloaded from a quick close and reopen disconnect the freshly opened Inbox.
    }

    /// <summary>Applies the full-page layout: page title, no width cap and no "Open Inbox" link.</summary>
    internal void UsePageLayout()
    {
        RootGrid.Width = double.NaN;
        RootGrid.MaxHeight = double.PositiveInfinity;
        TitleGlyph.Visibility = Visibility.Collapsed;
        TitleText.Style = (Style)Application.Current.Resources["TitleTextBlockStyle"];
        AutomationProperties.SetAutomationId(TitleText, PageMarkerAutomationId);
        FooterRow.Visibility = Visibility.Collapsed;
    }

    internal void Initialize(InboxFeed feed, InboxViewCommands commands)
    {
        Unbind();
        _feed = feed;
        _commands = commands;
        feed.Changed += OnFeedChanged;
        Render(feed.Snapshot);
    }

    internal void Unbind()
    {
        _clock.Stop();
        if (_feed is not null)
            _feed.Changed -= OnFeedChanged;
        _feed = null;
        _commands = null;
    }

    private void OnFeedChanged(object? sender, EventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_feed is { } feed && ReferenceEquals(sender, feed))
                Render(feed.Snapshot);
        });
    }

    private void Render(InboxSnapshot snapshot)
    {
        _snapshot = snapshot;
        ApplyTabLabels();
        SyncInboxItems(_snapshot.For(_tab), DateTimeOffset.UtcNow);
        ApplyChrome();
    }

    private void RefreshClock()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in _items)
            item.RefreshClock(now);
    }

    // Countdowns tick every second while an approval is shown; otherwise ages refresh slowly.
    private void UpdateClock()
    {
        if (_feed is null || _items.Count == 0)
        {
            _clock.Stop();
            return;
        }
        var interval = _items.Any(item => item.HasCountdown) ? CountdownInterval : AgeInterval;
        if (_clock.Interval != interval)
            _clock.Interval = interval;
        if (!_clock.IsRunning)
            _clock.Start();
    }

    private void ApplyTabLabels()
    {
        SetTabText(AllTab, "Inbox_TabAll", InboxTab.All);
        SetTabText(ApprovalsTab, "Inbox_TabApprovals", InboxTab.Approvals);
        SetTabText(MentionsTab, "Inbox_TabMentions", InboxTab.Mentions);
        SetTabText(AutomationsTab, "Inbox_TabAutomations", InboxTab.Automations);
        SetTabText(SystemTab, "Inbox_TabSystem", InboxTab.System);
    }

    private void SetTabText(SelectorBarItem item, string labelKey, InboxTab tab)
    {
        var label = LocalizationHelper.GetString(labelKey);
        var count = _snapshot.Count(tab);
        item.Text = count > 0 ? LocalizationHelper.Format("Inbox_TabCountFormat", label, count) : label;
        AutomationProperties.SetName(item, item.Text);
    }

    /// <summary>Reconciles rows by id so expansion state and focus survive updates.</summary>
    private void SyncInboxItems(IReadOnlyList<InboxEntry> entries, DateTimeOffset now)
    {
        var desiredIds = entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            if (!desiredIds.Contains(_items[i].Id))
                _items.RemoveAt(i);
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var existingIndex = -1;
            for (var j = i; j < _items.Count; j++)
            {
                if (string.Equals(_items[j].Id, entry.Id, StringComparison.Ordinal))
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                _items.Insert(i, InboxItemViewModel.From(entry, now));
                continue;
            }

            if (existingIndex != i)
                _items.Move(existingIndex, i);
            _items[i].UpdateFrom(entry, now);
        }
    }

    private void ApplyChrome()
    {
        var hasItems = _items.Count > 0;
        ItemsList.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        DismissShownButton.Visibility = InboxProjection.DismissibleIds(_snapshot, _tab).Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        var total = _snapshot.Entries.Count;
        CountText.Text = total > 0 ? LocalizationHelper.Format("Inbox_CountFormat", total) : string.Empty;
        UpdateClock();
    }

    private void OnTabSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is not string tag || !Enum.TryParse<InboxTab>(tag, out var tab))
            return;
        _tab = tab;
        SyncInboxItems(_snapshot.For(_tab), DateTimeOffset.UtcNow);
        ApplyChrome();
    }

    private void OnDismissShownClick(object sender, RoutedEventArgs e)
    {
        if (_feed is not { } feed)
            return;
        foreach (var id in InboxProjection.DismissibleIds(_snapshot, _tab))
            feed.Dismiss(id);
    }

    private void OnDismissClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id } &&
            _items.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal)) is { CanDismiss: true })
            _feed?.Dismiss(id);
    }

    private void OnActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: InboxItemViewModel item } ||
            string.IsNullOrWhiteSpace(item.ActionRoute) ||
            _commands is not { } commands)
            return;

        var feed = _feed;
        commands.OpenRoute(item.ActionRoute);
        // Opening the conversation is what a mention asks for; other entries stay until resolved.
        if (item.Kind == InboxEntryKind.Mention)
            feed?.Dismiss(item.Id);
    }

    private void OnSecondaryActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: InboxItemViewModel { SecondaryActionRoute: { Length: > 0 } route } })
            _commands?.OpenRoute(route);
    }

    private void OnAskClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: InboxItemViewModel { AskQuestion: { Length: > 0 } question } })
            _commands?.AskOpenClaw?.Invoke(question);
    }

    private void OnAllowOnceClick(object sender, RoutedEventArgs e) => Decide(sender, GatewayApprovalDecisions.AllowOnce);

    private void OnAllowAlwaysClick(object sender, RoutedEventArgs e) => Decide(sender, GatewayApprovalDecisions.AllowAlways);

    private void OnDenyClick(object sender, RoutedEventArgs e) => Decide(sender, GatewayApprovalDecisions.Deny);

    // A sent or already-resolved decision removes the row through the feed; anything else stays for retry.
    private void Decide(object sender, string decision)
    {
        if (sender is not FrameworkElement { Tag: InboxItemViewModel { GatewayItem: { } gatewayItem } item } ||
            _feed is not { } feed ||
            !item.DecisionsEnabled)
            return;

        AsyncEventHandlerGuard.Run(
            () => DecideAsync(feed, item, gatewayItem, decision),
            new AppLogger(),
            nameof(Decide),
            _ =>
            {
                item.IsDeciding = false;
                item.DecisionErrorText = LocalizationHelper.GetString("Inbox_ApprovalDecisionFailed");
            });
    }

    private static async Task DecideAsync(InboxFeed feed, InboxItemViewModel item, InboxGatewayItem gatewayItem, string decision)
    {
        item.IsDeciding = true;
        item.DecisionErrorText = "";
        try
        {
            var outcome = await feed.DecideApprovalAsync(gatewayItem, decision);
            item.DecisionErrorText = outcome switch
            {
                ApprovalDecisionOutcome.Failed => LocalizationHelper.GetString("Inbox_ApprovalDecisionFailed"),
                ApprovalDecisionOutcome.NotPending => LocalizationHelper.GetString("Inbox_ApprovalNoLongerPending"),
                ApprovalDecisionOutcome.NotAllowed => LocalizationHelper.GetString("Inbox_ApprovalDecisionUnavailable"),
                _ => "",
            };
        }
        finally
        {
            item.IsDeciding = false;
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => _commands?.OpenSettings();

    private void OnOpenPageClick(object sender, RoutedEventArgs e) => _commands?.OpenPage?.Invoke();
}
