using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Pages;
using OpenClawTray.Presentation;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Linq;

namespace OpenClawTray.Controls;

/// <summary>
/// The native Settings surface hosted inside <c>WorkspaceWindow</c>. It owns the section rail,
/// the content frame, the connection status pill and the Settings search flyout. It never mounts
/// ChatPage and never owns history: the host's <see cref="WorkspaceNavigationHistory"/> is the
/// only Back/Forward stack, and the frame keeps no stack of its own.
/// </summary>
internal sealed partial class SettingsShellView : UserControl
{
    private static App CurrentApp => (App)Application.Current;
    private static TaskCompletionSource<bool> CreateCompletedContentReady()
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ready.SetResult(true);
        return ready;
    }

    internal AppState? AppModel { get; set; }
    private string _currentAgentId = "main";
    public string CurrentAgentId => _currentAgentId;
    private TaskCompletionSource<bool> _contentReady = CreateCompletedContentReady();
    private AppNotificationService? _appNotificationService;
    private readonly AppNotificationInfoBarPresenter _appNotificationInfoBarPresenter = new();
    private AppNotificationSnapshot? _lastAppNotificationSnapshot;
    private AppNotificationInfoBarPresentation _currentAppNotificationPresentation =
        AppNotificationInfoBarPresentation.Hidden;
    private SettingsWriteOrigin? _commandPaletteSettingsOrigin;
    private bool _suppressAppNotificationClosed;
    private bool _disposed;
    private bool _active;

    /// <summary>Raised when the rail or an in-page link requests another Settings section.</summary>
    internal Action<string>? NavigateRequested { get; set; }

    /// <summary>Raised when the rail's Chat item or a notification route leaves Settings.</summary>
    internal Action<string>? WorkspaceRouteRequested { get; set; }

    /// <summary>
    /// Asks the host to drop unavailable Settings sections from history. The second argument is the
    /// section to show when the current one was pruned.
    /// </summary>
    internal Action<Func<string, bool>, string?>? PruneRequested { get; set; }

    public Action? ConnectAction { get; set; }
    public Action? ReconnectAction { get; set; }
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _gatewayNavHideTimer;

    public event EventHandler? SettingsSaved;

    public void RaiseSettingsSaved() => SettingsSaved?.Invoke(this, EventArgs.Empty);

    public SettingsShellView()
    {
        InitializeComponent();
        ContentFrame.IsNavigationStackEnabled = false;
        var searchLabel = LocalizationHelper.GetString("TitleSearchBox.PlaceholderText");
        AutomationProperties.SetName(SettingsSearchButton, searchLabel);
        ToolTipService.SetToolTip(SettingsSearchButton, searchLabel);
        NavView.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, (_, _) => UpdatePaneChrome());
        UpdatePaneChrome();
        RefreshDiagnosticsNavVisibility();
        ApplyHighContrastFallbackIfNeeded();
        RootGrid.SizeChanged += OnRootGridSizeChanged;
        ToolTipService.SetToolTip(StatusPillButton, LocalizationHelper.GetString("HubWindow_StatusPill_Tooltip"));
    }

    /// <summary>The element that opens the command search flyout for the current pane state.</summary>
    private FrameworkElement SearchAnchor => NavView.IsPaneOpen ? SettingsSearchButton : NavPaneToggleButton;

    public void RefreshDiagnosticsNavVisibility()
    {
        var isVisible = DiagnosticsGate.IsVisible;
        NavDiagnostics.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        if (isVisible)
            return;

        PruneRequested?.Invoke(tag => string.Equals(tag, "debug", StringComparison.Ordinal), "settings");
    }

    /// <summary>
    /// Subscribe to AppState property changes for status and nav updates.
    /// Called by the host after setting AppModel.
    /// </summary>
    internal void BindToAppState()
    {
        if (AppModel != null)
        {
            AppModel.PropertyChanged -= OnAppModelChanged;
            AppModel.PropertyChanged += OnAppModelChanged;
            UpdateStatus(AppModel.Status);
            ScheduleGatewayNavVisibilityForStatus(AppModel.Status, debounceDisconnected: false);

            // Apply agents list that may have arrived before this view was created.
            if (AppModel.AgentsList.HasValue)
                RebuildAgentNavItems(AppModel.AgentsList.Value);
        }
    }

    internal void BindAppNotifications(AppNotificationService service)
    {
        if (_appNotificationService != null)
            _appNotificationService.Changed -= OnAppNotificationChanged;

        _appNotificationService = service;
        _appNotificationService.Changed += OnAppNotificationChanged;
        RenderAppNotification(service.Snapshot);
    }

    /// <summary>
    /// Releases every subscription and timer the view holds. Called when the owning window closes.
    /// </summary>
    internal void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Deactivate();
        _contentReady.TrySetResult(true);
        _gatewayNavHideTimer?.Stop();
        if (_appNotificationService != null)
            _appNotificationService.Changed -= OnAppNotificationChanged;
        if (AppModel != null)
            AppModel.PropertyChanged -= OnAppModelChanged;
        _appNotificationService = null;
    }

    private void OnAppNotificationChanged(object? sender, AppNotificationChangedEventArgs args)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (_disposed) return;
            RenderAppNotification(args.Snapshot);
        });
    }

    private void RenderAppNotification(AppNotificationSnapshot snapshot)
    {
        _lastAppNotificationSnapshot = snapshot;

        _currentAppNotificationPresentation = _appNotificationInfoBarPresenter.Present(
            snapshot,
            _currentAppNotificationPresentation.Notification?.Id,
            AppNotificationInfoBar.IsOpen,
            _currentNavTag,
            LocalizationHelper.GetString("AppNotification_ShowMore"));
        if (!_currentAppNotificationPresentation.IsVisible)
        {
            HideAppNotificationInfoBar();
            return;
        }

        var notification = _currentAppNotificationPresentation.Notification!;
        AppNotificationInfoBar.Visibility = Visibility.Visible;
        AppNotificationInfoBar.Severity = ToInfoBarSeverity(_currentAppNotificationPresentation.Severity);
        AppNotificationInfoBar.Title = notification.Title;
        AppNotificationInfoBar.Message = notification.Message;

        var action = _currentAppNotificationPresentation.Action;
        if (action.Kind != AppNotificationInfoBarActionKind.None)
        {
            AppNotificationActionButton.Content = action.Label;
            AppNotificationActionButton.Visibility = Visibility.Visible;
            AppNotificationActionButton.IsEnabled = action.IsEnabled;
        }
        else
        {
            AppNotificationActionButton.Visibility = Visibility.Collapsed;
            AppNotificationActionButton.IsEnabled = true;
        }

        AppNotificationInfoBar.IsOpen = true;
    }

    private void HideAppNotificationInfoBar()
    {
        _suppressAppNotificationClosed = true;
        AppNotificationInfoBar.IsOpen = false;
        AppNotificationInfoBar.Visibility = Visibility.Collapsed;
        AppNotificationInfoBar.Title = string.Empty;
        AppNotificationInfoBar.Message = string.Empty;
        AppNotificationActionButton.Visibility = Visibility.Collapsed;
        _currentAppNotificationPresentation = AppNotificationInfoBarPresentation.Hidden;
        _suppressAppNotificationClosed = false;
        AppNotificationActionButton.IsEnabled = true;
    }

    private void UpdateAppNotificationActionEnabledState()
    {
        _currentAppNotificationPresentation = _appNotificationInfoBarPresenter.UpdateCurrentTag(
            _currentAppNotificationPresentation,
            _currentNavTag);
        AppNotificationActionButton.IsEnabled =
            _currentAppNotificationPresentation.Action.IsEnabled;
    }

    private static InfoBarSeverity ToInfoBarSeverity(AppNotificationInfoBarSeverity severity) => severity switch
    {
        AppNotificationInfoBarSeverity.Success => InfoBarSeverity.Success,
        AppNotificationInfoBarSeverity.Warning => InfoBarSeverity.Warning,
        AppNotificationInfoBarSeverity.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational
    };

    private void OnStatusFlyoutOpening(object sender, object e)
    {
        StatusContent.Initialize(
            () => StatusFlyout.Hide(),
            () => NavigateTo("connection"),
            () => (ReconnectAction ?? ConnectAction)?.Invoke());
    }

    private void OnAppNotificationInfoBarClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (_suppressAppNotificationClosed)
            return;

        if (_lastAppNotificationSnapshot is not null)
        {
            // Closing the InfoBar hides the entire banner strip for notifications
            // that were already active. The Notifications page remains the source
            // of truth, and deleting the displayed list item can still reveal a
            // remaining hidden item via RenderAppNotification's fallback path.
            _appNotificationInfoBarPresenter.HideActiveNotifications(_lastAppNotificationSnapshot);
        }

        HideAppNotificationInfoBar();
    }

    private void OnAppNotificationActionButtonClick(object sender, RoutedEventArgs e)
    {
        if (_currentAppNotificationPresentation.Action.Kind ==
            AppNotificationInfoBarActionKind.ShowMore)
        {
            NavigateTo("notifications");
            return;
        }

        if (_currentAppNotificationPresentation.Action is
            { Kind: AppNotificationInfoBarActionKind.NotificationRoute, Route.Length: > 0 } action)
        {
            var route = action.Route;
            if (AppNotificationActionRoutes.TryGetChatSessionKey(route, out var sessionKey))
            {
                CurrentApp.PendingChatSessionKey = sessionKey;
                NavigateTo("chat");
            }
            else
            {
                NavigateTo(route);
            }
            _appNotificationService?.Dismiss(_currentAppNotificationPresentation.Notification!.Id);
            return;
        }
    }

    private void OnAppModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || AppModel == null) return;
        try
        {
            switch (e.PropertyName)
            {
                case nameof(AppState.Status):
                    DispatcherQueue?.TryEnqueue(() =>
                    {
                        if (_disposed) return;
                        _cachedCommands = null;
                        var status = AppModel!.Status;
                        UpdateStatus(status);
                        // Defer nav visibility to avoid stowed exceptions during NavigationView layout
                        DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                        {
                            if (_disposed) return;
                            ScheduleGatewayNavVisibilityForStatus(AppModel!.Status, debounceDisconnected: true);
                        });
                });
                break;
            case nameof(AppState.GatewaySelf):
                DispatcherQueue?.TryEnqueue(() =>
                {
                    if (_disposed) return;
                    UpdateStatus(AppModel!.Status);
                });
                break;
            case nameof(AppState.AgentsList):
                DispatcherQueue?.TryEnqueue(() =>
                {
                    if (_disposed) return;
                    if (AppModel!.AgentsList.HasValue)
                        RebuildAgentNavItems(AppModel.AgentsList.Value);
                });
                break;
        }
        }
        catch (Exception ex)
        {
            Services.Logger.Warn($"[SettingsShellView] OnAppModelChanged({e.PropertyName}) failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Apply persisted nav-pane state. Called from App.ShowHub() after creating the window.
    /// </summary>
    internal void ApplyNavPaneState(SettingsManager settings)
    {
        if (NavView != null)
        {
            NavView.IsPaneOpen = settings.HubNavPaneOpen;
        }
    }

    private void OnRootGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        const double minPane = 200;
        const double maxPane = 260;
        const double ratio = 0.25;

        double desired = e.NewSize.Width * ratio;
        NavView.OpenPaneLength = Math.Clamp(desired, minPane, maxPane);
    }

    private void OnNavContentHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        NavContentClip.Rect = new global::Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
    }

    private void OnNavPaneToggleButtonClick(object sender, RoutedEventArgs e)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void UpdatePaneChrome()
    {
        SettingsNavigationActions.Visibility = NavView.IsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        StatusPillText.Visibility = SettingsNavigationActions.Visibility;
        StatusPillChevron.Visibility = SettingsNavigationActions.Visibility;
        StatusPillContent.ColumnSpacing = NavView.IsPaneOpen ? 8 : 0;
        StatusPillButton.Width = NavView.IsPaneOpen ? double.NaN : 40;
        StatusPillButton.HorizontalContentAlignment = NavView.IsPaneOpen ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
    }

    // Back/Forward live in the window-level pair driven only by WorkspaceNavigationHistory.
    // This view keeps no navigation stack of its own.
    /// <summary>Returns the currently displayed page in the content frame.</summary>
    public object? CurrentPage => ContentFrame?.Content;

    /// <summary>The canonical tag of the section currently shown, or null while deactivated.</summary>
    internal string? CurrentSectionTag => _currentNavTag;

    // Canonical tag of the page currently shown in ContentFrame; tracked here
    // (rather than relying on NavView.SelectedItem) so navigation identity
    // includes the tag — important for agent-scoped pages where several tags
    // map to the same Page type (e.g. "sessions" vs "agent:main:sessions"
    // both → SessionsPage).
    private string? _currentNavTag;

    // Set true while a programmatic SelectedItem update is in flight, to
    // suppress the resulting SelectionChanged from re-entering NavigateInternal.
    private bool _syncingNavSelection;

    // The one-shot native setup request this view is currently honoring. Waits compare against it
    // so history replay or a later navigation can never satisfy an older request.
    private SetupNativeNavigationRequest? _activeNativeRequest;

    /// <summary>
    /// Requests a section. Rail clicks, in-page links and search all flow through here; the host
    /// decides whether it becomes a history entry. Workspace routes leave Settings.
    /// </summary>
    public void NavigateTo(string tag)
    {
        WorkspaceNavigation.Dispatch(tag, _ => WorkspaceRouteRequested?.Invoke(tag), NavigateCompanion);
    }

    private void NavigateCompanion(string tag) =>
        NavigateRequested?.Invoke(WorkspaceNavigation.NormalizeSettingsTag(tag, _currentAgentId));

    /// <summary>
    /// Renders <paramref name="tag"/> without touching history. Also re-activates the view after
    /// <see cref="Deactivate"/>, so re-entry never depends on <c>Frame.Navigated</c> alone.
    /// </summary>
    internal void ShowSection(string tag, SetupNativeNavigationRequest? nativeRequest = null)
    {
        if (_disposed) return;
        _active = true;
        var showAbout = tag == "about";
        NavigateInternal(showAbout ? "settings" : tag, nativeRequest);
        if (ContentFrame.Content is SettingsPage settingsPage)
        {
            if (showAbout) settingsPage.ShowAbout();
            else if (tag == "settings") settingsPage.ShowTop();
        }
    }

    /// <summary>
    /// Leaves the Settings surface: stops every page, closes Settings flyouts, and
    /// resets the activation scope so no view model or subscription stays live behind the chat.
    /// </summary>
    internal void Deactivate()
    {
        if (!_active && ContentFrame.Content is null) return;
        _active = false;
        _activeNativeRequest = null;
        HideTransientSurfaces();
        _currentNavTag = null;
        // Clearing Content does not raise OnNavigatedFrom, so pages with cleanup are stopped here.
        (ContentFrame.Content as DebugPage)?.Deactivate();
        ContentFrame.Content = null;
        try { CurrentApp.PageActivator?.Reset(); }
        catch (Exception ex)
        {
            Logger.Warn($"[SettingsShellView] Navigation scope reset failed: {ex.Message}");
        }
        _contentReady.TrySetResult(true);
        UpdateAppNotificationActionEnabledState();
    }

    /// <summary>
    /// Policy: leaving Settings closes only the Settings flyouts. ContentDialogs are never
    /// cancelled here; the host window defers destination changes while one is open.
    /// </summary>
    private void HideTransientSurfaces()
    {
        StatusFlyout.Hide();
        CommandSearchFlyout.Hide();
    }

    internal void OpenCommandCenter()
    {
        CommandSearchFlyout.ShowAt(SearchAnchor);
    }

    /// <summary>
    /// Waits until the requested native setup page is the one shown and ready. Fails if the view
    /// moved to another section or request, so a stale wait can never report success.
    /// </summary>
    internal async Task WaitForNativeSetupAsync(SetupNativeNavigationRequest request, CancellationToken ct)
    {
        void EnsureCurrent()
        {
            if (!ReferenceEquals(_activeNativeRequest, request) || _currentNavTag != request.PageTag)
                throw new InvalidOperationException("The selected setup destination changed.");
        }

        EnsureCurrent();
        await WaitForCurrentContentReadyAsync().WaitAsync(ct);
        EnsureCurrent();
        while (ContentFrame.Content is not FrameworkElement { IsLoaded: true })
        {
            await Task.Delay(50, ct);
            EnsureCurrent();
        }
        if (ContentFrame.Content is ChannelsPage channels && request.PageTag == "channels")
            await channels.WaitForNativeSetupAsync(request, ct);
        else if (ContentFrame.Content is SkillsPage skills && request.PageTag == "skills")
            await skills.WaitForNativeSetupAsync(request, ct);
        else
            throw new InvalidOperationException("The selected setup destination changed.");
        EnsureCurrent();
    }

    private void NavigateInternal(string tag, SetupNativeNavigationRequest? nativeRequest = null)
    {
        _activeNativeRequest = nativeRequest;
        if (tag == "debug" && !DiagnosticsGate.IsVisible)
            tag = "settings";

        var pageType = HubPageRegistry.ResolvePageType(tag);
        if (pageType == null) return;

        // Identity dedupe: navigation identity = (PageType, normalized tag).
        // Page-type-only dedupe would collapse distinct logical destinations
        // that share a Page (e.g. agent switching on WorkspacePage).
        if (nativeRequest is null && ContentFrame.Content is not null &&
            ContentFrame.SourcePageType == pageType && _currentNavTag == tag)
        {
            _contentReady = CreateCompletedContentReady();
            AccessibilityNavigationSignal.WritePageReady(pageType.Name);
            return;
        }

        // Best-effort rail highlight before the page swaps in.
        SyncNavSelection(tag);

        // Pass the tag as the navigation parameter so OnContentFrameNavigated
        // can recover the canonical destination.
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _contentReady = ready;
        if (!ContentFrame.Navigate(pageType, (object?)nativeRequest ?? tag))
            CompleteContentReady(ready, pageName: null);
    }

    /// <summary>
    /// Reflect <paramref name="tag"/> in the NavigationView rail. Suppresses the
    /// resulting SelectionChanged so this programmatic update does not re-enter
    /// <see cref="NavigateInternal"/> (which would push a duplicate back-stack
    /// entry). This matters when called from Back/Forward in OnContentFrameNavigated.
    /// </summary>
    private void SyncNavSelection(string? tag)
    {
        if (tag == null) return;
        var item = FindNavItemForTag(NavView.MenuItems, tag)
                ?? FindNavItemForTag(NavView.FooterMenuItems, tag);
        if (item != null && !ReferenceEquals(NavView.SelectedItem, item))
        {
            _syncingNavSelection = true;
            try { NavView.SelectedItem = item; }
            finally { _syncingNavSelection = false; }
            return;
        }

        if (item == null && NavView.SelectedItem != null)
        {
            _syncingNavSelection = true;
            try { NavView.SelectedItem = null; }
            finally { _syncingNavSelection = false; }
        }
    }

    public async Task WaitForCurrentContentReadyAsync()
    {
        var ready = _contentReady;
        var completed = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        if (completed == ready.Task)
            await ready.Task;
        else
            ready.TrySetResult(true);
    }

    private static NavigationViewItem? FindNavItemForTag(IList<object> items, string tag)
    {
        foreach (var raw in items)
        {
            if (raw is NavigationViewItem item)
            {
                if ((item.Tag as string) == tag) return item;
                if (item.MenuItems.Count > 0)
                {
                    var nested = FindNavItemForTag(item.MenuItems, tag);
                    if (nested != null)
                    {
                        // Expand parent so the user can see the selected child.
                        item.IsExpanded = true;
                        return nested;
                    }
                }
            }
        }
        return null;
    }

    private void UpdateStatus(ConnectionStatus status)
    {
        var snapshot = CurrentApp.ConnectionManager?.CurrentSnapshot;
        var (text, accent) = ComputePillState(status, snapshot);
        StatusPillText.Text = text;
        StatusPillDot.Fill = Controls.GatewayStatusContent.AccentBrush(accent);
        AutomationProperties.SetHelpText(StatusPillButton, text);
    }

    internal void UpdateStatus(GatewayConnectionSnapshot? snapshot, ConnectionStatus status)
    {
        var (text, accent) = ComputePillState(status, snapshot);
        StatusPillText.Text = text;
        StatusPillDot.Fill = Controls.GatewayStatusContent.AccentBrush(accent);
        AutomationProperties.SetHelpText(StatusPillButton, text);
    }

    private static (string Text, ConnectionStatusAccent Accent) ComputePillState(
        ConnectionStatus status, GatewayConnectionSnapshot? snapshot)
    {
        var (labelKey, accent) = ConnectionStatusPresenter.Pill(snapshot?.OverallState, status);
        return (LocalizationHelper.GetString(labelKey), accent);
    }

    private void ScheduleGatewayNavVisibilityForStatus(ConnectionStatus status, bool debounceDisconnected)
    {
        switch (GatewayNavVisibilityDebouncePolicy.GetDecision(status, debounceDisconnected))
        {
            case GatewayNavVisibilityDecision.ShowNow:
                _gatewayNavHideTimer?.Stop();
                UpdateGatewayNavVisibility(connected: true);
                break;
            case GatewayNavVisibilityDecision.HideNow:
                _gatewayNavHideTimer?.Stop();
                UpdateGatewayNavVisibility(connected: false);
                break;
            case GatewayNavVisibilityDecision.ScheduleHide:
                ScheduleGatewayNavHide();
                break;
        }
    }

    private void ScheduleGatewayNavHide()
    {
        if (DispatcherQueue is null)
        {
            UpdateGatewayNavVisibility(connected: false);
            return;
        }

        _gatewayNavHideTimer ??= DispatcherQueue.CreateTimer();
        _gatewayNavHideTimer.Interval = GatewayNavVisibilityDebouncePolicy.DisconnectHideDelay;
        _gatewayNavHideTimer.Tick -= OnGatewayNavHideTimerTick;
        _gatewayNavHideTimer.Tick += OnGatewayNavHideTimerTick;
        _gatewayNavHideTimer.Stop();
        _gatewayNavHideTimer.Start();
    }

    private void OnGatewayNavHideTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_disposed || AppModel is null)
            return;

        if (GatewayNavVisibilityDebouncePolicy.ShouldHideAfterDelay(AppModel.Status))
            UpdateGatewayNavVisibility(connected: false);
    }

    private void UpdateGatewayNavVisibility(bool connected)
    {
        try
        {
            var vis = connected ? Visibility.Visible : Visibility.Collapsed;
            var currentTag = _currentNavTag ?? (NavView?.SelectedItem as NavigationViewItem)?.Tag as string;
            var keepCurrentGatewayPageVisible = !connected &&
                HubPageRegistry.ShouldKeepCurrentPageVisibleDuringDisconnect(currentTag);

            NavSessions.Visibility = vis;
            NavSkills.Visibility = vis;
            NavChannels.Visibility = vis;
            NavInstances.Visibility = vis;
            NavCron.Visibility = vis;
            NavAdvanced.Visibility = keepCurrentGatewayPageVisible ? Visibility.Visible : vis;
            NavGatewaySeparator.Visibility = vis;

            if (!connected)
            {
                if (keepCurrentGatewayPageVisible)
                    return;

                PruneUnavailableGatewayEntries(fallbackTag: null);
                if (HubPageRegistry.IsGatewayPageTag(currentTag))
                    PruneRequested?.Invoke(HubPageRegistry.IsGatewayPageTag, "connection");
            }
        }
        catch (Exception ex)
        {
            Services.Logger.Warn($"[SettingsShellView] UpdateGatewayNavVisibility failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Drops Gateway sections from the host history while the Gateway is unavailable. Called before
    /// the host moves Back or Forward so history never lands on a hidden section.
    /// </summary>
    internal void PruneUnavailableGatewayEntries(string? fallbackTag = null)
    {
        if (AppModel?.Status == ConnectionStatus.Connected)
            return;

        PruneRequested?.Invoke(HubPageRegistry.IsGatewayPageTag, fallbackTag);
    }

    public GatewaySelfInfo? LastGatewaySelf => AppModel?.GatewaySelf;

    private void RebuildAgentNavItems(System.Text.Json.JsonElement data)
    {
        if (!data.TryGetProperty("agents", out var agentsEl) ||
            agentsEl.ValueKind != System.Text.Json.JsonValueKind.Array) return;

        AgentsNavItem.MenuItems.Clear();

        foreach (var agent in agentsEl.EnumerateArray())
        {
            var id = agent.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(id)) continue;
            var name = agent.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;

            var agentItem = new NavigationViewItem
            {
                Content = name ?? id,
                Tag = $"agent:{id}",
                Icon = BuildAgentItemIcon()
            };

            AgentsNavItem.MenuItems.Add(agentItem);
        }
    }

    /// <summary>Extract agent IDs from cached agents data.</summary>
    public List<string> GetAgentIds() => AppModel?.GetAgentIds() ?? new List<string> { "main" };

    public System.Text.Json.JsonElement? LastAgentsData => AppModel?.AgentsList;

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem { Tag: "chat" })
            return;

        // NavigationView finishes focusing its item after ItemInvoked returns.
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!_disposed && _active)
                    NavigateTo("chat");
            }))
            Logger.Warn("Could not queue the Chat navigation because Settings is shutting down.");
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // Skip when the selection was set programmatically to reflect the current section;
        // the host already owns that history entry.
        if (_syncingNavSelection) return;

        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            NavigateCompanion(tag);
    }

    /// <summary>
    /// Authoritative post-navigation hook. Runs for every successful Frame.Navigate, so it is the
    /// single place that rebuilds <see cref="_currentNavTag"/> / <see cref="_currentAgentId"/>,
    /// re-syncs the rail, and re-runs <see cref="InitializeCurrentPage"/> for the visible page.
    /// </summary>
    private void OnContentFrameNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        var nativeRequest = e.Parameter as SetupNativeNavigationRequest;
        var tag = nativeRequest?.PageTag ?? e.Parameter as string;
        _currentNavTag = tag;
        if (nativeRequest is not null)
        {
            _currentAgentId = nativeRequest.Completion.Verification.AgentId!;
            _cachedCommands = null;
        }

        // Keep _currentAgentId aligned with the page that's now visible.
        if (tag != null && tag.StartsWith("agent:"))
        {
            var newAgent = HubPageRegistry.ParseAgentId(tag);
            if (newAgent != _currentAgentId)
            {
                _currentAgentId = newAgent;
                _cachedCommands = null;
            }
        }

        SyncNavSelection(tag);

        InitializeCurrentPage();
        UpdateAppNotificationActionEnabledState();
        ArmContentReady(
            e.Content as FrameworkElement,
            e.Content?.GetType().Name);

        // Navigation activation hook: resolve + assign a DI-backed view model for the
        // navigated page. Pages present in the page->view-model map (currently Settings)
        // get their view model resolved, activated, and assigned as DataContext; other pages
        // only advance the navigation scope (deactivating any prior view model). Contained in a
        // try/catch so a misbehaving view model's activation cannot escape this XAML event handler
        // and crash the app.
        if (e.Content is { } content)
        {
            try
            {
                CurrentApp.PageActivator?.OnNavigatedTo(content, tag);
            }
            catch (Exception ex)
            {
                OpenClawTray.Services.Logger.Error($"[SettingsShellView] Page activation failed: {ex}");
            }
        }
    }

    private void OnContentFrameNavigationFailed(object sender, Microsoft.UI.Xaml.Navigation.NavigationFailedEventArgs e)
    {
        _contentReady.TrySetResult(true);
    }

    private void ArmContentReady(
        FrameworkElement? element,
        string? pageName)
    {
        var ready = _contentReady;
        if (element == null || element.IsLoaded)
        {
            CompleteContentReady(ready, pageName);
            return;
        }

        RoutedEventHandler? loaded = null;
        loaded = (_, _) =>
        {
            element.Loaded -= loaded;
            CompleteContentReady(ready, pageName);
        };
        element.Loaded += loaded;
    }

    private void CompleteContentReady(
        TaskCompletionSource<bool> ready,
        string? pageName)
    {
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (ReferenceEquals(_contentReady, ready) &&
                    ready.TrySetResult(true))
                {
                    AccessibilityNavigationSignal.WritePageReady(pageName);
                }
            });
    }

    /// <summary>
    /// Persist the NavigationView's expanded/compact state on every toggle.
    /// Both PaneOpening and PaneClosing route here; we read the current
    /// state from the sender so we don't have to distinguish the two.
    /// </summary>
    private void OnNavPaneStateChanged(NavigationView sender, object args)
    {
        var settings = CurrentApp.Settings;
        if (settings == null) return;
        // PaneOpening fires BEFORE IsPaneOpen flips, PaneClosing fires
        // BEFORE it flips the other way. Use the event identity to know
        // the new state rather than reading IsPaneOpen.
        var newState = args is NavigationViewPaneClosingEventArgs ? false : true;
        if (settings.HubNavPaneOpen == newState) return;
        settings.HubNavPaneOpen = newState;
        // slopwatch-ignore: SW003 Optional persisted state fallback is intentional; caller continues with defaults or prior state.
        try { settings.Save(); } catch { /* swallow — don't block UI */ }
    }

    private void InitializeCurrentPage()
    {
        switch (ContentFrame.Content)
        {
            case SessionsPage sessions: sessions.Initialize(); break;
            case ConnectionPage connection: connection.Initialize(); break;
            case ChannelsPage channels: channels.Initialize(); break;
            case UsagePage usage: usage.Initialize(); break;
            case CronPage cron: cron.Initialize(); break;
            case SkillsPage skills: skills.Initialize(); break;
            case ConfigPage config:
                try { config.Initialize(); }
                catch (Exception ex)
                {
                    OpenClawTray.Services.Logger.Error($"[SettingsShellView] ConfigPage seed failed: {ex}");
                }
                break;
            case InstancesPage instances: instances.Initialize(); break;
            case PermissionsPage: break;
            case SandboxPage sandbox: sandbox.Initialize(); break;
            case VoiceSettingsPage voice: voice.Initialize(CurrentApp.VoiceService); break;
            case AgentEventsPage agentEvents:
                agentEvents.Initialize(GetAgentIds());
                agentEvents.ClearCentralCache = () => AppModel?.ClearAgentEvents();
                agentEvents.PopulateAgentFilter(GetAgentIds());
                // When navigated via top-level nav (tag "agentevents") show all
                // agents; when reached via an agent-scoped tag (e.g.
                // "agent:main:agentevents") scope to that agent. Reading
                // _currentNavTag (set by OnContentFrameNavigated) is more
                // reliable than peeking NavView.SelectedItem, which may briefly
                // disagree during Back/Forward.
                var eventsAgentFilter = _currentNavTag?.StartsWith("agent:") == true ? _currentAgentId : null;
                agentEvents.SetAgentFilter(eventsAgentFilter);
                // Seed existing events from AppState
                if (agentEvents.EventCount == 0 && AppModel?.AgentEvents is { Count: > 0 } events)
                {
                    for (int i = events.Count - 1; i >= 0; i--)
                        agentEvents.AddEvent(events[i]);
                }
                break;
            case WorkspacePage workspace:
                workspace.AgentId = _currentAgentId;
                workspace.Initialize();
                break;
            case BindingsPage bindings: bindings.Initialize(); break;
            case SettingsPage settings: settings.Initialize(); break;
            case NotificationsPage notifications: notifications.Initialize(_appNotificationService); break;
            case DebugPage debug: debug.Initialize(); break;
        }
    }

    public void SetActivityFilter(string? filter)
    {
        // ActivityPage has been removed; the method is kept as a no-op so any
        // remaining external callers (e.g. legacy deep links) don't NRE.
        _ = filter;
    }

    // Command search retains its catalog in the sidebar flyout. Keyboard shortcuts belong to the
    // host window (Ctrl+K opens this flyout; Alt+Left/Right drive its single history).

    private ImmutableArray<HubCommand>? _cachedCommands;

    private void OnCommandSearchOpening(object sender, object e)
    {
        _cachedCommands = BuildCommandList();
        TitleSearchBox.Text = string.Empty;
        TitleSearchBox.ItemsSource = HubPageRegistry.SearchCommands(_cachedCommands.Value, string.Empty);
    }

    private void OnCommandSearchOpened(object sender, object e)
    {
        TitleSearchBox.Focus(FocusState.Programmatic);
        TitleSearchBox.IsSuggestionListOpen = true;
    }

    private void OnCommandSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != global::Windows.System.VirtualKey.Escape) return;
        e.Handled = true;
        TitleSearchBox.IsSuggestionListOpen = false;
        CommandSearchFlyout.Hide();
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _cachedCommands ??= BuildCommandList();
        sender.ItemsSource = HubPageRegistry.SearchCommands(_cachedCommands.Value, sender.Text);
    }

    private void OnSearchSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is HubCommand cmd)
            sender.Text = cmd.Title;
    }

    private void OnSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is HubCommand cmd)
        {
            CommandSearchFlyout.Hide();
            sender.Text = "";
            sender.ItemsSource = null;
            _cachedCommands = null;
            ExecuteCommand(cmd);
        }
        else if (HubPageRegistry.SearchCommands(_cachedCommands ?? BuildCommandList(), args.QueryText)
            .FirstOrDefault() is { } first)
        {
            // Enter pressed without selecting — execute first match
            CommandSearchFlyout.Hide();
            sender.Text = "";
            sender.ItemsSource = null;
            _cachedCommands = null;
            ExecuteCommand(first);
        }
    }

    internal ImmutableArray<HubCommand> BuildCommandList()
    {
        return HubCommandCatalog.Build(AppModel, CurrentApp.Settings, _currentAgentId);
    }

    private void ToggleCommandPalettePermission(HubSettingToggle toggle)
    {
        var settings = CurrentApp.SettingsOrNull;
        if (settings == null)
            return;

        var settingName = GetSettingName(toggle);
        var nextValue = !GetCurrentSettingValue(settings, toggle);
        try
        {
            if (CurrentApp.SettingsStore is { } store)
            {
                _commandPaletteSettingsOrigin ??= store.CreateOrigin();
                store.Update(_commandPaletteSettingsOrigin, edit =>
                    ApplySettingValue(edit, toggle, nextValue));
            }
            else
            {
                Services.Logger.Warn($"[SettingsShellView] ISettingsStore unavailable for {settingName}. Falling back to SettingsManager.Save.");
                ApplySettingValue(settings, toggle, nextValue);
                settings.Save();
            }

            RaiseSettingsSaved();
        }
        catch (Exception ex)
        {
            Services.Logger.Warn($"[SettingsShellView] Failed to persist {settingName}: {ex.Message}");
        }
    }

    private static string GetSettingName(HubSettingToggle toggle) => toggle switch
    {
        HubSettingToggle.NodeMode => nameof(SettingsManager.EnableNodeMode),
        HubSettingToggle.Camera => nameof(SettingsManager.NodeCameraEnabled),
        HubSettingToggle.Canvas => nameof(SettingsManager.NodeCanvasEnabled),
        HubSettingToggle.ScreenCapture => nameof(SettingsManager.NodeScreenEnabled),
        HubSettingToggle.BrowserControl => nameof(SettingsManager.NodeBrowserProxyEnabled),
        _ => throw new ArgumentOutOfRangeException(nameof(toggle))
    };

    private static bool GetCurrentSettingValue(SettingsManager settings, HubSettingToggle toggle) =>
        toggle switch
        {
            HubSettingToggle.NodeMode => settings.EnableNodeMode,
            HubSettingToggle.Camera => settings.NodeCameraEnabled,
            HubSettingToggle.Canvas => settings.NodeCanvasEnabled,
            HubSettingToggle.ScreenCapture => settings.NodeScreenEnabled,
            HubSettingToggle.BrowserControl => settings.NodeBrowserProxyEnabled,
            _ => throw new ArgumentOutOfRangeException(nameof(toggle))
        };

    private static void ApplySettingValue(
        ISettingsEditor settings,
        HubSettingToggle toggle,
        bool value)
    {
        switch (toggle)
        {
            case HubSettingToggle.NodeMode: settings.EnableNodeMode = value; break;
            case HubSettingToggle.Camera: settings.NodeCameraEnabled = value; break;
            case HubSettingToggle.Canvas: settings.NodeCanvasEnabled = value; break;
            case HubSettingToggle.ScreenCapture: settings.NodeScreenEnabled = value; break;
            case HubSettingToggle.BrowserControl: settings.NodeBrowserProxyEnabled = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(toggle));
        }
    }

    private static void ApplySettingValue(
        SettingsManager settings,
        HubSettingToggle toggle,
        bool value)
    {
        switch (toggle)
        {
            case HubSettingToggle.NodeMode: settings.EnableNodeMode = value; break;
            case HubSettingToggle.Camera: settings.NodeCameraEnabled = value; break;
            case HubSettingToggle.Canvas: settings.NodeCanvasEnabled = value; break;
            case HubSettingToggle.ScreenCapture: settings.NodeScreenEnabled = value; break;
            case HubSettingToggle.BrowserControl: settings.NodeBrowserProxyEnabled = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(toggle));
        }
    }

    private void ExecuteCommand(HubCommand command)
    {
        switch (command.Action.Kind)
        {
            case HubCommandActionKind.Navigate when command.Action.Value is { Length: > 0 } tag:
                NavigateTo(tag);
                break;
            case HubCommandActionKind.OpenDashboard:
                ((IAppCommands)Application.Current).OpenDashboard(command.Action.Value);
                break;
            case HubCommandActionKind.ToggleSetting when command.Action.Toggle is { } toggle:
                ToggleCommandPalettePermission(toggle);
                break;
        }
    }

    #region High Contrast icon fallback

    // Fixed-color SVGs provide the normal Hub presentation. In High Contrast,
    // App.xaml resolves this path to system-foreground Fluent glyphs without
    // constructing or subscribing to the legacy WinRT accessibility API.
    private static readonly Dictionary<string, string> s_highContrastGlyphFallback = new()
    {
        { "chat",        "\uE8BD" },
        { "connection",  "\uE839" },
        { "sessions",    "\uE8F2" },
        { "skills",      "\uE945" },
        { "channels",    "\uEC05" },
        { "instances",   "\uE977" },
        { "agentevents", "\uE943" },
        { "bindings",    "\uE8AD" },
        { "config",      "\uE90F" },
        { "usage",       "\uE9D9" },
        { "cron",        "\uE787" },
        { "voice",       "\uE720" },
        { "settings",    "\uE713" },
        { "permissions", "\uEA18" },
        { "sandbox",     "\uE72E" },
        { "activity",    "\uEA95" },
        { "notifications", "\uE7F4" },
        { "debug",       "\uEBE8" },
    };

    private const string AdvancedGroupGlyph = "\uE950";
    private const string AgentsGroupGlyph = "\uE99A";
    private bool _isHighContrast;

    private void ApplyHighContrastFallbackIfNeeded()
    {
        const string resourceKey = "HubNavigationUseHighContrastIcons";
        _isHighContrast = Application.Current.Resources.ContainsKey(resourceKey)
            && Application.Current.Resources[resourceKey] is true;
        if (!_isHighContrast)
            return;

        SwapToFontIcons(NavView.MenuItems);
        SwapToFontIcons(NavView.FooterMenuItems);
    }

    private void SwapToFontIcons(IList<object> items)
    {
        foreach (var value in items)
        {
            if (value is not NavigationViewItem item)
                continue;

            item.Icon = ResolveHighContrastIcon(item);
            if (item.MenuItems.Count > 0)
                SwapToFontIcons(item.MenuItems);
        }
    }

    private IconElement ResolveHighContrastIcon(NavigationViewItem item)
    {
        if (item.Tag is string tag)
        {
            if (s_highContrastGlyphFallback.TryGetValue(tag, out var glyph))
                return FluentIconCatalog.Build(glyph, 20);
            if (tag.StartsWith("agent:", StringComparison.Ordinal))
                return FluentIconCatalog.Build(AgentsGroupGlyph, 20);
        }

        if (item == AgentsNavItem)
            return FluentIconCatalog.Build(AgentsGroupGlyph, 20);
        if (item == NavAdvanced)
            return FluentIconCatalog.Build(AdvancedGroupGlyph, 20);

        return FluentIconCatalog.Build("\uE700", 20);
    }

    private IconElement BuildAgentItemIcon()
    {
        if (_isHighContrast)
            return FluentIconCatalog.Build(AgentsGroupGlyph, 20);

        return new ImageIcon
        {
            Source = (ImageSource)NavView.Resources["Agents_Icon"]
        };
    }

    #endregion
}
