using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClaw.Shared.Sessions;
using OpenClawTray.Dialogs;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace OpenClawTray.Pages;

public sealed partial class SessionsPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private AppState? _appState;
    private SessionInfo[]? _allSessions;
    private string _activeChannel = "all";
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _refreshTimer;
    private readonly AsyncListLoadingState _sessionLoading = new();
    private IOperatorGatewayClient? _subscribedClient;
    private bool _unloaded;
    private bool _syncingShowCompletedToggle;
    private bool _showBackgroundSessions;

    public SessionsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => _unloaded = false;
        Unloaded += (_, _) =>
        {
            _unloaded = true;
            _refreshTimer?.Stop(); _refreshTimer = null;
            if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
            if (_subscribedClient != null)
            {
                _subscribedClient.SessionCommandCompleted -= OnSessionCommandCompleted;
                _subscribedClient = null;
            }
        };
    }

    public void Initialize()
    {
        // Guard against duplicate subscriptions (NavigationCacheMode reuses page)
        if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
        _appState = CurrentApp.AppState!;
        _appState.PropertyChanged += OnAppStateChanged;
        SyncShowCompletedToggle();

        var client = CurrentApp.GatewayClient;

        // The real-process accessibility suite has no gateway. Give it an
        // isolated, deterministic duplicate-name scenario so UI Automation can
        // prove both the rendered titles and the row-to-chat key hand-off.
        // Requiring the test data directory as well as the explicit flag keeps
        // this path unreachable from a normal app launch.
        if (Environment.GetEnvironmentVariable("OPENCLAW_ACCESSIBILITY_TEST_SESSIONS") == "1"
            && Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR") is { Length: > 0 })
        {
            UpdateSessions(
            [
                new SessionInfo
                {
                    Key = "agent:main:main",
                    IsMain = true,
                    Status = "active",
                    HasActiveRun = true,
                    DisplayName = "OpenClaw Windows Tray",
                    UpdatedAt = DateTime.UtcNow,
                },
                new SessionInfo
                {
                    Key = "agent:main:fork",
                    Status = "running",
                    HasActiveRun = false,
                    DisplayName = "OpenClaw Windows Tray",
                    UpdatedAt = DateTime.UtcNow.AddSeconds(-1),
                },
                new SessionInfo
                {
                    Key = "agent:main:deploy-migration",
                    Status = "failed",
                    DisplayName = "Deploy migration",
                    UpdatedAt = DateTime.UtcNow.AddMinutes(-2),
                },
                new SessionInfo
                {
                    Key = "agent:main:research-labels",
                    Status = "timeout",
                    DisplayName = "Research status labels",
                    UpdatedAt = DateTime.UtcNow.AddMinutes(-4),
                },
                new SessionInfo
                {
                    Key = "agent:main:release-notes",
                    Status = "killed",
                    DisplayName = "Draft release notes",
                    UpdatedAt = DateTime.UtcNow.AddMinutes(-6),
                },
                new SessionInfo
                {
                    Key = "agent:main:cron:nightly-cleanup",
                    Status = "active",
                    HasActiveRun = true,
                    DisplayName = "Nightly cleanup",
                    Classification = "cron",
                    IsBackground = true,
                    UpdatedAt = DateTime.UtcNow.AddMinutes(-7),
                },
                new SessionInfo
                {
                    Key = "agent:main:completed-cleanup",
                    Status = "done",
                    DisplayName = "Completed cleanup",
                    UpdatedAt = DateTime.UtcNow.AddMinutes(-8),
                },
            ]);
            return;
        }

        // Rebind when the client instance changes so a cached page never holds
        // a stale command-result subscription.
        if (_subscribedClient != client)
        {
            if (_subscribedClient != null)
                _subscribedClient.SessionCommandCompleted -= OnSessionCommandCompleted;
            _subscribedClient = client;
            if (_subscribedClient != null)
                _subscribedClient.SessionCommandCompleted += OnSessionCommandCompleted;
        }

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

    private void OnOpenConnectionClick(object sender, RoutedEventArgs e)
        => ((IAppCommands)CurrentApp).Navigate("connection");

    public void UpdateSessions(SessionInfo[] sessions)
    {
        _allSessions = sessions;
        _sessionLoading.Complete(_allSessions.Length);
        RebuildChannelTabs();
        ApplyFilter();
    }

    private IEnumerable<SessionInfo> SessionsForCurrentBackgroundScope() =>
        (_allSessions ?? Array.Empty<SessionInfo>())
        .Where(session => SessionDisplayResolver.IsVisible(session, _showBackgroundSessions));

    private void RebuildChannelTabs()
    {
        if (_allSessions == null) return;

        var requestedChannel = _activeChannel;
        var visibleSessions = SessionVisibilityFilter.VisibleSessions(
            SessionsForCurrentBackgroundScope(),
            ShowCompletedSessions);
        var channels = visibleSessions
            .Where(s => !string.IsNullOrWhiteSpace(s.Channel))
            .Select(s => s.Channel!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c)
            .ToList();
        var activeChannel = SessionVisibilityFilter.ResolveActiveChannel(requestedChannel, channels);

        // Keep "All" tab, clear dynamic tabs
        while (ChannelSelector.Items.Count > 1)
            ChannelSelector.Items.RemoveAt(ChannelSelector.Items.Count - 1);

        SelectorBarItem selectedItem = AllTab;
        foreach (var ch in channels)
        {
            var item = new SelectorBarItem { Text = ch };
            ChannelSelector.Items.Add(item);
            if (string.Equals(ch, activeChannel, StringComparison.OrdinalIgnoreCase))
                selectedItem = item;
        }

        _activeChannel = activeChannel;
        selectedItem.IsSelected = true;
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

        var visibleSessions = SessionVisibilityFilter.VisibleSessions(
                SessionsForCurrentBackgroundScope(),
                ShowCompletedSessions)
            .ToList();
        var activeTitles = SessionTitleFormatter.FormatUnique(visibleSessions);
        IEnumerable<(SessionInfo Session, string Title)> filtered = visibleSessions
            .Select((session, index) => (Session: session, Title: activeTitles[index]));

        if (_activeChannel != "all")
        {
            filtered = filtered.Where(item =>
                string.Equals(item.Session.Channel, _activeChannel, StringComparison.OrdinalIgnoreCase));
        }

        var viewModels = filtered
            .OrderBy(item => SessionRunState.GetDisplaySortOrder(item.Session))
            .ThenByDescending(item => item.Session.UpdatedAt ?? item.Session.LastSeen)
            .Select(item => ToViewModel(item.Session, item.Title))
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

    private bool ShowCompletedSessions => CurrentApp.Settings?.ShowCompletedSessions ?? false;

    private void SyncShowCompletedToggle()
    {
        _syncingShowCompletedToggle = true;
        try
        {
            ShowCompletedToggle.IsOn = ShowCompletedSessions;
        }
        finally
        {
            _syncingShowCompletedToggle = false;
        }
    }

    private void OnShowCompletedToggled(object sender, RoutedEventArgs e)
    {
        if (_syncingShowCompletedToggle)
            return;

        if (CurrentApp.Settings is { } settings)
        {
            settings.ShowCompletedSessions = ShowCompletedToggle.IsOn;
            settings.Save();
        }
        RebuildChannelTabs();
        ApplyFilter();
    }

    private void OnShowBackgroundToggled(object sender, RoutedEventArgs e)
    {
        _showBackgroundSessions = ShowBackgroundToggle.IsOn;
        RebuildChannelTabs();
        ApplyFilter();
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

    private SessionViewModel ToViewModel(SessionInfo s, string displayName)
    {
        var subtitle = SessionTitleFormatter.FormatSubtitle(s);
        var parts = new List<string>(5);
        if (!string.IsNullOrWhiteSpace(subtitle)) parts.Add(subtitle!);
        if (!string.IsNullOrWhiteSpace(s.Provider)) parts.Add(s.Provider!);
        if (!string.IsNullOrWhiteSpace(s.Model)) parts.Add(s.Model!);
        if (string.IsNullOrWhiteSpace(subtitle) && !string.IsNullOrWhiteSpace(s.Channel))
            parts.Add(s.Channel!);
        if (SessionRunState.HasStoppedLastRun(s))
            parts.Add(LocalizationHelper.GetString("SessionsPage_LastRunStopped"));

        var hasTokens = s.InputTokens > 0 || s.OutputTokens > 0;
        var tokensText = hasTokens
            ? $"↓{FormatTokenCount(s.InputTokens)} / ↑{FormatTokenCount(s.OutputTokens)}"
            : "";

        // ContextTokens is the window size, TotalTokens is usage.
        double contextPercent = 0;
        if (s.ContextTokens > 0 && s.TotalTokens > 0)
            contextPercent = Math.Min(100.0, (double)s.TotalTokens / s.ContextTokens * 100.0);

        var mainState = SessionActionPlanner.ResolveMainState(
            s.Key,
            rowIsMain: s.IsMain,
            mainSessionKey: CurrentApp.GatewayClient?.MainSessionKey,
            sessions: _appState?.Sessions);
        var isMain = mainState == SessionMainState.Main;

        return new SessionViewModel
        {
            Key = s.Key,
            DisplayName = displayName,
            AgeText = s.AgeText,
            DetailLine = parts.Count > 0 ? string.Join(" · ", parts) : "",
            StatusBrush = ResolveStatusBrush(s),
            StatusText = ResolveStatusText(s),
            StatusTooltip = ResolveStatusText(s),
            TokensText = tokensText,
            ContextPercent = contextPercent,
            HasTokenData = hasTokens || contextPercent > 0,
            CanEdit = _sessionLoading.CanEdit,
            IsMain = isMain,
            CanDelete = _sessionLoading.CanEdit && SessionActionPlanner.IsAllowed(SessionActionKind.Delete, mainState, out _),
        };
    }

    private static Brush ResolveStatusBrush(SessionInfo s)
    {
        return SessionRunState.GetDisplayState(s) switch
        {
            SessionDisplayState.Working => s_successBrush.Value,
            SessionDisplayState.NeedsAttention => s_criticalBrush.Value,
            _ => s_neutralBrush.Value,
        };
    }

    private static string ResolveStatusText(SessionInfo s)
    {
        return SessionRunState.GetDisplayState(s) switch
        {
            SessionDisplayState.Working => LocalizationHelper.GetString("SessionsPage_Status_Working"),
            SessionDisplayState.NeedsAttention => LocalizationHelper.GetString("SessionsPage_Status_NeedsAttention"),
            _ => LocalizationHelper.GetString("SessionsPage_Status_Ready"),
        };
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
            // Stash the target session on both App (fallback when the HubWindow
            // doesn't exist yet) and HubWindow (existing path consumed by ChatPage).
            CurrentApp.PendingChatSessionKey = key;
            if (CurrentApp.ActiveHubWindow is HubWindow hub)
            {
                hub.PendingChatSessionKey = key;
            }
            // The native title-bar back button handles returning to Sessions.
            ((IAppCommands)CurrentApp).Navigate("chat");
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

    private static SessionViewModel? ResolveSessionVm(object sender)
    {
        if (sender is FrameworkElement fe)
        {
            if (fe.DataContext is SessionViewModel vm && !string.IsNullOrEmpty(vm.Key))
                return vm;
            if (fe is MenuFlyoutItem mfi && mfi.Parent is MenuFlyout mf
                && mf.Target is FrameworkElement target
                && target.DataContext is SessionViewModel targetVm
                && !string.IsNullOrEmpty(targetVm.Key))
                return targetVm;
        }
        return null;
    }

    private void OnResetSession(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => RunSessionActionAsync(sender, SessionActionKind.Reset),
            new OpenClawTray.AppLogger(),
            nameof(OnResetSession));

    private void OnDeleteSession(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => RunSessionActionAsync(sender, SessionActionKind.Delete),
            new OpenClawTray.AppLogger(),
            nameof(OnDeleteSession));

    private void OnCompactSession(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => RunSessionActionAsync(sender, SessionActionKind.Compact),
            new OpenClawTray.AppLogger(),
            nameof(OnCompactSession));

    private async Task RunSessionActionAsync(object sender, SessionActionKind kind)
    {
        var vm = ResolveSessionVm(sender);
        var key = vm?.Key ?? ResolveSessionKey(sender);
        if (string.IsNullOrEmpty(key)) return;

        var client = CurrentApp.GatewayClient;
        if (client == null) { ShowDisconnected(); return; }

        var isMainState = ResolveMainState(key, vm);
        var isMain = isMainState == SessionMainState.Main;
        var displayName = vm?.DisplayName;

        if (!SessionActionPlanner.IsAllowed(kind, isMainState, out var blockedReason))
        {
            ShowActionInfo("Action unavailable", blockedReason ?? "This action isn't available.", InfoBarSeverity.Informational);
            return;
        }

        var prompt = SessionActionPlanner.BuildPrompt(kind, key, displayName, isMain);
        if (prompt is not null && !await ConfirmAsync(prompt))
            return;

        try
        {
            if (kind == SessionActionKind.Delete)
            {
                var latestState = ResolveMainState(key, vm);
                if (!SessionActionPlanner.IsAllowed(kind, latestState, out blockedReason))
                {
                    ShowActionInfo("Action unavailable", blockedReason ?? "Delete isn't available for this session.", InfoBarSeverity.Informational);
                    return;
                }
            }

            var sent = kind switch
            {
                SessionActionKind.Reset => await client.ResetSessionAsync(key),
                SessionActionKind.Compact => await client.CompactSessionAsync(key),
                SessionActionKind.Delete => await client.DeleteSessionAsync(key),
                _ => true,
            };
            if (!sent)
                ShowActionInfo($"{kind} failed", "The gateway didn't accept the request. Try again.", InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            ShowActionFailure($"{kind} failed", ex);
        }
    }

    private SessionMainState ResolveMainState(string key, SessionViewModel? vm)
        => SessionActionPlanner.ResolveMainState(
            key,
            rowIsMain: vm?.IsMain,
            mainSessionKey: CurrentApp.GatewayClient?.MainSessionKey,
            sessions: _appState?.Sessions);

    private void OnExportSession(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => OnExportSessionAsync(sender),
            new OpenClawTray.AppLogger(),
            nameof(OnExportSession));

    private async Task OnExportSessionAsync(object sender)
    {
        var vm = ResolveSessionVm(sender);
        var key = vm?.Key ?? ResolveSessionKey(sender);
        if (string.IsNullOrEmpty(key)) return;

        var client = CurrentApp.GatewayClient;
        if (client == null) { ShowDisconnected(); return; }

        var hwnd = ResolveHostHwnd();
        if (hwnd == IntPtr.Zero)
        {
            ShowActionInfo("Export unavailable", "Open the app window before exporting a transcript.", InfoBarSeverity.Informational);
            return;
        }

        ChatHistoryInfo history;
        try
        {
            history = await client.RequestChatHistoryAsync(key);
        }
        catch (NotSupportedException)
        {
            ShowActionInfo("Not supported", "This gateway doesn't support exporting a transcript. Update the gateway to use this.", InfoBarSeverity.Informational);
            return;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("unknown method", StringComparison.OrdinalIgnoreCase))
        {
            ShowActionInfo("Not supported", "This gateway doesn't support exporting a transcript. Update the gateway to use this.", InfoBarSeverity.Informational);
            return;
        }
        catch (Exception ex)
        {
            ShowActionFailure("Export failed", ex);
            return;
        }

        if (history.Messages.Count == 0)
        {
            ShowActionInfo("Nothing to export", "This session has no transcript yet.", InfoBarSeverity.Informational);
            return;
        }

        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.Desktop,
                SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(
                    SessionTranscriptFormatter.SuggestFileName(key)),
            };
            picker.FileTypeChoices.Add("Text file", new List<string> { ".txt" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file == null) return; // user cancelled

            await FileIO.WriteTextAsync(file, SessionTranscriptFormatter.Format(history));
            ShowActionInfo("Transcript exported", $"Saved {history.Messages.Count} messages to {file.Name}.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowActionFailure("Export failed", ex);
        }
    }

    private void OnShowCheckpoints(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => OnShowCheckpointsAsync(sender),
            new OpenClawTray.AppLogger(),
            nameof(OnShowCheckpoints));

    private async Task OnShowCheckpointsAsync(object sender)
    {
        var vm = ResolveSessionVm(sender);
        var key = vm?.Key ?? ResolveSessionKey(sender);
        if (string.IsNullOrEmpty(key))
            return;

        if (CurrentApp.GatewayClient is null)
        {
            ShowDisconnected();
            return;
        }

        await SessionCheckpointDialogCoordinator.ShowAsync(
            XamlRoot,
            key,
            isHostAvailable: () => !_unloaded && XamlRoot is not null,
            showStatusAsync: (title, message, severity) =>
            {
                ShowActionInfo(title, message, severity);
                return Task.CompletedTask;
            },
            displayName: vm?.DisplayName,
            rowIsMain: vm?.IsMain);
    }

    private async Task<bool> ConfirmAsync(SessionActionPrompt prompt)
    {
        if (XamlRoot == null) return false;
        var localizedPrompt = SessionActionPromptLocalizer.Localize(prompt);
        var dialog = new ContentDialog
        {
            Title = localizedPrompt.Title,
            Content = localizedPrompt.Body,
            PrimaryButtonText = localizedPrompt.ConfirmLabel,
            CloseButtonText = LocalizationHelper.GetString("SessionActionPrompt_CancelLabel"),
            DefaultButton = ContentDialogButton.None,
            XamlRoot = XamlRoot,
        };
        if (localizedPrompt.IsDestructive)
            dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private IntPtr ResolveHostHwnd()
    {
        var window = CurrentApp.ActiveHubWindow;
        if (window == null) return IntPtr.Zero;
        try { return WinRT.Interop.WindowNative.GetWindowHandle(window); }
        catch { return IntPtr.Zero; }
    }

    private void OnSessionCommandCompleted(object? sender, SessionCommandResult result)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_unloaded) return;

            if (string.Equals(result.Method, "sessions.compact", StringComparison.Ordinal) && result.Ok)
            {
                if (result.Compacted == true)
                {
                    var kept = result.Kept.HasValue ? $" Kept {result.Kept.Value} lines." : "";
                    ShowActionInfo("Checkpoint created", $"Compacted {result.Key ?? "session"}.{kept} View it from the session's Checkpoints menu.", InfoBarSeverity.Success);
                }
                else if (result.Compacted == false)
                {
                    ShowActionInfo("Nothing to compact", $"{result.Key ?? "Session"} was already compact; no checkpoint was created.", InfoBarSeverity.Informational);
                }
                else
                {
                    ShowActionInfo("Session compacted", $"Compacted {result.Key ?? "session"}. Refresh Checkpoints to see any new entries.", InfoBarSeverity.Success);
                }
            }
            ApplyFilter();
        });
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
        if (n >= 1_000_000) return $"{(n / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture)}M";
        if (n >= 1_000) return $"{(n / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture)}K";
        return n.ToString();
    }

    private void ShowDisconnected()
    {
        ConnectionInfoBar.Title = LocalizationHelper.GetString("SessionsPage_GatewayDisconnected.Title");
        ConnectionInfoBar.Message = LocalizationHelper.GetString("SessionsPage_GatewayDisconnected.Message");
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

    private void ShowActionInfo(string title, string message, InfoBarSeverity severity)
    {
        ConnectionInfoBar.Title = title;
        ConnectionInfoBar.Message = message;
        ConnectionInfoBar.Severity = severity;
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
    public string StatusText { get; set; } = "Ready";
    public string StatusTooltip { get; set; } = "Ready";
    public string TokensText { get; set; } = "";
    public double ContextPercent { get; set; }
    public bool HasTokenData { get; set; }
    public bool CanEdit { get; set; } = true;
    public bool IsMain { get; set; }
    public bool CanDelete { get; set; } = true;
    public Visibility TokenRowVisibility => HasTokenData ? Visibility.Visible : Visibility.Collapsed;
}
