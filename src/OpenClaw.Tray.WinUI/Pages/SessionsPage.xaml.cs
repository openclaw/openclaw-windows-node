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

public sealed partial class SessionsPage : Page
{
    private HubWindow? _hub;
    private SessionInfo[]? _allSessions;
    private string _activeChannel = "all";
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _refreshTimer;

    public SessionsPage()
    {
        InitializeComponent();
        Unloaded += (_, _) => { _refreshTimer?.Stop(); _refreshTimer = null; };
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;

        if (hub.GatewayClient == null)
        {
            ConnectionWarning.IsOpen = true;
            EmptyState.Visibility = Visibility.Collapsed;
            SessionListView.ItemsSource = null;
            return;
        }

        ConnectionWarning.IsOpen = false;

        if (hub.LastSessions != null)
            UpdateSessions(hub.LastSessions);

        _ = hub.GatewayClient.RequestSessionsAsync();
        _ = hub.GatewayClient.RequestModelsListAsync();
    }

    public void UpdateSessions(SessionInfo[] sessions)
    {
        _allSessions = sessions;
        DispatcherQueue?.TryEnqueue(() =>
        {
            RebuildChannelTabs();
            ApplyFilter();
        });
    }

    public void UpdateModelsList(ModelsListInfo data)
    {
        // Models data received — re-render in case we want it later
    }

    private void RebuildChannelTabs()
    {
        if (_allSessions == null) return;

        var channels = _allSessions
            .Where(s => !string.IsNullOrWhiteSpace(s.Channel))
            .Select(s => s.Channel!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c)
            .ToList();

        // Keep "All" tab, clear dynamic tabs
        while (ChannelSelector.Items.Count > 1)
            ChannelSelector.Items.RemoveAt(ChannelSelector.Items.Count - 1);

        foreach (var ch in channels)
        {
            ChannelSelector.Items.Add(new SelectorBarItem { Text = ch });
        }
    }

    private void ApplyFilter()
    {
        if (_allSessions == null || _allSessions.Length == 0)
        {
            SessionListView.ItemsSource = null;
            EmptyState.Visibility = Visibility.Visible;
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;

        IEnumerable<SessionInfo> filtered = _allSessions;

        if (_activeChannel != "all")
        {
            filtered = filtered.Where(s =>
                string.Equals(s.Channel, _activeChannel, StringComparison.OrdinalIgnoreCase));
        }

        var viewModels = filtered
            .OrderByDescending(s => s.UpdatedAt ?? s.LastSeen)
            .Select(s => ToViewModel(s))
            .ToList();

        if (viewModels.Count == 0)
        {
            SessionListView.ItemsSource = null;
            EmptyState.Visibility = Visibility.Visible;
        }
        else
        {
            SessionListView.ItemsSource = viewModels;
        }
    }

    private SessionViewModel ToViewModel(SessionInfo s)
    {
        var isActive = s.Status == "active" || s.Status == "running";

        // Detail line: Provider · Model · Channel
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(s.Provider)) parts.Add(s.Provider!);
        if (!string.IsNullOrWhiteSpace(s.Model)) parts.Add(s.Model!);
        if (!string.IsNullOrWhiteSpace(s.Channel)) parts.Add(s.Channel!);

        // Token display
        var hasTokens = s.InputTokens > 0 || s.OutputTokens > 0;
        var tokensText = hasTokens
            ? $"↓{FormatTokenCount(s.InputTokens)} / ↑{FormatTokenCount(s.OutputTokens)}"
            : "";

        // Context % — ContextTokens is the window size, TotalTokens is usage
        double contextPercent = 0;
        if (s.ContextTokens > 0 && s.TotalTokens > 0)
            contextPercent = Math.Min(100.0, (double)s.TotalTokens / s.ContextTokens * 100.0);

        return new SessionViewModel
        {
            Key = s.Key,
            DisplayName = !string.IsNullOrWhiteSpace(s.DisplayName) ? s.DisplayName! : s.Key,
            AgeText = s.AgeText,
            DetailLine = parts.Count > 0 ? string.Join(" · ", parts) : "",
            StatusColor = new SolidColorBrush(isActive ? Colors.LimeGreen : Colors.Gray),
            TokensText = tokensText,
            ContextPercent = contextPercent,
            HasTokenData = hasTokens || contextPercent > 0,
        };
    }

    private void ChannelSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_hub == null) return;
        var selected = sender.SelectedItem;
        _activeChannel = selected == AllTab ? "all" : (selected?.Text ?? "all");
        ApplyFilter();
    }

    private async void OnResetSession(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string key)
        {
            var client = _hub?.GatewayClient;
            if (client == null) return;
            try { await client.ResetSessionAsync(key); }
            catch { }
        }
    }

    private async void OnDeleteSession(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string key)
        {
            var client = _hub?.GatewayClient;
            if (client == null) return;
            try { await client.DeleteSessionAsync(key); }
            catch { }
        }
    }

    private async void OnCompactSession(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string key)
        {
            var client = _hub?.GatewayClient;
            if (client == null) return;
            try { await client.CompactSessionAsync(key); }
            catch { }
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        var client = _hub?.GatewayClient;
        if (client != null)
        {
            _ = client.RequestSessionsAsync();
            _ = client.RequestModelsListAsync();
        }

        if (RefreshButton.Content is StackPanel)
        {
            // Temporarily update the text inside the StackPanel
            var sp = (StackPanel)RefreshButton.Content;
            if (sp.Children.Count > 1 && sp.Children[1] is TextBlock tb)
            {
                tb.Text = "Refreshing...";
                _refreshTimer?.Stop();
                _refreshTimer = DispatcherQueue.CreateTimer();
                _refreshTimer.Interval = TimeSpan.FromSeconds(1);
                _refreshTimer.Tick += (t, a) => { tb.Text = "Refresh"; _refreshTimer.Stop(); };
                _refreshTimer.Start();
            }
        }
    }

    private static string FormatTokenCount(long n)
    {
        if (n >= 1_000_000) return $"{n / 1_000_000.0:0.#}M";
        if (n >= 1_000) return $"{n / 1_000.0:0.#}K";
        return n.ToString();
    }
}

public class SessionViewModel
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string AgeText { get; set; } = "";
    public string DetailLine { get; set; } = "";
    public SolidColorBrush StatusColor { get; set; } = new(Colors.Gray);
    public string TokensText { get; set; } = "";
    public double ContextPercent { get; set; }
    public bool HasTokenData { get; set; }
    public Visibility TokenRowVisibility => HasTokenData ? Visibility.Visible : Visibility.Collapsed;
}
