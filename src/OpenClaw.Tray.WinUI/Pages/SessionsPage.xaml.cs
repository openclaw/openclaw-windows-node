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
        // Drop cron-spawned sessions (key shape "agent:<id>:cron" — slot is
        // the third ":"-separated part). They have their own home on the
        // Cron page; surfacing them here overcrowds the conversation list.
        _allSessions = sessions
            .Where(s => !IsCronSession(s))
            .ToArray();
        _sessionLoading.Complete(_allSessions.Length);
        RebuildChannelTabs();
        ApplyFilter();
    }

    private static bool IsCronSession(SessionInfo s)
    {
        if (string.IsNullOrEmpty(s.Key)) return false;
        var parts = s.Key.Split(':');
        return parts.Length >= 3
               && string.Equals(parts[2], "cron", StringComparison.OrdinalIgnoreCase);
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
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(s.Provider)) parts.Add(s.Provider!);
        if (!string.IsNullOrWhiteSpace(s.Model)) parts.Add(s.Model!);
        if (!string.IsNullOrWhiteSpace(s.Channel)) parts.Add(s.Channel!);

        var hasTokens = s.InputTokens > 0 || s.OutputTokens > 0;
        var tokensText = hasTokens
            ? $"↓{FormatTokenCount(s.InputTokens)} / ↑{FormatTokenCount(s.OutputTokens)}"
            : "";

        // ContextTokens is the window size, TotalTokens is usage.
        double contextPercent = 0;
        if (s.ContextTokens > 0 && s.TotalTokens > 0)
            contextPercent = Math.Min(100.0, (double)s.TotalTokens / s.ContextTokens * 100.0);

        return new SessionViewModel
        {
            Key = s.Key,
            DisplayName = !string.IsNullOrWhiteSpace(s.DisplayName) ? s.DisplayName! : s.Key,
            AgeText = s.AgeText,
            DetailLine = parts.Count > 0 ? string.Join(" · ", parts) : "",
            StatusBrush = ResolveStatusBrush(s),
            StatusTooltip = ResolveStatusTooltip(s),
            TokensText = tokensText,
            ContextPercent = contextPercent,
            HasTokenData = hasTokens || contextPercent > 0,
            CanEdit = _sessionLoading.CanEdit,
        };
    }

    private static Brush ResolveStatusBrush(SessionInfo s)
    {
        var status = s.Status?.Trim().ToLowerInvariant();
        if (status is "error" or "failed" or "failure")
            return s_criticalBrush.Value;
        if (s.AbortedLastRun)
            return s_cautionBrush.Value;
        if (status is "active" or "running")
            return s_successBrush.Value;
        return s_neutralBrush.Value;
    }

    private static string ResolveStatusTooltip(SessionInfo s)
    {
        var status = s.Status?.Trim().ToLowerInvariant();
        if (status is "error" or "failed" or "failure") return "Error";
        if (s.AbortedLastRun) return "Aborted last run";
        if (status is "active" or "running") return "Running";
        return "Idle";
    }

    private static readonly Lazy<Brush> s_successBrush =
        new(() => (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"]);
    private static readonly Lazy<Brush> s_cautionBrush =
        new(() => (Brush)Application.Current.Resources["SystemFillColorCautionBrush"]);
    private static readonly Lazy<Brush> s_criticalBrush =
        new(() => (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]);
    private static readonly Lazy<Brush> s_neutralBrush =
        new(() => (Brush)Application.Current.Resources["SystemFillColorNeutralBrush"]);

    private void OnOpenChat(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string key)
        {
            if (CurrentApp.ActiveHubWindow is HubWindow hub)
            {
                hub.PendingChatSessionKey = key;
            }
            ((IAppCommands)CurrentApp).Navigate("chat", "sessions");
        }
    }

    private void ChannelSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var selected = sender.SelectedItem;
        _activeChannel = selected == AllTab ? "all" : (selected?.Text ?? "all");
        ApplyFilter();
    }

    private static string? ResolveSessionKey(object sender)
    {
        if (sender is FrameworkElement fe)
        {
            if (fe.DataContext is SessionViewModel vm && !string.IsNullOrEmpty(vm.Key))
                return vm.Key;
            if (fe.Tag is string tag && !string.IsNullOrEmpty(tag))
                return tag;
            if (fe is MenuFlyoutItem mfi && mfi.Parent is MenuFlyout mf
                && mf.Target is FrameworkElement target)
            {
                if (target.DataContext is SessionViewModel targetVm && !string.IsNullOrEmpty(targetVm.Key))
                    return targetVm.Key;
                if (target.Tag is string targetTag && !string.IsNullOrEmpty(targetTag))
                    return targetTag;
            }
        }
        return null;
    }

    private void OnResetSession(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => OnResetSessionAsync(sender),
            new OpenClawTray.AppLogger(),
            nameof(OnResetSession));

    private async Task OnResetSessionAsync(object sender)
    {
        if (ResolveSessionKey(sender) is not string key) return;
        var client = CurrentApp.GatewayClient;
        if (client == null) { ShowDisconnected(); return; }
        try { await client.ResetSessionAsync(key); }
        catch (Exception ex) { ShowActionFailure("Reset failed", ex); }
    }

    private void OnDeleteSession(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => OnDeleteSessionAsync(sender),
            new OpenClawTray.AppLogger(),
            nameof(OnDeleteSession));

    private async Task OnDeleteSessionAsync(object sender)
    {
        if (ResolveSessionKey(sender) is not string key) return;
        var client = CurrentApp.GatewayClient;
        if (client == null) { ShowDisconnected(); return; }
        try { await client.DeleteSessionAsync(key); }
        catch (Exception ex) { ShowActionFailure("Delete failed", ex); }
    }

    private void OnCompactSession(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => OnCompactSessionAsync(sender),
            new OpenClawTray.AppLogger(),
            nameof(OnCompactSession));

    private async Task OnCompactSessionAsync(object sender)
    {
        if (ResolveSessionKey(sender) is not string key) return;
        var client = CurrentApp.GatewayClient;
        if (client == null) { ShowDisconnected(); return; }
        try { await client.CompactSessionAsync(key); }
        catch (Exception ex) { ShowActionFailure("Compact failed", ex); }
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

        if (RefreshLabel is not null)
        {
            RefreshLabel.Text = "Refreshing...";
            _refreshTimer?.Stop();
            _refreshTimer = DispatcherQueue.CreateTimer();
            _refreshTimer.Interval = TimeSpan.FromSeconds(1);
            _refreshTimer.Tick += (t, a) => { RefreshLabel.Text = "Refresh"; _refreshTimer.Stop(); };
            _refreshTimer.Start();
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
    public Brush StatusBrush { get; set; } = new SolidColorBrush(Colors.Gray);
    public string StatusTooltip { get; set; } = "Idle";
    public string TokensText { get; set; } = "";
    public double ContextPercent { get; set; }
    public bool HasTokenData { get; set; }
    public bool CanEdit { get; set; } = true;
    public Visibility TokenRowVisibility => HasTokenData ? Visibility.Visible : Visibility.Collapsed;
}
