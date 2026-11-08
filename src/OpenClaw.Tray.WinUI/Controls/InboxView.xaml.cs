// The Inbox list shared by the footer bell flyout and the full Inbox page: tabs,
// action-first rows that expand in place, "Dismiss shown", the empty state and the
// notification settings shortcut. Data comes from AppNotificationService through
// InboxProjection; navigation is supplied by the hosting window.
//
// Usage:
//   inboxView.Initialize(appNotifications, new InboxViewCommands(
//       OpenRoute: route => window.OpenNotificationRoute(route),
//       OpenSettings: () => window.OpenSettings("notification-preferences"),
//       OpenPage: () => window.ShowInboxPage()));
//   ...
//   inboxView.Unbind();

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Services;
using OpenClawTray.ViewModels;
using System.Collections.ObjectModel;

namespace OpenClawTray.Controls;

/// <summary>Navigation supplied by the Inbox host. <paramref name="OpenPage"/> is null on the page itself.</summary>
internal sealed record InboxViewCommands(Action<string> OpenRoute, Action OpenSettings, Action? OpenPage = null);

public sealed partial class InboxView : UserControl
{
    private const string PageMarkerAutomationId = "NotificationsPageMarker";
    private readonly ObservableCollection<InboxItemViewModel> _items = new();
    private AppNotificationService? _service;
    private InboxViewCommands? _commands;
    private InboxSnapshot _snapshot = InboxSnapshot.Empty;
    private InboxTab _tab = InboxTab.All;

    public InboxView()
    {
        InitializeComponent();
        ItemsList.ItemsSource = _items;
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
        // The compact title-to-tabs spacing is only for the flyout; the page keeps the standard spacing.
        TabsBar.ClearValue(FrameworkElement.MarginProperty);
        TabsBar.ClearValue(Control.PaddingProperty);
    }

    internal void Initialize(AppNotificationService service, InboxViewCommands commands)
    {
        Unbind();
        _service = service;
        _commands = commands;
        service.Changed += OnNotificationsChanged;
        Render(service.Snapshot);
    }

    internal void Unbind()
    {
        if (_service is not null)
            _service.Changed -= OnNotificationsChanged;
        _service = null;
        _commands = null;
    }

    private void OnNotificationsChanged(object? sender, AppNotificationChangedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_service is { } service && ReferenceEquals(sender, service))
                Render(service.Snapshot);
        });
    }

    private void Render(AppNotificationSnapshot snapshot)
    {
        _snapshot = InboxProjection.Build(snapshot.ActiveNotifications);
        ApplyTabLabels();
        SyncInboxItems(_snapshot.For(_tab), DateTimeOffset.UtcNow);
        ApplyChrome();
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
        if (_service is not { } service)
            return;
        foreach (var id in InboxProjection.DismissibleIds(_snapshot, _tab))
            service.Dismiss(id);
    }

    private void OnDismissClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id } &&
            _items.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal)) is { CanDismiss: true })
            _service?.Dismiss(id);
    }

    private void OnActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: InboxItemViewModel item } ||
            string.IsNullOrWhiteSpace(item.ActionRoute) ||
            _commands is not { } commands)
            return;

        var service = _service;
        commands.OpenRoute(item.ActionRoute);
        // Opening the conversation is what a mention asks for; other entries stay until resolved.
        if (item.Kind == InboxEntryKind.Mention)
            service?.Dismiss(item.Id);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => _commands?.OpenSettings();

    private void OnOpenPageClick(object sender, RoutedEventArgs e) => _commands?.OpenPage?.Invoke();
}
