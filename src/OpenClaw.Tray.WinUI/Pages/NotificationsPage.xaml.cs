using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using OpenClawTray.Services;
using OpenClawTray.ViewModels;
using System.Collections.ObjectModel;

namespace OpenClawTray.Pages;

public sealed partial class NotificationsPage : Page
{
    private static App CurrentApp => (App)Application.Current!;
    private readonly ObservableCollection<NotificationItemViewModel> _notificationItems = new();
    private AppNotificationService? _notificationService;

    public NotificationsPage()
    {
        InitializeComponent();
        NotificationsList.ItemsSource = _notificationItems;
        Unloaded += OnUnloaded;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new NotificationsPageAutomationPeer(this);

    // Page has no default peer. Expose the empty destination and its localized heading
    // so the keyboard focus handoff is also announced by assistive technology.
    private sealed class NotificationsPageAutomationPeer(NotificationsPage owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override string GetClassNameCore() => nameof(NotificationsPage);
    }

    internal void Initialize(AppNotificationService? notificationService)
    {
        if (!ReferenceEquals(_notificationService, notificationService))
        {
            if (_notificationService is not null)
                _notificationService.Changed -= OnNotificationsChanged;

            _notificationService = notificationService;

            if (_notificationService is not null)
                _notificationService.Changed += OnNotificationsChanged;
        }

        Render(notificationService?.Snapshot);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_notificationService is not null)
            _notificationService.Changed -= OnNotificationsChanged;
        _notificationService = null;
    }

    private void OnNotificationsChanged(object? sender, AppNotificationChangedEventArgs e)
    {
        DispatcherQueue?.TryEnqueue(() => Render(e.Snapshot));
    }

    private void Render(AppNotificationSnapshot? snapshot)
    {
        _notificationItems.Clear();

        if (snapshot is not null)
        {
            foreach (var notification in snapshot.ActiveNotifications)
                _notificationItems.Add(NotificationItemViewModel.From(notification));
        }

        var hasNotifications = _notificationItems.Count > 0;
        NotificationsList.Visibility = hasNotifications ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = hasNotifications ? Visibility.Collapsed : Visibility.Visible;
        ClearButton.IsEnabled = hasNotifications;
        // An empty inbox has no enabled child control. Keep a named destination for
        // the workspace focus handoff instead of leaving focus in the Settings pane.
        IsTabStop = !hasNotifications;
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
        => _notificationService?.ClearAll();

    private void OnDismissNotificationClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string notificationId })
            return;

        _notificationService?.Dismiss(notificationId);
    }

    private void OnNotificationActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: NotificationItemViewModel item })
            return;

        if (string.IsNullOrWhiteSpace(item.ActionRoute))
            return;

        if (AppNotificationActionRoutes.TryGetChatSessionKey(item.ActionRoute, out var sessionKey))
        {
            CurrentApp.PendingChatSessionKey = sessionKey;
            ((IAppCommands)CurrentApp).Navigate("chat");
            _notificationService?.Dismiss(item.Id);
            return;
        }

        ((IAppCommands)CurrentApp).Navigate(item.ActionRoute);
        _notificationService?.Dismiss(item.Id);
    }
}
