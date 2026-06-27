using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClaw.Shared.Sessions;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.ComponentModel;
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

        var client = CurrentApp.GatewayClient;

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

        var mainState = SessionActionPlanner.ResolveMainState(
            s.Key,
            rowIsMain: s.IsMain,
            mainSessionKey: CurrentApp.GatewayClient?.MainSessionKey,
            sessions: _appState?.Sessions);
        var isMain = mainState == SessionMainState.Main;

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
            IsMain = isMain,
            CanDelete = _sessionLoading.CanEdit && SessionActionPlanner.IsAllowed(SessionActionKind.Delete, mainState, out _),
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
        if (string.IsNullOrEmpty(key)) return;
        if (XamlRoot == null) return;

        var client = CurrentApp.GatewayClient;
        if (client == null) { ShowDisconnected(); return; }

        var name = SessionActionPlanner.Describe(key, vm?.DisplayName);
        var isMainState = ResolveMainState(key, vm);
        var isMain = isMainState == SessionMainState.Main;

        SessionCompactionCheckpointList list;
        try
        {
            list = await client.ListCompactionCheckpointsAsync(key);
        }
        catch (Exception ex)
        {
            ShowActionFailure("Couldn't load checkpoints", ex);
            return;
        }

        if (!list.IsSupported)
        {
            ShowActionInfo("Not supported", "This gateway doesn't support session compaction checkpoints. Update the gateway to use this.", InfoBarSeverity.Informational);
            return;
        }

        if (_unloaded || XamlRoot == null)
            return;

        var checkpoints = list.Checkpoints
            .OrderByDescending(c => c.CreatedAt ?? DateTime.MinValue)
            .ToList();

        var branchTarget = checkpoints.FirstOrDefault();
        var restoreTarget = SessionCheckpointSelection.ResolveUnambiguousLatest(checkpoints);
        var canRestore = restoreTarget is not null
            && SessionActionPlanner.IsAllowed(SessionActionKind.Restore, isMainState, out _);
        var actionHint = BuildCheckpointActionHint(checkpoints.Count, restoreTarget, canRestore, isMainState);

        var body = new StackPanel { Spacing = 12 };

        if (checkpoints.Count == 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "No compaction checkpoints yet. Compacting this session creates one you can branch from or restore to.",
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            body.Children.Add(new TextBlock
            {
                Text = $"{checkpoints.Count} checkpoint{(checkpoints.Count == 1 ? "" : "s")} \u00B7 newest first",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            });

            var listPanel = new StackPanel { Spacing = 4 };
            foreach (var cp in checkpoints)
            {
                listPanel.Children.Add(new TextBlock
                {
                    Text = "\u2022 " + DescribeCheckpoint(cp),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            body.Children.Add(listPanel);

            body.Children.Add(new TextBlock
            {
                Text = actionHint,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var dialog = new ContentDialog
        {
            Title = $"Checkpoints \u2014 {name}",
            Content = body,
            PrimaryButtonText = branchTarget is not null
                ? (restoreTarget is not null ? "Branch from latest" : "Branch from top checkpoint")
                : "",
            SecondaryButtonText = canRestore ? "Restore latest" : "",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && branchTarget is not null)
            await BranchCheckpointAsync(key, branchTarget.Id);
        else if (result == ContentDialogResult.Secondary && restoreTarget is not null)
            await RestoreCheckpointAsync(key, name, isMain, restoreTarget.Id);
    }

    private static string DescribeCheckpoint(SessionCompactionCheckpoint cp)
    {
        var parts = new List<string>(3);
        if (cp.CreatedAt is { } ts) parts.Add(ts.ToLocalTime().ToString("g"));
        if (!string.IsNullOrWhiteSpace(cp.Reason)) parts.Add(cp.Reason!);
        if (cp.TokensBefore is { } tb && cp.TokensAfter is { } ta) parts.Add($"{tb:n0}\u2192{ta:n0} tokens");

        var head = parts.Count > 0
            ? string.Join(" \u00B7 ", parts)
            : (string.IsNullOrEmpty(cp.Id) ? "checkpoint" : cp.Id);

        if (!string.IsNullOrWhiteSpace(cp.Summary))
            head += $" \u2014 {cp.Summary}";
        return head;
    }

    private static string BuildCheckpointActionHint(
        int checkpointCount,
        SessionCompactionCheckpoint? restoreTarget,
        bool canRestore,
        SessionMainState mainState)
    {
        if (checkpointCount <= 0)
            return "";

        if (canRestore)
        {
            return "Actions apply to the most recent checkpoint (top of the list). " +
                   "Branch starts a new session from it; Restore rolls this session back to it.";
        }

        var reason = mainState == SessionMainState.Main
            ? "Restore is unavailable for the main session."
            : restoreTarget is null
                ? "Restore is unavailable because the latest checkpoint can't be determined safely."
                : "Restore is unavailable for this session.";

        return "Branch starts a new session from the top checkpoint. " + reason;
    }

    private async Task BranchCheckpointAsync(string key, string checkpointId)
    {
        var client = CurrentApp.GatewayClient;
        if (client == null) { ShowDisconnected(); return; }

        SessionCompactionMutationResult result;
        try
        {
            result = await client.BranchCompactionCheckpointAsync(key, checkpointId);
        }
        catch (Exception ex)
        {
            ShowActionFailure("Branch failed", ex);
            return;
        }

        if (!result.IsSupported)
            ShowActionInfo("Not supported", "This gateway doesn't support branching from a checkpoint. Update the gateway to use this.", InfoBarSeverity.Informational);
        else if (result.Ok)
        {
            ShowActionInfo("Branched", result.ResultSessionKey is { Length: > 0 } nk ? $"Created session {nk}." : "Created a new session from the checkpoint.", InfoBarSeverity.Success);
            _ = client.RequestSessionsAsync();
        }
        else
            ShowActionInfo("Branch failed", result.Error ?? "Could not branch from the checkpoint.", InfoBarSeverity.Error);
    }

    private async Task RestoreCheckpointAsync(string key, string name, bool isMain, string checkpointId)
    {
        var mainState = ResolveMainState(key, null);
        if (isMain && mainState == SessionMainState.NotMain)
            mainState = SessionMainState.Main;

        if (!SessionActionPlanner.IsAllowed(SessionActionKind.Restore, mainState, out var blockedReason))
        {
            ShowActionInfo("Action unavailable", blockedReason ?? "Restore isn't available for this session.", InfoBarSeverity.Informational);
            return;
        }

        var prompt = SessionActionPlanner.BuildPrompt(SessionActionKind.Restore, key, name, mainState == SessionMainState.Main);
        if (prompt is not null && !await ConfirmAsync(prompt))
            return;

        var client = CurrentApp.GatewayClient;
        if (client == null) { ShowDisconnected(); return; }

        mainState = ResolveMainState(key, null);
        if (isMain && mainState == SessionMainState.NotMain)
            mainState = SessionMainState.Main;
        if (!SessionActionPlanner.IsAllowed(SessionActionKind.Restore, mainState, out blockedReason))
        {
            ShowActionInfo("Action unavailable", blockedReason ?? "Restore isn't available for this session.", InfoBarSeverity.Informational);
            return;
        }

        // Re-check before restore so a concurrent compaction cannot make the
        // confirmed "latest" checkpoint stale.
        try
        {
            var fresh = await client.ListCompactionCheckpointsAsync(key);
            if (!fresh.IsSupported)
            {
                ShowActionInfo("Not supported", "This gateway doesn't support restoring a checkpoint. Update the gateway to use this.", InfoBarSeverity.Informational);
                return;
            }

            var freshLatest = SessionCheckpointSelection.ResolveUnambiguousLatest(fresh.Checkpoints);
            if (freshLatest is null || !string.Equals(freshLatest.Id, checkpointId, StringComparison.Ordinal))
            {
                ShowActionInfo("Checkpoints changed", "The latest checkpoint changed since you opened this. Reopen Checkpoints and try again.", InfoBarSeverity.Warning);
                return;
            }
        }
        catch (Exception ex)
        {
            ShowActionFailure("Restore failed", ex);
            return;
        }

        SessionCompactionMutationResult result;
        try
        {
            result = await client.RestoreCompactionCheckpointAsync(key, checkpointId);
        }
        catch (Exception ex)
        {
            ShowActionFailure("Restore failed", ex);
            return;
        }

        if (!result.IsSupported)
            ShowActionInfo("Not supported", "This gateway doesn't support restoring a checkpoint. Update the gateway to use this.", InfoBarSeverity.Informational);
        else if (result.Ok)
        {
            ShowActionInfo("Restored", "Rolled the session back to the checkpoint.", InfoBarSeverity.Success);
            _ = client.RequestSessionsAsync();
        }
        else
            ShowActionInfo("Restore failed", result.Error ?? "Could not restore the checkpoint.", InfoBarSeverity.Error);
    }

    private async Task<bool> ConfirmAsync(SessionActionPrompt prompt)
    {
        if (XamlRoot == null) return false;
        var dialog = new ContentDialog
        {
            Title = prompt.Title,
            Content = prompt.Body,
            PrimaryButtonText = prompt.ConfirmLabel,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (prompt.IsDestructive)
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
        if (n >= 1_000_000) return $"{n / 1_000_000.0:0.#}M";
        if (n >= 1_000) return $"{n / 1_000.0:0.#}K";
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
    public string StatusTooltip { get; set; } = "Idle";
    public string TokensText { get; set; } = "";
    public double ContextPercent { get; set; }
    public bool HasTokenData { get; set; }
    public bool CanEdit { get; set; } = true;
    public bool IsMain { get; set; }
    public bool CanDelete { get; set; } = true;
    public Visibility TokenRowVisibility => HasTokenData ? Visibility.Visible : Visibility.Collapsed;
}
