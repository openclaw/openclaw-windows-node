using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.ViewModels;
using System.Collections.ObjectModel;

namespace OpenClawTray.Controls;

public sealed partial class NotificationFlyoutContent : UserControl
{
    private readonly ObservableCollection<NotificationItemViewModel> _bellItems = new();
    private AppNotificationService? _appNotificationService;
    private Action? _openPage;

    public NotificationFlyoutContent()
    {
        InitializeComponent();
        BellNotificationsList.ItemsSource = _bellItems;
        Unloaded += (_, _) => Unbind();
    }

    internal void Initialize(AppNotificationService service, Action openPage)
    {
        Unbind();
        _appNotificationService = service;
        _openPage = openPage;
        service.Changed += OnNotificationsChanged;
        Render(service.Snapshot);
    }

    internal void Unbind()
    {
        if (_appNotificationService is not null)
            _appNotificationService.Changed -= OnNotificationsChanged;
        _appNotificationService = null;
        _openPage = null;
    }

    private void OnNotificationsChanged(object? sender, AppNotificationChangedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_appNotificationService is { } service && ReferenceEquals(sender, service) && IsLoaded)
                Render(service.Snapshot);
        });
    }

    private void Render(AppNotificationSnapshot snapshot)
    {
        SyncBellItems(snapshot.ActiveNotifications.Select(NotificationItemViewModel.From).ToList());
        SyncBellFlyoutEmptyState();
    }

    private void SyncBellItems(IReadOnlyList<NotificationItemViewModel> desiredItems)
    {
        var desiredIds = desiredItems
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);

        for (var i = _bellItems.Count - 1; i >= 0; i--)
        {
            if (!desiredIds.Contains(_bellItems[i].Id))
                _bellItems.RemoveAt(i);
        }

        for (var i = 0; i < desiredItems.Count; i++)
        {
            var item = desiredItems[i];
            if (i < _bellItems.Count && string.Equals(_bellItems[i].Id, item.Id, StringComparison.Ordinal))
            {
                if (!_bellItems[i].Equals(item))
                    _bellItems[i] = item;
                continue;
            }

            var existingIndex = -1;
            for (var j = i + 1; j < _bellItems.Count; j++)
            {
                if (string.Equals(_bellItems[j].Id, item.Id, StringComparison.Ordinal))
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                _bellItems.Move(existingIndex, i);
                if (!_bellItems[i].Equals(item))
                    _bellItems[i] = item;
            }
            else
            {
                _bellItems.Insert(i, item);
            }
        }
    }

    private void SyncBellFlyoutEmptyState()
    {
        var hasItems = _bellItems.Count > 0;

        if (BellNotificationsList is not null)
            BellNotificationsList.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        if (BellEmptyState is not null)
            BellEmptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        if (BellClearAllButton is not null)
            BellClearAllButton.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        if (BellActiveCountText is not null)
            BellActiveCountText.Text = hasItems
                ? LocalizationHelper.Format("NotificationsFlyout_ActiveCountFormat", _bellItems.Count)
                : string.Empty;
    }

    private void OnBellClearAllClick(object sender, RoutedEventArgs e)
        => _appNotificationService?.ClearAll();

    private void OnBellDismissNotificationClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string notificationId })
            _appNotificationService?.Dismiss(notificationId);
    }

    private void OnBellOpenPageClick(object sender, RoutedEventArgs e)
    {
        _openPage?.Invoke();
    }

}
