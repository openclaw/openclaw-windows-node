using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using OpenClaw.Shared;
using OpenClawTray.Services;
using OpenClawTray.ViewModels;
using System.Collections.ObjectModel;

namespace OpenClawTray.Pages;

public sealed partial class NotificationsPage : Page
{
    private const double CompactLayoutWidth = 720;
    private static App CurrentApp => (App)Application.Current!;
    private readonly ObservableCollection<NotificationItemViewModel> _notificationItems = new();
    private AppNotificationService? _notificationService;

    public NotificationsPage()
    {
        InitializeComponent();
        SizeChanged += OnLayoutSizeChanged;
        NotificationsList.ItemsSource = _notificationItems;
        Unloaded += OnUnloaded;
    }

    private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < CompactLayoutWidth;
        Grid.SetColumnSpan(PageHeading, compact ? 2 : 1);
        Grid.SetRow(ClearButton, compact ? 1 : 0);
        Grid.SetColumn(ClearButton, compact ? 0 : 1);
        ClearButton.HorizontalAlignment = HorizontalAlignment.Left;
    }

    private void OnNotificationLayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var grid = (Grid)sender;
        var content = (StackPanel)grid.FindName("NotificationContent");
        var actions = (StackPanel)grid.FindName("NotificationActions");
        var compact = e.NewSize.Width < CompactLayoutWidth;
        Grid.SetColumnSpan(content, compact ? 2 : 1);
        Grid.SetRow(actions, compact ? 1 : 0);
        Grid.SetColumn(actions, compact ? 1 : 2);
        Grid.SetColumnSpan(actions, compact ? 2 : 1);
        actions.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal;
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

        if (_notificationService is { } notifications)
            AsyncEventHandlerGuard.Run(
                () => notifications.NavigateActionAsync(item.Id, ((IAppCommands)CurrentApp).TryNavigateAsync),
                new AppLogger(), nameof(OnNotificationActionClick));
    }
}
