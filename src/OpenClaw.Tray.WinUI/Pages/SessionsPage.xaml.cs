using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace OpenClawTray.Pages;

public sealed partial class SessionsPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current;
    private AppState? _appState;
    private SessionInfo[]? _allSessions;
    private string _activeChannel = "all";
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _refreshTimer;
    private readonly AsyncListLoadingState _sessionLoading = new();

    public SessionsPage()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            _refreshTimer?.Stop(); _refreshTimer = null;
            if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
        };
    }

    public void Initialize()
    {
        // Guard against duplicate subscriptions (NavigationCacheMode reuses page)
        if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
        _appState = CurrentApp.AppState;
        _appState.PropertyChanged += OnAppStateChanged;

        // Show "← Back to Connection" only when the user arrived from
        // Connection's cross-page link; staying hidden when the rail nav
        // is used keeps the page chrome quiet for direct navigation.
        var hub = CurrentApp.ActiveHubWindow as HubWindow;
        BackToConnectionLink.Visibility = hub?.LastNavigationOrigin == "connection"
            ? Visibility.Visible
            : Visibility.Collapsed;

        var client = CurrentApp.GatewayClient;
        if (client == null)
        {
            _sessionLoading.Fail();
            ShowDisconnected();
            ApplyFilter();
            return;
        }

        ConnectionInfoBar.IsOpen = false;

        if (_appState?.Sessions is { Length: > 0 } sessions)
        {
            _sessionLoading.Complete(sessions.Length);
            UpdateSessions(sessions);
            _sessionLoading.BeginRefresh();
            ApplyFilter();
        }
        else
        {
            _sessionLoading.BeginInitialRefresh();
            ApplyFilter();
        }

        _ = client.RequestSessionsAsync();
        _ = client.RequestModelsListAsync();
    }

    private void OnBackToConnectionClicked(object sender, RoutedEventArgs e)
        => ((IAppCommands)CurrentApp).Navigate("connection");

    private void OnOpenConnectionClick(object sender, RoutedEventArgs e)
        => ((IAppCommands)CurrentApp).Navigate("connection");

    public void UpdateSessions(SessionInfo[] sessions)
    {
        _allSessions = sessions;
        _sessionLoading.Complete(sessions.Length);
        RebuildChannelTabs();
        ApplyFilter();
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
        if (!_sessionLoading.HasLoaded)
        {
            SessionListView.ItemsSource = null;
            SessionListView.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Collapsed;
            LoadingState.Visibility = _sessionLoading.ShouldShowLoading ? Visibility.Visible : Visibility.Collapsed;
            RefreshButton.IsEnabled = CurrentApp.GatewayClient != null && _sessionLoading.CanEdit;
            ChannelSelector.IsEnabled = false;
            return;
        }

        IEnumerable<SessionInfo> filtered = _allSessions ?? Array.Empty<SessionInfo>();

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
            SessionListView.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = _sessionLoading.ShouldShowEmpty || _sessionLoading.HasLoaded ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            SessionListView.ItemsSource = viewModels;
            SessionListView.Visibility = Visibility.Visible;
            EmptyState.Visibility = Visibility.Collapsed;
        }

        LoadingState.Visibility = _sessionLoading.ShouldShowLoading ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = CurrentApp.GatewayClient != null && _sessionLoading.CanEdit;
        ChannelSelector.IsEnabled = _sessionLoading.HasLoaded && _sessionLoading.CanEdit;
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.Sessions):
                UpdateSessions(_appState!.Sessions);
                break;
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
            CanEdit = _sessionLoading.CanEdit,
        };
    }

    private void ChannelSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var selected = sender.SelectedItem;
        _activeChannel = selected == AllTab ? "all" : (selected?.Text ?? "all");
        ApplyFilter();
    }

    private async void OnResetSession(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string key)
        {
            var client = CurrentApp.GatewayClient;
            if (client == null) { ShowDisconnected(); return; }
            try { await client.ResetSessionAsync(key); }
            catch (Exception ex) { ShowActionFailure("Reset failed", ex); }
        }
    }

    private async void OnDeleteSession(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string key)
        {
            var client = CurrentApp.GatewayClient;
            if (client == null) { ShowDisconnected(); return; }
            try { await client.DeleteSessionAsync(key); }
            catch (Exception ex) { ShowActionFailure("Delete failed", ex); }
        }
    }

    private async void OnCompactSession(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string key)
        {
            var client = CurrentApp.GatewayClient;
            if (client == null) { ShowDisconnected(); return; }
            try { await client.CompactSessionAsync(key); }
            catch (Exception ex) { ShowActionFailure("Compact failed", ex); }
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        var client = CurrentApp.GatewayClient;
        if (client == null)
        {
            _sessionLoading.Fail();
            ShowDisconnected();
            ApplyFilter();
            return;
        }

        ConnectionInfoBar.IsOpen = false;
        _sessionLoading.BeginRefresh();
        ApplyFilter();
        _ = client.RequestSessionsAsync();
        _ = client.RequestModelsListAsync();

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

    private void ShowDisconnected()
    {
        ConnectionInfoBar.Title = "Gateway disconnected";
        ConnectionInfoBar.Message = "Connect to a gateway to load sessions.";
        ConnectionInfoBar.Severity = InfoBarSeverity.Warning;
        ConnectionInfoBar.IsOpen = true;
        RefreshButton.IsEnabled = false;
    }

    private void ShowActionFailure(string title, Exception ex)
    {
        ConnectionInfoBar.Title = title;
        ConnectionInfoBar.Message = ex.Message;
        ConnectionInfoBar.Severity = InfoBarSeverity.Error;
        ConnectionInfoBar.IsOpen = true;
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
    public bool CanEdit { get; set; } = true;
    public Visibility TokenRowVisibility => HasTokenData ? Visibility.Visible : Visibility.Collapsed;
}
