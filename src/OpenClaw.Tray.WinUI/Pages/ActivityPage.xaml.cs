using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClawTray.Pages;

public sealed partial class ActivityPage : Page
{
    private HubWindow? _hub;
    private string _currentFilter = "all";
    private bool _initialized;

    public ActivityPage()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ActivityStreamService.Updated -= OnActivityUpdated;
        _initialized = false;
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        if (!_initialized)
        {
            ActivityStreamService.Updated += OnActivityUpdated;
            _initialized = true;
        }
        LoadActivity();
    }

    private void OnActivityUpdated(object? sender, EventArgs e)
    {
        DispatcherQueue?.TryEnqueue(LoadActivity);
    }

    private void LoadActivity()
    {
        var category = _currentFilter == "all" ? null : _currentFilter;
        var items = ActivityStreamService.GetItems(200, category);
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

    public void SetFilter(string? filter)
    {
        var normalized = NormalizeFilter(filter);
        foreach (var child in FilterPanel.Children.OfType<ToggleButton>())
            child.IsChecked = string.Equals(child.Tag?.ToString(), normalized, StringComparison.Ordinal);

        _currentFilter = normalized;
        LoadActivity();
    }

    private static string NormalizeFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return "all";

        var normalized = filter.Trim().ToLowerInvariant();
        return normalized switch
        {
            "sessions" => "session",
            "nodes" => "node",
            "notifications" or "history" => "notification",
            "usage" or "session" or "node" or "notification" => normalized,
            _ => "all"
        };
    }

    private static ActivityViewModel MapToViewModel(ActivityStreamItem item)
    {
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Details)) details.Add(item.Details);
        if (!string.IsNullOrWhiteSpace(item.SessionKey)) details.Add($"session: {item.SessionKey}");
        var detailText = string.Join(" · ", details);

        return new ActivityViewModel
        {
            Title = item.Title,
            Icon = item.Icon,
            IconVisibility = string.IsNullOrWhiteSpace(item.Icon) ? Visibility.Collapsed : Visibility.Visible,
            Category = item.Category,
            TimeAgo = GetTimeAgo(item.Timestamp),
            DetailText = detailText,
            DetailVisibility = string.IsNullOrWhiteSpace(detailText) ? Visibility.Collapsed : Visibility.Visible
        };
    }

    private static string GetTimeAgo(DateTime timestamp)
    {
        var diff = DateTime.Now - timestamp;
        if (diff.TotalMinutes < 1) return "just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
        return timestamp.ToString("MMM d, HH:mm");
    }

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton btn)
        {
            foreach (var child in FilterPanel.Children.OfType<ToggleButton>())
            {
                if (child != btn) child.IsChecked = false;
            }
            btn.IsChecked = true;
            _currentFilter = btn.Tag?.ToString() ?? "all";
            LoadActivity();
        }
    }

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        ActivityStreamService.Clear();
        LoadActivity();
    }

    private class ActivityViewModel
    {
        public string Title { get; set; } = "";
        public string Icon { get; set; } = "";
        public Visibility IconVisibility { get; set; }
        public string Category { get; set; } = "";
        public string TimeAgo { get; set; } = "";
        public string DetailText { get; set; } = "";
        public Visibility DetailVisibility { get; set; }
    }
}
