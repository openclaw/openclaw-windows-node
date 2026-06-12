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

    private sealed record NotificationItemViewModel(
        string Id,
        string SeverityGlyph,
        string Title,
        string Message,
        string Metadata,
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
            "exec-approval" => LocalizationHelper.GetString("NotificationsPage_MetadataSourceExecApproval"),
            "node.invoke" => LocalizationHelper.GetString("NotificationsPage_MetadataCategoryNodeInvoke"),
            _ => value
        };
    }
}
