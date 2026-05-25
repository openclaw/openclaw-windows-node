using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Pages;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using WinUIEx;

namespace OpenClawTray.Windows;

public sealed partial class HubWindow : WindowEx
{
    public bool IsClosed { get; private set; }

    private static App CurrentApp => (App)Application.Current;

    internal AppState? AppModel { get; set; }
    private string _currentAgentId = "main";
    public string CurrentAgentId => _currentAgentId;

    // Legacy compatibility alias
    public string SelectedAgentId => _currentAgentId;
    public Action<string?>? OpenDashboardAction { get; set; }
    public Action? CheckForUpdatesAction { get; set; }
    public Action? ConnectAction { get; set; }
    public Action? DisconnectAction { get; set; }
    public Action? ReconnectAction { get; set; }
    public Action? OpenSetupAction { get; set; }
    public Action? OpenConnectionStatusAction { get; set; }
    public Action? OpenVoiceAction { get; set; }
    public OpenClaw.Connection.IGatewayConnectionManager? ConnectionManager { get; set; }
    public OpenClaw.Connection.GatewayRegistry? GatewayRegistry { get; set; }

    // Node service state (set by App.xaml.cs in ShowHub)
    public bool NodeIsConnected { get; set; }
    public bool NodeIsPaired { get; set; }
    public bool NodeIsPendingApproval { get; set; }
    public string? LastAuthError { get; set; }
    public string? NodeShortDeviceId { get; set; }
    public VoiceService? VoiceServiceInstance { get; set; }
    /// <summary>When true, ChatPage should auto-start voice recording on next navigation. Consumed (reset to false) by ChatPage.</summary>
    public bool PendingAutoStartVoice { get; set; }
    /// <summary>Session key the chat surface should select on its next mount. Consumed (cleared) by ChatPage.</summary>
    public string? PendingChatSessionKey { get; set; }
    public string? NodeFullDeviceId { get; set; }
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _gatewayNavHideTimer;

    // Cached gateway data — pages read these on navigation
    public SessionInfo[]? LastSessions { get; private set; }
    public ChannelHealth[]? LastChannels { get; private set; }
    public GatewayUsageInfo? LastUsage { get; private set; }
    public GatewayCostUsageInfo? LastUsageCost { get; private set; }
    public GatewayUsageStatusInfo? LastUsageStatus { get; private set; }
    public GatewayNodeInfo[]? LastNodes { get; private set; }

    public System.Text.Json.JsonElement? LastConfig { get; private set; }
    public System.Text.Json.JsonElement? LastConfigSchema { get; private set; }
    public System.Text.Json.JsonElement? LastSkillsData { get; private set; }
    public string? LastSkillsAgentId { get; private set; }
    public System.Text.Json.JsonElement? LastAgentFilesList { get; private set; }
    public string? LastAgentFilesListAgentId { get; private set; }
    private string? _pendingAgentFilesListAgentId;


    // Event for settings saved (App.xaml.cs subscribes)
    public event EventHandler? SettingsSaved;

    public void RaiseSettingsSaved() => SettingsSaved?.Invoke(this, EventArgs.Empty);

    public HubWindow()
    {
        InitializeComponent();
        ApplyHighContrastFallbackIfNeeded();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        Closed += (s, e) =>
        {
            IsClosed = true;
            _gatewayNavHideTimer?.Stop();
            if (AppModel != null)
                AppModel.PropertyChanged -= OnAppModelChanged;
        };

        this.SetWindowSize(1000, 650);
        this.CenterOnScreen();
        this.SetIcon(IconHelper.GetStatusIconPath(ConnectionStatus.Connected));

        RootGrid.SizeChanged += OnRootGridSizeChanged;
    }

    /// <summary>
    /// Subscribe to AppState property changes for title bar and nav updates.
    /// Called from App.ShowHub() after setting AppModel.
    /// </summary>
    internal void BindToAppState()
    {
        if (AppModel != null)
        {
            AppModel.PropertyChanged += OnAppModelChanged;
            UpdateTitleBarStatus(AppModel.Status);
            ScheduleGatewayNavVisibilityForStatus(AppModel.Status, debounceDisconnected: false);

            // Apply agents list that may have arrived before this window opened.
            if (AppModel.AgentsList.HasValue)
                RebuildAgentNavItems(AppModel.AgentsList.Value);
        }
    }

    private void OnAppModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsClosed || AppModel == null) return;
        try
        {
            switch (e.PropertyName)
            {
                case nameof(AppState.Status):
                    DispatcherQueue?.TryEnqueue(() =>
                    {
                        if (IsClosed) return;
                        _cachedCommands = null;
                        var status = AppModel!.Status;
                        UpdateTitleBarStatus(status);
                        // Defer nav visibility to avoid stowed exceptions during NavigationView layout
                        DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                        {
                            if (IsClosed) return;
                            ScheduleGatewayNavVisibilityForStatus(AppModel!.Status, debounceDisconnected: true);
                        });
                });
                break;
            case nameof(AppState.GatewaySelf):
                DispatcherQueue?.TryEnqueue(() =>
                {
                    if (IsClosed) return;
                    UpdateTitleBarStatus(AppModel!.Status);
                    if (ContentFrame?.Content is AboutPage about)
                        about.RefreshGatewayInfo();
                });
                break;
            case nameof(AppState.AgentsList):
                DispatcherQueue?.TryEnqueue(() =>
                {
                    if (IsClosed) return;
                    if (AppModel!.AgentsList.HasValue)
                        RebuildAgentNavItems(AppModel.AgentsList.Value);
                });
                break;
        }
        }
        catch (Exception ex)
        {
            Services.Logger.Warn($"[HubWindow] OnAppModelChanged({e.PropertyName}) failed: {ex.Message}");
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

    private void OnTitleBarStatusTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        NavigateTo("connection");
    }

    private void OnNavPaneToggleButtonClick(object sender, RoutedEventArgs e)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    /// <summary>
    /// Navigate to the default page. Call after setting AppModel.
    /// </summary>
    public void NavigateToDefault()
    {
        if (ContentFrame.Content == null)
        {
            NavigateTo("connection");
        }
    }

    /// <summary>Returns the currently displayed page in the content frame.</summary>
    public object? CurrentPage => ContentFrame?.Content;

    // Canonical tag of the page currently shown in ContentFrame; tracked here
    // (rather than relying on NavView.SelectedItem) so navigation identity
    // includes the tag — important for agent-scoped pages where several tags
    // map to the same Page type (e.g. "sessions" vs "agent:main:sessions"
    // both → SessionsPage), and for the per-page "Back to ..." link logic
    // that needs to know whether the user arrived via a cross-page link.
    private string? _currentNavTag;

    // Set true while a programmatic SelectedItem update is in flight, to
    // suppress the resulting SelectionChanged from re-entering NavigateInternal.
    private bool _syncingNavSelection;

    // Set by NavigateTo(tag, originTag); consumed by OnContentFrameNavigated
    // when the new page is initialized, then surfaced as LastNavigationOrigin
    // so destination pages can decide whether to show an inline back link.
    private string? _pendingNavigationOrigin;

    /// <summary>
    /// Tag of the page the user navigated FROM on the most recent navigation,
    /// or <c>null</c> if the navigation didn't declare an origin (e.g. rail
    /// click, deep link, app start). Destination pages read this in
    /// <c>Initialize</c> to decide whether to surface a "Back to X" affordance.
    /// </summary>
    public string? LastNavigationOrigin { get; private set; }

    /// <summary>
    /// Navigate to a specific page by tag name (e.g. "connection", "sessions", "channels").
    /// </summary>
    public void NavigateTo(string tag) => NavigateTo(tag, null);

    /// <summary>
    /// Navigate to a specific page by tag, declaring which logical surface
    /// initiated the navigation. The destination page can read this via
    /// <see cref="LastNavigationOrigin"/> to render an inline "Back to ..."
    /// link — used by cross-page links on the Connection page so users have
    /// a one-click return path without relying on the rail or a chrome back
    /// button.
    /// </summary>
    public void NavigateTo(string tag, string? originTag)
    {
        _pendingNavigationOrigin = originTag;
        NavigateInternal(NormalizeNavTag(tag));
    }

    private string NormalizeNavTag(string tag)
    {
        // Map legacy tags — Home page was retired in favor of the Lobby/Cockpit
        // layout on Connection. Any caller still using "home" or "general"
        // (deep links, persisted nav state, command palette) lands here.
        if (tag == "home" || tag == "general") return "connection";
        if (tag == "about") return "info";
        if (tag == "nodes") return "instances";
        // Map legacy agent-scoped workspace/cron tags
        if (tag == "cron") return $"agent:{_currentAgentId}:cron";
        if (tag == "workspace") return $"agent:{_currentAgentId}:workspace";
        return tag;
    }

    private void NavigateInternal(string tag)
    {
        var pageType = TagToPageType(tag);
        if (pageType == null)
        {
            // Unknown tag: nothing to navigate, but we still need to discard
            // any pending origin so it doesn't leak into the next real
            // navigation (where it would surface a wrong "Back to ..." link).
            _pendingNavigationOrigin = null;
            return;
        }

        // Identity dedupe: navigation identity = (PageType, normalized tag).
        // Page-type-only dedupe would collapse distinct logical destinations
        // that share a Page (e.g. agent switching on WorkspacePage), and
        // would also push duplicate back-stack entries when the user
        // re-invokes the current page.
        if (ContentFrame.SourcePageType == pageType && _currentNavTag == tag)
        {
            // Same as above: Frame.Navigate is skipped, so
            // OnContentFrameNavigated won't run to consume the origin. If the
            // caller changed origin context, refresh the active page so inline
            // back-link state stays accurate.
            var pendingOrigin = _pendingNavigationOrigin;
            _pendingNavigationOrigin = null;
            if (!string.Equals(LastNavigationOrigin, pendingOrigin, StringComparison.Ordinal))
            {
                LastNavigationOrigin = pendingOrigin;
                InitializeCurrentPage();
            }
            return;
        }

        // Best-effort rail highlight. Suppress the selection-changed callback
        // so this programmatic update doesn't re-enter NavigateInternal.
        var item = FindNavItemForTag(NavView.MenuItems, tag)
                ?? FindNavItemForTag(NavView.FooterMenuItems, tag);
        if (item != null && !ReferenceEquals(NavView.SelectedItem, item))
        {
            _syncingNavSelection = true;
            try { NavView.SelectedItem = item; }
            finally { _syncingNavSelection = false; }
        }

        // Pass the tag as the navigation parameter so OnContentFrameNavigated
        // can recover the canonical destination on Back/Forward.
        ContentFrame.Navigate(pageType, tag);
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

    private void UpdateTitleBarStatus(ConnectionStatus status)
    {
        var (color, text) = status switch
        {
            ConnectionStatus.Connected => (Microsoft.UI.Colors.LimeGreen, "Connected"),
            ConnectionStatus.Connecting => (Microsoft.UI.Colors.Orange, "Connecting…"),
            ConnectionStatus.Error => (Microsoft.UI.Colors.Red, "Error"),
            _ => (Microsoft.UI.Colors.Gray, "Disconnected")
        };

        TitleStatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);

        // Build status text with version when connected
        if (status == ConnectionStatus.Connected && LastGatewaySelf is { ServerVersion: { Length: > 0 } ver })
            TitleStatusText.Text = $"v{ver}";
        else
            TitleStatusText.Text = text;

        // Update role indicator dots
        var snapshot = CurrentApp.ConnectionManager?.CurrentSnapshot;
        if (snapshot != null)
        {
            TitleOpDot.Fill = RoleDotBrush(snapshot.OperatorState);
            TitleNodeDot.Fill = RoleDotBrush(snapshot.NodeState);
        }
        else
        {
            var gray = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);
            TitleOpDot.Fill = gray;
            TitleNodeDot.Fill = gray;
        }
    }

    private static Microsoft.UI.Xaml.Media.SolidColorBrush RoleDotBrush(OpenClaw.Connection.RoleConnectionState state) => state switch
    {
        OpenClaw.Connection.RoleConnectionState.Connected =>
            new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LimeGreen),
        OpenClaw.Connection.RoleConnectionState.Connecting =>
            new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange),
        OpenClaw.Connection.RoleConnectionState.PairingRequired =>
            new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange),
        OpenClaw.Connection.RoleConnectionState.Error or
        OpenClaw.Connection.RoleConnectionState.PairingRejected or
        OpenClaw.Connection.RoleConnectionState.RateLimited =>
            new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red),
        _ => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
    };

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
        if (IsClosed || AppModel is null)
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
                GatewayNavVisibilityDebouncePolicy.ShouldKeepCurrentPageVisibleDuringDisconnect(currentTag);

            NavChat.Visibility = vis;
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

                var gatewayTags = new HashSet<string> { "chat", "sessions", "skills", "channels", "instances", "agentevents", "bindings", "config", "usage", "cron", "workspace" };
                if (currentTag != null && (gatewayTags.Contains(currentTag) || currentTag.StartsWith("agent:")))
                {
                    foreach (NavigationViewItem item in NavView.MenuItems.OfType<NavigationViewItem>())
                    {
                        if (item.Tag as string == "connection")
                        {
                            NavView.SelectedItem = item;
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Services.Logger.Warn($"[HubWindow] UpdateGatewayNavVisibility failed: {ex.Message}");
            throw;
        }
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

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // Skip when the selection was set programmatically by
        // OnContentFrameNavigated reflecting a Back/Forward — the page
        // is already showing and re-running NavigateInternal would push
        // a duplicate back-stack entry.
        if (_syncingNavSelection) return;

        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            NavigateInternal(NormalizeNavTag(tag));
        }
    }

    /// <summary>
    /// Authoritative post-navigation hook. Runs for every successful
    /// Frame.Navigate, so it's the single place that rebuilds
    /// <see cref="_currentNavTag"/> / <see cref="_currentAgentId"/> /
    /// <see cref="LastNavigationOrigin"/> and re-runs
    /// <see cref="InitializeCurrentPage"/> for the page that's now visible.
    /// </summary>
    private void OnContentFrameNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        var tag = e.Parameter as string;
        _currentNavTag = tag;
        LastNavigationOrigin = _pendingNavigationOrigin;
        _pendingNavigationOrigin = null;

        // Keep _currentAgentId aligned with the page that's now visible.
        if (tag != null && tag.StartsWith("agent:"))
        {
            var newAgent = ParseAgentIdFromTag(tag);
            if (newAgent != _currentAgentId)
            {
                _currentAgentId = newAgent;
                _cachedCommands = null;
            }
        }

        InitializeCurrentPage();
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
        try { settings.Save(); } catch { /* swallow — don't block UI */ }
    }

    private void InitializeCurrentPage()
    {
        switch (ContentFrame.Content)
        {
            case ChatPage chat: chat.Initialize(); break;
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
                    OpenClawTray.Services.Logger.Error($"[HubWindow] ConfigPage seed failed: {ex}");
                }
                break;
            case InstancesPage instances: instances.Initialize(); break;
            case PermissionsPage permissions: permissions.Initialize(); break;
            case SandboxPage sandbox: sandbox.Initialize(); break;
            case VoiceSettingsPage voice: voice.Initialize(CurrentApp.VoiceService); break;
            case AgentEventsPage agentEvents:
                agentEvents.Initialize(this);
                agentEvents.ClearCentralCache = () => AppModel?.ClearAgentEvents();
                agentEvents.PopulateAgentFilter(this);
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
            case DebugPage debug: debug.Initialize(); break;
            case AboutPage about: about.Initialize(); break;
        }
    }

    public void SetActivityFilter(string? filter)
    {
        // ActivityPage has been removed; the method is kept as a no-op so any
        // remaining external callers (e.g. legacy deep links) don't NRE.
        _ = filter;
    }

    private static Type? TagToPageType(string? tag) => tag switch
    {
        "chat" => typeof(ChatPage),
        "connection" => typeof(ConnectionPage),
        "channels" => typeof(ChannelsPage),
        "nodes" => typeof(InstancesPage),
        "instances" => typeof(InstancesPage),
        "config" => typeof(ConfigPage),
        "usage" => typeof(UsagePage),
        "bindings" => typeof(BindingsPage),
        "capabilities" => typeof(PermissionsPage),
        "voice" => typeof(VoiceSettingsPage),
        "permissions" => typeof(PermissionsPage),
        "sandbox" => typeof(SandboxPage),
        // ActivityPage has been removed; legacy "activity"/"history" deep links
        // redirect to ChannelsPage via DeepLinkHandler.
        "activity" => typeof(ChannelsPage),
        "settings" => typeof(SettingsPage),
        "debug" => typeof(DebugPage),
        "info" => typeof(AboutPage),
        // Legacy tags
        "home" => typeof(ConnectionPage),
        "general" => typeof(ConnectionPage),
        "conversations" => typeof(SessionsPage), // legacy redirect
        "sessions" => typeof(SessionsPage),
        "agentevents" => typeof(AgentEventsPage),
        "skills" => typeof(SkillsPage),
        "cron" => typeof(CronPage),
        "workspace" => typeof(WorkspacePage),
        "about" => typeof(AboutPage),
        // Agent-scoped pages
        _ when tag?.StartsWith("agent:") == true => ResolveAgentPageType(tag),
        _ => null
    };

    private static Type? ResolveAgentPageType(string tag)
    {
        var parts = tag.Split(':');
        // "agent:main" (2 parts) → workspace page for that agent
        if (parts.Length == 2) return typeof(WorkspacePage);
        // "agent:main:workspace" etc (3 parts)
        return parts[2] switch
        {
            "sessions" => typeof(SessionsPage),
            "agentevents" => typeof(AgentEventsPage),
            "skills" => typeof(SkillsPage),
            "cron" => typeof(CronPage),
            "workspace" => typeof(WorkspacePage),
            _ => null
        };
    }

    private static string ParseAgentIdFromTag(string? tag)
    {
        if (tag == null || !tag.StartsWith("agent:")) return "main";
        var parts = tag.Split(':');
        return parts.Length >= 2 ? parts[1] : "main";
    }

    // ── Command Search (Ctrl+E / Ctrl+K / Ctrl+F) — title bar AutoSuggestBox ──

    private void OnRootPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            global::Windows.System.VirtualKey.Control).HasFlag(
            global::Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl && (e.Key == global::Windows.System.VirtualKey.E ||
                     e.Key == global::Windows.System.VirtualKey.K ||
                     e.Key == global::Windows.System.VirtualKey.F))
        {
            e.Handled = true;
            TitleSearchBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            TitleSearchBox.Text = "";
        }
    }

    private List<CommandItem>? _cachedCommands;

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _cachedCommands ??= BuildCommandList();
        var query = sender.Text?.Trim() ?? "";
        var filtered = string.IsNullOrEmpty(query)
            ? _cachedCommands.Take(8).ToList()
            : _cachedCommands.Where(c => c.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (c.Subtitle?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                .Take(10).ToList();
        sender.ItemsSource = filtered;
    }

    private void OnSearchSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is CommandItem cmd)
        {
            sender.Text = "";
            sender.ItemsSource = null;
            _cachedCommands = null;
            ExecuteCommand(cmd);
        }
    }

    private void OnSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is CommandItem cmd)
        {
            sender.Text = "";
            sender.ItemsSource = null;
            _cachedCommands = null;
            ExecuteCommand(cmd);
        }
        else if (sender.ItemsSource is List<CommandItem> items && items.Count > 0)
        {
            // Enter pressed without selecting — execute first match
            var first = items[0];
            sender.Text = "";
            sender.ItemsSource = null;
            _cachedCommands = null;
            ExecuteCommand(first);
        }
    }

    internal List<CommandItem> BuildCommandList()
    {
        var agentId = _currentAgentId;
        var commands = new List<CommandItem>
        {
            // Navigation
            new() { Icon = "🔌", Title = "Go to Connection", Subtitle = "Gateway, node, saved gateways", Tag = "connection" },
            new() { Icon = "💬", Title = "Go to Chat", Subtitle = "Open chat", Tag = "chat" },
            new() { Icon = "🧠", Title = "Go to Sessions", Subtitle = "All sessions", Tag = "sessions" },
            new() { Icon = "🧠", Title = "Go to Agent Events", Subtitle = "Agent event log", Tag = "agentevents" },
            new() { Icon = "🧠", Title = "Go to Skills", Subtitle = "Registered skills", Tag = "skills" },
            new() { Icon = "🧠", Title = $"Go to Cron ({agentId})", Subtitle = "Scheduled tasks", Tag = $"agent:{agentId}:cron" },
            new() { Icon = "🧠", Title = $"Go to Workspace ({agentId})", Subtitle = "Workspace files", Tag = $"agent:{agentId}" },
            new() { Icon = "📡", Title = "Go to Channels", Subtitle = "Gateway channels", Tag = "channels" },
            new() { Icon = "📡", Title = "Go to Instances", Subtitle = "Gateway instances", Tag = "instances" },
            new() { Icon = "📡", Title = "Go to Config", Subtitle = "Gateway configuration", Tag = "config" },
            new() { Icon = "📡", Title = "Go to Usage", Subtitle = "Usage statistics", Tag = "usage" },
            new() { Icon = "📡", Title = "Go to Bindings", Subtitle = "Gateway bindings", Tag = "bindings" },
            new() { Icon = "🛡️", Title = "Go to Permissions", Subtitle = "Capabilities, exec policy & allowlists", Tag = "permissions" },
            new() { Icon = "⚙️", Title = "Go to Settings", Subtitle = "Application settings", Tag = "settings" },
            new() { Icon = "🐛", Title = "Go to Diagnostics", Subtitle = "Logs, support bundle, device identity, developer tools", Tag = "debug" },
            new() { Icon = "ℹ️", Title = "Go to Info", Subtitle = "About this app", Tag = "info" },

            // Actions
            new() { Icon = "💬", Title = "Open Chat Window", Subtitle = "Open standalone chat", Tag = "chat" },
            new() { Icon = "🌐", Title = "Open Dashboard", Subtitle = "Open web dashboard", Execute = () => ((IAppCommands)Application.Current).OpenDashboard(null) },
        };

        // Toggle commands
        var settings = CurrentApp.Settings;
        if (settings != null)
        {
            commands.Add(new CommandItem
            {
                Icon = "🔌", Title = "Toggle Node Mode",
                Subtitle = settings.EnableNodeMode ? "Currently ON" : "Currently OFF",
                Execute = () => { settings.EnableNodeMode = !settings.EnableNodeMode; settings.Save(); RaiseSettingsSaved(); }
            });
            commands.Add(new CommandItem
            {
                Icon = "📷", Title = "Toggle Camera",
                Subtitle = settings.NodeCameraEnabled ? "Currently ON" : "Currently OFF",
                Execute = () => { settings.NodeCameraEnabled = !settings.NodeCameraEnabled; settings.Save(); RaiseSettingsSaved(); }
            });
            commands.Add(new CommandItem
            {
                Icon = "🎨", Title = "Toggle Canvas",
                Subtitle = settings.NodeCanvasEnabled ? "Currently ON" : "Currently OFF",
                Execute = () => { settings.NodeCanvasEnabled = !settings.NodeCanvasEnabled; settings.Save(); RaiseSettingsSaved(); }
            });
            commands.Add(new CommandItem
            {
                Icon = "🖥️", Title = "Toggle Screen Capture",
                Subtitle = settings.NodeScreenEnabled ? "Currently ON" : "Currently OFF",
                Execute = () => { settings.NodeScreenEnabled = !settings.NodeScreenEnabled; settings.Save(); RaiseSettingsSaved(); }
            });
            commands.Add(new CommandItem
            {
                Icon = "🌐", Title = "Toggle Browser Control",
                Subtitle = settings.NodeBrowserProxyEnabled ? "Currently ON" : "Currently OFF",
                Execute = () => { settings.NodeBrowserProxyEnabled = !settings.NodeBrowserProxyEnabled; settings.Save(); RaiseSettingsSaved(); }
            });
        }

        // Dynamic session commands
        var sessions = AppModel?.Sessions;
        if (sessions != null)
        {
            foreach (var session in sessions)
            {
                var key = session.Key;
                commands.Add(new CommandItem
                {
                    Icon = "🧠", Title = $"Go to session: {key}",
                    Subtitle = "Open in dashboard",
                    Execute = () => ((IAppCommands)Application.Current).OpenDashboard($"sessions/{key}")
                });
            }
        }

        return commands;
    }

    private void ExecuteCommand(CommandItem cmd)
    {
        if (cmd.Execute != null)
        {
            cmd.Execute();
            return;
        }

        if (!string.IsNullOrEmpty(cmd.Tag))
        {
            NavigateTo(cmd.Tag);
        }
    }

    #region High Contrast icon fallback

    // Maps NavigationViewItem.Tag -> Segoe Fluent Icons glyph used as fallback
    // when Windows High Contrast is active. FontIcon uses the system foreground
    // brush so it auto-adapts to every HC variant (HC Black/White/#1/#2); our
    // multi-color SVGs don't, so we swap them out at construction. This mirrors
    // the original gray Segoe Fluent Icons that were here before the colorful
    // refresh — same glyphs as those Windows users learned in earlier builds.
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
        { "debug",       "\uEBE8" },
        { "info",        "\uE946" },
    };

    // Glyphs for the two parent NavigationViewItems that don't carry a Tag
    // ("Advanced" group and "Agents" group). These also feed the dynamic agent
    // items added at runtime.
    private const string AdvancedGroupGlyph = "\uE950";
    private const string AgentsGroupGlyph = "\uE99A";

    private bool _isHighContrast;

    private void ApplyHighContrastFallbackIfNeeded()
    {
        try
        {
            var settings = new global::Windows.UI.ViewManagement.AccessibilitySettings();
            _isHighContrast = settings.HighContrast;
        }
        catch
        {
            _isHighContrast = false;
            return;
        }
        if (!_isHighContrast) return;
        SwapToFontIcons(NavView.MenuItems);
        SwapToFontIcons(NavView.FooterMenuItems);
    }

    private void SwapToFontIcons(IList<object> items)
    {
        foreach (var obj in items)
        {
            if (obj is not NavigationViewItem item) continue;
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
                return new FontIcon { Glyph = glyph };
            if (tag.StartsWith("agent:", StringComparison.Ordinal))
                return new FontIcon { Glyph = AgentsGroupGlyph };
        }
        if (item == AgentsNavItem)
            return new FontIcon { Glyph = AgentsGroupGlyph };
        if (item.Content is string content && content.Equals("Advanced", StringComparison.OrdinalIgnoreCase))
            return new FontIcon { Glyph = AdvancedGroupGlyph };
        // Fall back to whatever the XAML provided (keeps the colorful icon
        // rather than blanking it out for unmapped items).
        return item.Icon ?? new FontIcon { Glyph = "\uE700" };
    }

    private IconElement BuildAgentItemIcon()
    {
        if (_isHighContrast)
            return new FontIcon { Glyph = AgentsGroupGlyph };
        return new ImageIcon
        {
            Source = (Microsoft.UI.Xaml.Media.ImageSource)NavView.Resources["Agents_Icon"]
        };
    }

    #endregion
}
