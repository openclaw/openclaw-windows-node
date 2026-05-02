using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClawTray.Pages;

public sealed partial class ConversationsPage : Page
{
    private HubWindow? _hub;
    private SessionInfo[]? _allSessions;

    public ConversationsPage()
    {
        InitializeComponent();
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;

        if (hub.CurrentStatus != ConnectionStatus.Connected || hub.GatewayClient == null)
        {
            ConnectionWarning.IsOpen = true;
            EmptyState.Visibility = Visibility.Collapsed;
            SessionListView.ItemsSource = null;
            return;
        }

        ConnectionWarning.IsOpen = false;

        // Use cached data immediately, then request fresh
        if (hub.LastSessions != null)
            UpdateSessions(hub.LastSessions);

        _ = hub.GatewayClient.RequestSessionsAsync();
    }

    public void UpdateSessions(SessionInfo[] sessions)
    {
        _allSessions = sessions;
        DispatcherQueue?.TryEnqueue(() => ApplyFilter());
    }

    private void ApplyFilter()
    {
        if (_allSessions == null || _allSessions.Length == 0)
        {
            if (SessionListView != null) SessionListView.ItemsSource = null;
            if (EmptyState != null) EmptyState.Visibility = Visibility.Visible;
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;

        var filterTag = "all";
        if (FilterCombo.SelectedItem is ComboBoxItem combo)
            filterTag = combo.Tag as string ?? "all";

        IEnumerable<SessionInfo> ordered = _allSessions.OrderByDescending(s => s.UpdatedAt ?? s.LastSeen);

        List<ConversationViewModel> viewModels;

        switch (filterTag)
        {
            case "channel":
                // Group by channel, flatten with group headers in details
                viewModels = ordered.Select(s => ToViewModel(s, groupLabel: s.Channel ?? "unknown")).ToList();
                break;
            case "status":
                viewModels = ordered.Select(s => ToViewModel(s, groupLabel: s.Status)).ToList();
                break;
            default:
                viewModels = ordered.Select(s => ToViewModel(s)).ToList();
                break;
        }

        SessionListView.ItemsSource = viewModels;
    }

    private static ConversationViewModel ToViewModel(SessionInfo s, string? groupLabel = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.Channel)) parts.Add(s.Channel!);
        if (!string.IsNullOrWhiteSpace(s.Model)) parts.Add(s.Model!);
        if (!string.IsNullOrWhiteSpace(s.DisplayName)) parts.Add(s.DisplayName!);
        if (s.TotalTokens > 0) parts.Add($"{FormatTokenCount(s.TotalTokens)} tokens");
        if (groupLabel != null) parts.Insert(0, $"[{groupLabel}]");

        var isActive = s.Status == "active" || s.Status == "running";

        return new ConversationViewModel
        {
            Key = s.Key,
            AgeText = s.AgeText,
            Details = string.Join(" · ", parts),
            StatusColor = new SolidColorBrush(isActive ? Colors.LimeGreen : Colors.Gray),
        };
    }

    private void FilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Guard against firing during InitializeComponent before page is fully initialized
        if (_hub == null) return;
        ApplyFilter();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_hub?.GatewayClient != null)
            await _hub.GatewayClient.RequestSessionsAsync();
    }

    private static string FormatTokenCount(long n)
    {
        if (n >= 1_000_000) return $"{n / 1_000_000.0:0.#}M";
        if (n >= 1_000) return $"{n / 1_000.0:0.#}K";
        return n.ToString();
    }
}

public class ConversationViewModel
{
    public string Key { get; set; } = "";
    public string AgeText { get; set; } = "";
    public string Details { get; set; } = "";
    public SolidColorBrush StatusColor { get; set; } = new(Colors.Gray);
}
