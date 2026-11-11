// <summary>
// Native Settings surface hosted by WorkspaceWindow. It owns the Settings navigation rail, page frame, status
// presentation and command-search flyout, while HubPageRegistry supplies navigation policy and the host owns history.
// Activating or deactivating this view starts or releases page scopes. It never mounts the Chat page.
// Usage:
//   var view = new SettingsShellView { AppModel = appState };
//   view.BindToAppState();
//   view.ShowSection("settings");
//   view.Deactivate();
// </summary>
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
/// Hosts Settings-specific WinUI controls and applies routes selected by the Workspace host.
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
    internal HubSearchController? SearchController { get; set; }
    internal Action<HubSearchTarget>? SearchNavigation { get; set; }
    private bool _suppressAppNotificationClosed;
    private bool _disposed;
    private bool _active;
    private double _hostContentTopInset;

    /// <summary>Raised when the rail or an in-page link requests another Settings section.</summary>
    internal Action<string>? NavigateRequested { get; set; }
    internal Action? PageNavigationChanged { get; set; }
    internal Action? PageContentChanged { get; set; }

    /// <summary>Raised when a notification route leaves Settings.</summary>
    internal Action<string>? WorkspaceRouteRequested { get; set; }

    /// <summary>Raised by Back to app; the host returns to the app destination Settings was opened from.</summary>
    internal Action? ExitRequested { get; set; }

    /// <summary>
    /// Asks the host to drop unavailable Settings sections from history. The second argument is the
    /// section to show when the current one was pruned.
    /// </summary>
    internal Action<Func<string, bool>, string?>? PruneRequested { get; set; }

    /// <summary>
    /// The host's search entry point. A deferred search reopen replays through it so the window's
    /// visibility and modal-dialog rules apply; without a host the view opens its own search.
    /// </summary>
    internal Action? CommandCenterRequested { get; set; }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _gatewayNavHideTimer;

    public event EventHandler? SettingsSaved;

    /// <summary>Notifies host subscribers that a Settings action completed a save.</summary>
    public void RaiseSettingsSaved() => SettingsSaved?.Invoke(this, EventArgs.Empty);

    /// <summary>Initializes the Settings controls and their view-owned visual state.</summary>
    public SettingsShellView()
    {
        InitializeComponent();
        CommandSearchFlyout.FlyoutPresenterStyle = SearchHost.CreatePresenterStyle();
        ContentFrame.IsNavigationStackEnabled = false;
        var searchLabel = LocalizationHelper.GetString("TitleSearchBox.PlaceholderText");
        AutomationProperties.SetName(SettingsSearchButton, searchLabel);
        ToolTipService.SetToolTip(SettingsSearchButton, searchLabel);
        var backToApp = LocalizationHelper.GetString("SettingsShell_BackToApp");
        var escapeKey = LocalizationHelper.GetString("SettingsShell_BackToAppKeyHint");
        BackToAppLabel.Text = backToApp;
        BackToAppKeyHintText.Text = escapeKey;
        AutomationProperties.SetName(BackToAppButton, backToApp);
        AutomationProperties.SetAcceleratorKey(BackToAppButton, escapeKey);
        _backToAppToolTip = $"{backToApp} ({escapeKey})";
        SettingsTitle.Text = LocalizationHelper.GetString("SettingsShell_Title");
        NavView.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, (_, _) => UpdatePaneChrome());
        UpdatePaneChrome();
        RefreshDiagnosticsNavVisibility();
        ApplyHighContrastFallbackIfNeeded();
        RootGrid.SizeChanged += OnRootGridSizeChanged;
        _commandSearchShow = new FlyoutShowRequest(CommandSearchFlyout, () => (CommandCenterRequested ?? OpenCommandCenter)());
    }

    private readonly FlyoutShowRequest _commandSearchShow;
    private readonly string _backToAppToolTip;

    /// <summary>The shared account, Dashboard and bell footer; <c>WorkspaceWindow</c> applies its state.</summary>
    internal WorkspacePaneFooter Footer => PaneFooterControl;

    /// <summary>The element that opens the command search flyout for the current pane state.</summary>
    private FrameworkElement SearchAnchor => NavView.IsPaneOpen ? SettingsSearchButton : NavPaneToggleButton;

    /// <summary>Shows or removes the diagnostics rail item and prunes its unavailable history entries.</summary>
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
        PageNavigationChanged = null;
        PageContentChanged = null;
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
            { Kind: AppNotificationInfoBarActionKind.NotificationRoute, Route.Length: > 0 })
        {
            if (_appNotificationService is { } notifications)
            {
                var notificationId = _currentAppNotificationPresentation.Notification!.Id;
                AsyncEventHandlerGuard.Run(
                    () => notifications.NavigateActionAsync(notificationId, ((IAppCommands)CurrentApp).TryNavigateAsync),
                    new AppLogger(), nameof(OnAppNotificationActionButtonClick));
            }
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
                        SearchController?.Refresh();
                        // Defer nav visibility to avoid stowed exceptions during NavigationView layout
                        DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                        {
                            if (_disposed) return;
                            ScheduleGatewayNavVisibilityForStatus(AppModel!.Status, debounceDisconnected: true);
                        });
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
    /// Applies the pane state saved by the toggle. <c>WorkspaceWindow.EnsureSettings</c> calls this when it
    /// creates the view, and <see cref="ContentOnly"/> calls it again when the section list returns.
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
        PersistNavPaneState(NavView.IsPaneOpen);
    }

    /// <summary>
    /// Saves the pane state the user chose with the toggle. Only the toggle records that choice: NavigationView
    /// also opens and closes the pane itself, for example when a pinned page hides the section list or a
    /// controller's View button opens it, and those changes are not saved.
    /// </summary>
    private static void PersistNavPaneState(bool isOpen)
    {
        var settings = CurrentApp.Settings;
        if (settings is null || settings.HubNavPaneOpen == isOpen)
            return;
        settings.HubNavPaneOpen = isOpen;
        // slopwatch-ignore: SW003 Optional persisted state fallback is intentional; caller continues with defaults or prior state.
        try { settings.Save(); } catch { /* swallow — don't block UI */ }
    }

    private void UpdatePaneChrome()
    {
        var open = NavView.IsPaneOpen;
        SettingsNavigationActions.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        // The compact rail keeps Back to app as an arrow aligned with the item icons, like NavigationView
        // items, which show their label as a tooltip only while compact.
        var headerText = open ? Visibility.Visible : Visibility.Collapsed;
        BackToAppLabel.Visibility = headerText;
        BackToAppKeyHint.Visibility = headerText;
        SettingsTitle.Visibility = headerText;
        BackToAppButton.Width = open ? double.NaN : 40;
        BackToAppButton.HorizontalAlignment = open ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        ToolTipService.SetToolTip(BackToAppButton, open ? null : _backToAppToolTip);
        UpdatePageAllocation();
        // Footer popups are anchored to buttons that move when the rail opens or closes.
        if (PaneFooterControl.Layout != (NavView.IsPaneOpen ? WorkspacePaneFooterLayout.Expanded : WorkspacePaneFooterLayout.Rail))
            PaneFooterControl.HideFlyouts();
        PaneFooterControl.Layout = NavView.IsPaneOpen ? WorkspacePaneFooterLayout.Expanded : WorkspacePaneFooterLayout.Rail;
    }

    // Back/Forward live in the window-level pair driven only by WorkspaceNavigationHistory.
    // This view keeps no navigation stack of its own.
    internal bool HasOpenDashboard => ContentFrame.Content is DashboardsPage { HasOpenBoard: true };

    internal bool TryReturnToDashboardGallery() =>
        ContentFrame.Content is DashboardsPage dashboards && dashboards.TryReturnToGallery();

    internal void RefreshPageNavigation()
    {
        if (!_disposed)
            PageNavigationChanged?.Invoke();
    }

    /// <summary>
    /// Shows only the section content, hiding this view's own section list. Used when a pinned page
    /// opens beside the Workspace sidebar.
    /// </summary>
    internal bool ContentOnly
    {
        get => !NavView.IsPaneVisible;
        set
        {
            var wasContentOnly = ContentOnly;
            NavView.IsPaneVisible = !value;
            SettingsNavigationToolbar.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            // Hiding the section list closes the pane and showing it reopens it, so restore the saved choice.
            if (wasContentOnly && !value && CurrentApp.Settings is { } settings)
                ApplyNavPaneState(settings);
            UpdatePageAllocation();
        }
    }

    private void UpdatePageAllocation()
    {
        // Existing pages retain their own padding. Only opted-in pages allocate shell chrome.
        if (ContentFrame.Content is ISettingsPageContentInset page)
            page.ApplyTopInset(ContentOnly ? _hostContentTopInset
                : WorkspaceSettingsLayout.PageTopInset(false, NavView.IsPaneOpen));
    }

    internal void SetHostContentTopInset(double inset)
    {
        if (!double.IsFinite(inset) || inset < 0)
            throw new ArgumentOutOfRangeException(nameof(inset));
        _hostContentTopInset = inset;
        UpdatePageAllocation();
    }

    /// <summary>Returns the currently displayed page in the content frame.</summary>
    public object? CurrentPage => ContentFrame?.Content;

    internal bool IsSectionShown(string tag) =>
        !_disposed && _active && ContentFrame.Content is not null &&
        _currentNavTag == tag;

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

    /// <summary>
    /// Requests a section. Rail clicks, in-page links and search all flow through here; the host
    /// decides whether it becomes a history entry. Workspace routes leave Settings.
    /// </summary>
    public void NavigateTo(string tag)
    {
        WorkspaceNavigation.Dispatch(tag, _ => WorkspaceRouteRequested?.Invoke(tag), NavigateCompanion,
            _ => WorkspaceRouteRequested?.Invoke(tag));
    }

    private void NavigateCompanion(string tag) =>
        NavigateRequested?.Invoke(WorkspaceNavigation.NormalizeSettingsTag(tag, _currentAgentId));

    /// <summary>
    /// Renders <paramref name="tag"/> without touching history. Also re-activates the view after
    /// <see cref="Deactivate"/>, so re-entry never depends on <c>Frame.Navigated</c> alone.
    /// </summary>
    internal void ShowSection(string tag)
    {
        if (_disposed) return;
        _active = true;
        NavigateInternal(tag);
    }

    /// <summary>
    /// Leaves the Settings surface: stops every page, closes Settings flyouts, and
    /// resets the activation scope so no view model or subscription stays live behind the chat.
    /// </summary>
    internal void Deactivate()
    {
        if (!_active && ContentFrame.Content is null) return;
        _active = false;
        HideTransientSurfaces();
        _currentNavTag = null;
        // Clearing Content does not raise OnNavigatedFrom, so pages with cleanup are stopped here.
        (ContentFrame.Content as DebugPage)?.Deactivate();
        (ContentFrame.Content as SystemsPage)?.Dispose();
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
        _commandSearchShow.Cancel();
        CommandSearchFlyout.Hide();
    }

    internal void OpenCommandCenter()
    {
        if (_disposed || !IsLoaded) return;
        _commandSearchShow.ShowAt(SearchAnchor);
    }

    internal bool DeferCommandSearchUntilClosed() => _commandSearchShow.DeferUntilClosed();

    private void NavigateInternal(string tag)
    {
        if (tag == "debug" && !DiagnosticsGate.IsVisible)
            tag = "settings";

        var pageType = HubPageRegistry.ResolvePageType(tag);
        if (pageType == null) return;

        // Identity dedupe: navigation identity = (PageType, normalized tag).
        // Page-type-only dedupe would collapse distinct logical destinations
        // that share a Page (e.g. agent switching on WorkspacePage).
        if (ContentFrame.Content is not null &&
            ContentFrame.SourcePageType == pageType && _currentNavTag == tag)
        {
            // Boards open in-page without changing the route, so the same tag can mean a new destination.
            if (ContentFrame.Content is DashboardsPage dashboards)
                dashboards.Initialize(this, tag);
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
        if (!ContentFrame.Navigate(pageType, tag))
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
        tag = HubPageRegistry.NavigationTag(tag);
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

    /// <summary>Waits briefly for the current Settings page to report that its initial content is ready.</summary>
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
            NavInstances.Visibility = keepCurrentGatewayPageVisible && HubPageRegistry.IsSystemsTag(currentTag)
                ? Visibility.Visible
                : vis;
            NavCron.Visibility = vis;
            var gatewayToolVisibility = keepCurrentGatewayPageVisible ? Visibility.Visible : vis;
            AgentsNavItem.Visibility = gatewayToolVisibility;
            NavUsage.Visibility = gatewayToolVisibility;
            NavConfig.Visibility = gatewayToolVisibility;

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

    private void OnBackToAppClick(object sender, RoutedEventArgs e)
    {
        if (_disposed || !_active) return;
        if (ExitRequested is { } exit) exit();
        else NavigateTo("chat");
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
            SearchController?.Refresh();
        }

        // Keep _currentAgentId aligned with the page that's now visible.
        if (tag != null && tag.StartsWith("agent:"))
        {
            var newAgent = HubPageRegistry.ParseAgentId(tag);
            if (newAgent != _currentAgentId)
            {
                _currentAgentId = newAgent;
                SearchController?.Refresh();
            }
        }

        SyncNavSelection(tag);

        InitializeCurrentPage();
        PageContentChanged?.Invoke();
        UpdatePageAllocation();
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

    private void InitializeCurrentPage()
    {
        switch (ContentFrame.Content)
        {
            case SessionsPage sessions: sessions.Initialize(); break;
            case ConnectionPage connection: connection.Initialize(); break;
            case UsagePage usage: usage.Initialize(); break;
            case CronPage cron:
                cron.AgentId = _currentAgentId;
                cron.Initialize();
                break;
            case ConfigPage config:
                try { config.Initialize(); }
                catch (Exception ex)
                {
                    OpenClawTray.Services.Logger.Error($"[SettingsShellView] ConfigPage seed failed: {ex}");
                }
                break;
            case SystemsPage systems: systems.Initialize(); break;
            case DashboardsPage dashboards: dashboards.Initialize(this, _currentNavTag); break;
            case PermissionsPage: break;
            case SandboxPage sandbox: sandbox.Initialize(); break;
            case VoiceSettingsPage voice: voice.Initialize(CurrentApp.VoiceService); break;
            case WorkspacePage workspace:
                workspace.AgentId = _currentAgentId;
                workspace.Initialize();
                break;
            case SettingsPage settings: settings.Initialize(); break;
            case AboutPage about: about.Initialize(); break;
            case NotificationsPage notifications: notifications.Initialize(CurrentApp.InboxFeed); break;
            case DebugPage debug: debug.Initialize(); break;
        }
    }

    public void SetActivityFilter(string? filter)
    {
        // ActivityPage has been removed; the method is kept as a no-op so any
        // remaining external callers (e.g. legacy deep links) don't NRE.
        _ = filter;
    }

    private void OnCommandSearchOpening(object sender, object e)
    {
        if (SearchController is { } controller && SearchNavigation is { } navigate)
            SettingsSearchHost.Begin(controller, navigate, () => CommandSearchFlyout.Hide(), "SettingsSearchBox");
    }

    private void OnCommandSearchClosed(object sender, object e) => SettingsSearchHost.End();
    private void OnCommandSearchOpened(object sender, object e) => SettingsSearchHost.FocusInput();

    internal void RevealSearchTarget(HubSearchTarget target)
    {
        var page = ContentFrame.Content as FrameworkElement;
        if (page is null || target.Focus is null) return;
        var client = CurrentApp.GatewayClient;
        var epoch = client?.ServerHandshakeGeneration;
        var searchGeneration = SearchController?.Generation;
        void Reveal()
        {
            if (!_active || !ReferenceEquals(ContentFrame.Content, page) ||
                !ReferenceEquals(client, CurrentApp.GatewayClient) || epoch != client?.ServerHandshakeGeneration) return;
            if (SearchController is { } search && search.IsOwner(SettingsSearchHost) &&
                search.Generation != searchGeneration) return;
            switch (page)
            {
                case PermissionsPage permissions: permissions.RevealSearchSetting(target.Focus); break;
                case ConfigPage config: config.RevealSearchPath(target.Focus); break;
                case CronPage cron: cron.RevealSearchJob(target.Focus); break;
            }
        }
        if (page.IsLoaded) Reveal();
        else
        {
            void Loaded(object sender, RoutedEventArgs e) { page.Loaded -= Loaded; Reveal(); }
            page.Loaded += Loaded;
        }
    }

    #region High Contrast icon fallback

    // Fixed-color SVGs provide the normal Hub presentation. In High Contrast,
    // App.xaml resolves this path to system-foreground Fluent glyphs without
    // constructing or subscribing to the legacy WinRT accessibility API.
    private static readonly Dictionary<string, string> s_highContrastGlyphFallback = new()
    {
        { "connection",  "\uE839" },
        { "sessions",    "\uE8F2" },
        { "systems",     "\uE977" },
        { "config",      "\uE90F" },
        { "usage",       "\uE9D9" },
        { "cron",        "\uE787" },
        { "model",       "\uE8F1" },
        { "voice",       "\uE720" },
        { "settings",    "\uE713" },
        { "permissions", "\uEA18" },
        { "sandbox",     "\uE72E" },
        { "activity",    "\uEA95" },
        { "notifications", "\uE7F4" },
        { "debug",       "\uEBE8" },
        { "about",       FluentIconCatalog.About },
    };

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
