using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.ApplicationModel.DataTransfer;
using WinUIEx;

namespace OpenClawTray.Windows;

public sealed partial class ActivityStreamWindow : WindowEx
{
    private readonly Action<string?> _openDashboard;
    private string _currentFilter = "all";

    public bool IsClosed { get; private set; }

    public ActivityStreamWindow(Action<string?> openDashboard)
    {
        InitializeComponent();
        VisualTestCapture.CaptureOnLoaded(RootGrid, "Activity");
        Title = LocalizationHelper.GetString("WindowTitle_ActivityStream");

        _openDashboard = openDashboard;

        this.SetWindowSize(520, 640);
        this.CenterOnScreen();
        this.SetIcon(IconHelper.GetStatusIconPath(ConnectionStatus.Connected));

        Closed += OnClosed;
        ActivityStreamService.Updated += OnActivityUpdated;

        FilterCombo.SelectedIndex = 0;
        LoadActivity();

        var initialCount = ActivityStreamService.GetItems(ActivityStreamService.MaxStoredItems).Count;
        Logger.Info($"[ActivityStream] Window opened with {initialCount} items");
    }

    public void SetFilter(string? filter)
    {
        var normalized = NormalizeFilter(filter);
        _currentFilter = normalized;

        foreach (var item in FilterCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                FilterCombo.SelectedItem = item;
                break;
            }
        }

        LoadActivity();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        IsClosed = true;
        ActivityStreamService.Updated -= OnActivityUpdated;
    }

    private void OnActivityUpdated(object? sender, EventArgs e)
    {
        DispatcherQueue?.TryEnqueue(LoadActivity);
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FilterCombo.SelectedItem is ComboBoxItem item)
        {
            _currentFilter = item.Tag?.ToString() ?? "all";
            LoadActivity();
            var category = _currentFilter == "all" ? null : _currentFilter;
            var count = ActivityStreamService.GetItems(ActivityStreamService.MaxStoredItems, category).Count;
            Logger.Info($"[ActivityStream] Filter changed to '{_currentFilter}', {count} items");
        }
    }

    private void LoadActivity()
    {
        var category = _currentFilter == "all" ? null : _currentFilter;
        var items = ActivityStreamService.GetItems(ActivityStreamService.MaxStoredItems, category);
        Logger.Debug($"[ActivityStream] Items loaded: {items.Count} (filter={_currentFilter})");
        CountText.Text = $"({items.Count})";

        if (items.Count == 0)
        {
            ActivityList.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            return;
        }

        ActivityList.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        ActivityList.ItemsSource = items.Select(MapToViewModel).ToList();
    }

    private static ActivityViewModel MapToViewModel(ActivityStreamItem item)
    {
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Details))
            details.Add(item.Details);
        if (!string.IsNullOrWhiteSpace(item.SessionKey))
            details.Add($"session: {item.SessionKey}");
        if (!string.IsNullOrWhiteSpace(item.NodeId))
            details.Add($"node: {ShortId(item.NodeId)}");

        var detailText = string.Join(" · ", details);
        var canOpen = !string.IsNullOrWhiteSpace(item.DashboardPath);

        return new ActivityViewModel
        {
            Title = item.Title,
            Category = item.Category,
            TimeAgo = GetTimeAgo(item.Timestamp),
            DetailText = detailText,
            DetailVisibility = string.IsNullOrWhiteSpace(detailText) ? Visibility.Collapsed : Visibility.Visible,
            DashboardPath = item.DashboardPath,
            OpenHint = LocalizationHelper.GetString("Activity_ClickToOpen"),
            OpenHintVisibility = canOpen ? Visibility.Visible : Visibility.Collapsed
        };
    }

    private static string NormalizeFilter(string? filter)
    {
        return filter?.ToLowerInvariant() switch
        {
            "sessions" => "session",
            "usage" => "usage",
            "nodes" => "node",
            "notifications" => "notification",
            "invokes" => "node.invoke",
            "node.invoke" => "node.invoke",
            "channel" => "channel",
            "channels" => "channel",
            "gateway" => "gateway",
            "all" => "all",
            "session" => "session",
            "node" => "node",
            "notification" => "notification",
            _ => "all"
        };
    }

    private static string GetTimeAgo(DateTime timestamp)
    {
        var diff = DateTime.Now - timestamp;

        if (diff.TotalMinutes < 1) return LocalizationHelper.GetString("TimeAgo_JustNow");
        if (diff.TotalMinutes < 60) return string.Format(LocalizationHelper.GetString("TimeAgo_MinutesFormat"), (int)diff.TotalMinutes);
        if (diff.TotalHours < 24) return string.Format(LocalizationHelper.GetString("TimeAgo_HoursFormat"), (int)diff.TotalHours);
        if (diff.TotalDays < 7) return string.Format(LocalizationHelper.GetString("TimeAgo_DaysFormat"), (int)diff.TotalDays);
        return timestamp.ToString("MMM d, HH:mm");
    }

    private static string ShortId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return value.Length <= 12 ? value : value[..12] + "…";
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ActivityViewModel item &&
            !string.IsNullOrWhiteSpace(item.DashboardPath))
        {
            _openDashboard(item.DashboardPath);
        }
    }

    private void OnOpenDashboard(object sender, RoutedEventArgs e)
    {
        _openDashboard(null);
    }

    private void OnCopySupportBundle(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(ActivityStreamService.BuildSupportBundle());
        Clipboard.SetContent(package);
        Logger.Info("[ActivityStream] Copied support bundle");
    }

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        ActivityStreamService.Clear();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private class ActivityViewModel
    {
        public string Title { get; set; } = "";
        public string Category { get; set; } = "";
        public string TimeAgo { get; set; } = "";
        public string DetailText { get; set; } = "";
        public Visibility DetailVisibility { get; set; }
        public string? DashboardPath { get; set; }
        public string OpenHint { get; set; } = "";
        public Visibility OpenHintVisibility { get; set; }
    }
}
