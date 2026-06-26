using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

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
            if (CurrentApp.ActiveHubWindow is OpenClawTray.Windows.HubWindow hub)
                hub.PendingChatSessionKey = sessionKey;
            ((IAppCommands)CurrentApp).Navigate("chat");
            _notificationService?.Dismiss(item.Id);
            return;
        }

        ((IAppCommands)CurrentApp).Navigate(item.ActionRoute);
        _notificationService?.Dismiss(item.Id);
    }

    private sealed record NotificationItemViewModel(
        string Id,
        string SeverityGlyph,
        string Title,
        string Message,
        string Metadata,
        string? ActionLabel,
        string? ActionRoute,
        Visibility ActionVisibility,
        string OccurrenceText,
        Visibility OccurrenceVisibility,
        string DismissAutomationName)
    {
        public static NotificationItemViewModel From(AppNotification notification)
        {
            var metadata = new[]
                {
                    LocalizeMetadataValue(notification.Source),
                    LocalizeMetadataValue(notification.Category),
                    notification.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                }
                .Where(value => !string.IsNullOrWhiteSpace(value));

            var occurrenceText = LocalizationHelper.Format(
                "AppNotification_RepeatedBadgeFormat",
                notification.OccurrenceCount);

            return new(
                notification.Id,
                ToSeverityGlyph(notification.Severity),
                notification.Title,
                notification.Message,
                string.Join(" - ", metadata),
                notification.ActionLabel,
                notification.ActionRoute,
                !string.IsNullOrWhiteSpace(notification.ActionLabel) &&
                !string.IsNullOrWhiteSpace(notification.ActionRoute)
                    ? Visibility.Visible
                    : Visibility.Collapsed,
                occurrenceText,
                notification.OccurrenceCount > 1 ? Visibility.Visible : Visibility.Collapsed,
                LocalizationHelper.Format("NotificationsPage_DismissAutomationNameFormat", notification.Title));
        }

        private static string ToSeverityGlyph(AppNotificationSeverity severity) => severity switch
        {
            AppNotificationSeverity.Success => "\uE930",
            AppNotificationSeverity.Warning => "\uE7BA",
            AppNotificationSeverity.Error => "\uE783",
            _ => "\uE946"
        };

        private static string? LocalizeMetadataValue(string? value) => value switch
        {
            null or "" => null,
            "authentication" => LocalizationHelper.GetString("NotificationsPage_MetadataAuthentication"),
            "bindings" => LocalizationHelper.GetString("NotificationsPage_MetadataBindings"),
            "channels" => LocalizationHelper.GetString("NotificationsPage_MetadataChannels"),
            "config" => LocalizationHelper.GetString("NotificationsPage_MetadataConfig"),
            "connection" => LocalizationHelper.GetString("NotificationsPage_MetadataConnection"),
            "cron" => LocalizationHelper.GetString("NotificationsPage_MetadataCron"),
            "exec-approval" => LocalizationHelper.GetString("NotificationsPage_MetadataSourceExecApproval"),
            "gateway" => LocalizationHelper.GetString("NotificationsPage_MetadataGateway"),
            "jobs" => LocalizationHelper.GetString("NotificationsPage_MetadataJobs"),
            "load" => LocalizationHelper.GetString("NotificationsPage_MetadataLoad"),
            "lifecycle" => LocalizationHelper.GetString("NotificationsPage_MetadataLifecycle"),
            "local-gateway" => LocalizationHelper.GetString("NotificationsPage_MetadataLocalGateway"),
            "node.invoke" => LocalizationHelper.GetString("NotificationsPage_MetadataCategoryNodeInvoke"),
            "node" => LocalizationHelper.GetString("NotificationsPage_MetadataNode"),
            "pairing" => LocalizationHelper.GetString("NotificationsPage_MetadataPairing"),
            "sandbox" => LocalizationHelper.GetString("NotificationsPage_MetadataSandbox"),
            "settings" => LocalizationHelper.GetString("NotificationsPage_MetadataSettings"),
            "status" => LocalizationHelper.GetString("NotificationsPage_MetadataStatus"),
            "system.run" => LocalizationHelper.GetString("NotificationsPage_MetadataSystemRun"),
            _ => value
        };
    }
}
