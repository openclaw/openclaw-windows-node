using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Controls;
using OpenClawTray.Dialogs;
using OpenClawTray.Helpers;
using OpenClawTray.Pages;
using OpenClawTray.Presentation;
using OpenClawTray.Services;
using WinUIEx;

namespace OpenClawTray.Windows;

public sealed partial class WorkspaceWindow : WindowEx
{
    private static App CurrentApp => (App)Application.Current;
    private readonly AppState _state;
    private readonly Action _openTimeline;
    private readonly AppNotificationService _notifications;
    private readonly WorkspaceIdentitySource _identity;
    private readonly WorkspaceSessionMenuController _sessionMenu;
    private readonly WorkspaceNavigationHistory _navigation = new();
    private readonly WorkspaceSessionOrder _sessionOrder = new();
    private readonly ChatPage _chat = new();
    private readonly GatewayStatusContent _gatewayStatusContent = new();
    private readonly Flyout _gatewayStatusFlyout = new() { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft };
    private readonly MenuFlyoutItem _connectionStatusItem = new();
    private readonly FontIcon _connectionStatusIcon = new();
    // Sidebar rows keyed by session key; SyncSessionItems keeps MenuItems in step with these.
    private readonly Dictionary<string, NavigationViewItem> _sessionItems = new(StringComparer.Ordinal);
    private bool _updating;
    private bool _creatingSession;
    // Session switches commit sidebar/navigation state synchronously; the chat surface is
    // applied on one later low-priority dispatcher turn so the click paints first, and rapid
    // clicks coalesce onto the latest ChatPage.QueueSession value.
    private bool _chatApplyQueued;
    private bool _showingAgentCreation;
    private IOperatorGatewayClient? _refreshingClient;
    private string? _agentId;
    // Settings lives in this window as a retained sibling of the chat section. Chat is only
    // collapsed while Settings is visible, so its control identity, draft and focus survive.
    private SettingsShellView? _settings;
    private Microsoft.UI.Xaml.Controls.Control? _chatFocus;
    private readonly PendingRequestSlot<SetupNativeNavigationRequest> _pendingSettingsRequest = new();
    private readonly TaskCompletionSource _settingsRendered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Raised when a Settings page saved a user setting.</summary>
    internal event EventHandler? SettingsSaved;

    public bool IsClosed { get; private set; }
    internal bool IsChatVisible => !IsClosed && ChatVisibilityPolicy.IsWorkspaceChatVisible(
        IsClosed,
        AppWindow.IsVisible,
        AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
            { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized },
        Destination.Page);
    internal string? SelectedAgentId => _agentId;
    internal WorkspaceDestination Destination => _navigation.Current;
    internal ChatPage ChatPage => _chat;
    internal bool CanGoBack => _navigation.CanGoBack;

    internal WorkspaceWindow(
        AppState state, AppNotificationService notifications, Action openTimeline)
    {
        InitializeComponent();
        _state = state;
        _notifications = notifications;
        _openTimeline = openTimeline;
        _identity = new WorkspaceIdentitySource(UpdateOwnerIdentity,
            category => Logger.Warn($"[Workspace] users.self unavailable ({category}); using owner fallback."));
        _sessionMenu = new WorkspaceSessionMenuController(
            () => IsClosed ? null : CurrentApp.GatewayClient,
            key => _state.Sessions.FirstOrDefault(s => s.Key == key),
            () => _state.Sessions,
            () => Root.XamlRoot,
            () => WinRT.Interop.WindowNative.GetWindowHandle(this),
            ForkSessionAsync,
            LeaveSession,
            ShowInfo,
            ReportError);
        _gatewayStatusFlyout.Content = _gatewayStatusContent;
        _gatewayStatusFlyout.Opening += (_, _) => _gatewayStatusContent.Initialize(
            () => _gatewayStatusFlyout.Hide(),
            () => OpenCompanion(CompanionPageId.Connection),
            () => ((IAppCommands)CurrentApp).Reconnect());
        Title = Text("Title");
        this.SetWindowSize(1280, 860);
        ExtendsContentIntoTitleBar = true;
        WorkspaceTitleBar.IconSource = new BitmapIconSource
        {
            UriSource = new Uri(BrandAssets.RedBotMarkUri),
            ShowAsMonochrome = false
        };
        this.SetIcon("Assets\\openclaw.ico");
        SetTitleBar(WorkspaceTitleBar);
        NewAgentLabel.Text = LocalizationHelper.GetString("AgentCreation_Title");
        AutomationProperties.SetName(NewAgentOption, NewAgentLabel.Text);
        // ComboBox temporarily removes its selected presentation while the popup is open.
        AssistantSelector.DropDownOpened += (_, _) => AssistantSelector.MinHeight = AssistantSelector.ActualHeight;
        AssistantSelector.DropDownClosed += (_, _) => AssistantSelector.ClearValue(FrameworkElement.MinHeightProperty);
        NavView.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, (_, _) => UpdatePanePresentation());
        UpdatePanePresentation();
        HomeLabel.Text = Text("Home");
        AutomationProperties.SetName(HomeItem, Text("Home"));
        UpdateOwnerIdentity();
        BuildOwnerMenu();
        _state.PropertyChanged += OnStateChanged;
        _notifications.Changed += OnNotificationsChanged;
        UpdateNotificationsBadge();
        _chat.Loaded += (_, _) => _chat.Initialize(this);
        _chat.SessionSelected += OnChatSessionSelected;
        Closed += OnClosed;
        Root.Loaded += OnLoaded;
        RefreshSidebar();
    }

    internal static string Text(string key) => LocalizationHelper.GetString($"WorkspaceShell_{key}");

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RenderDestination();
        _ = RefreshAsync();
    }

    internal void Navigate(WorkspaceDestination destination, bool preserveConversation = true)
    {
        if (preserveConversation && destination is { Page: WorkspacePageId.Home, SessionKey: null })
            destination = _navigation.ChatDestination;
        if (destination != Destination && DeferWhileModalOpen(() => Navigate(destination, preserveConversation)))
            return;
        _chat.RetainNativeSetupForDestination(destination);
        if (!_navigation.Navigate(destination))
        {
            // Re-rendering Settings is idempotent and also re-arms a deactivated section.
            if (destination.Page == WorkspacePageId.Settings)
            {
                if (Root.XamlRoot is not null) RenderDestination();
            }
            else if (ContentHost.Children.FirstOrDefault() is FrameworkElement current)
                SignalContentReady(current);
            return;
        }
        if (Root.XamlRoot is not null)
            RenderDestination();
    }

    internal void OpenCompanion(CompanionPageId page, string? agentId = null) =>
        OpenSettings(WorkspaceNavigation.CompanionTag(page, agentId ?? _agentId ?? "main"));

    /// <summary>
    /// Shows a Settings section in this window. The section becomes one history entry; the visible
    /// chat's agent selection is never changed by Settings agent routes.
    /// </summary>
    internal void OpenSettings(string tag)
    {
        if (IsClosed) return;
        if (tag == "command-center")
        {
            OpenCommandCenter();
            return;
        }
        Navigate(WorkspaceDestination.Settings(
            WorkspaceNavigation.NormalizeSettingsTag(tag, _settings?.CurrentAgentId ?? _agentId ?? "main")));
    }

    /// <summary>The Settings view while it exists; null before Settings was first opened.</summary>
    internal SettingsShellView? SettingsView => _settings;
    internal bool IsSettingsVisible => !IsClosed && Destination.Page == WorkspacePageId.Settings;

    internal void OpenTimeline() => _openTimeline();

    /// <summary>
    /// Opens the visible surface's search: Settings search while Settings is shown, otherwise the
    /// Settings search after pushing the default section (the one search surface of this window).
    /// </summary>
    internal void OpenCommandCenter()
    {
        if (IsClosed) return;
        if (!IsSettingsVisible)
            Navigate(WorkspaceDestination.Settings("settings"));
        if (_settings is null) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!IsClosed && IsSettingsVisible && _settings.IsLoaded)
                _settings.OpenCommandCenter();
        });
    }

    internal void SelectSession(string sessionKey) =>
        Navigate(new(WorkspacePageId.Home, sessionKey));

    private void OnChatSessionSelected(string sessionKey, string? previousSessionKey)
    {
        if (IsClosed || !_navigation.SelectChatSession(sessionKey, previousSessionKey))
            return;

        var session = _state.Sessions.FirstOrDefault(session => session.Key == sessionKey)
            ?? new SessionInfo { Key = sessionKey };
        _agentId = SessionDisplayResolver.Resolve(session).AgentId;
        RefreshSidebar();
        UpdateNavigationSelection();
        UpdateHistoryButtons();
    }

    internal void NavigateNativeSetup(SetupNativeNavigationRequest request)
    {
        if (request.WorkspaceDestination is null)
        {
            var settingsDestination = WorkspaceDestination.Settings(NormalizeNativeSetupTag(request.PageTag));
            _pendingSettingsRequest.Set(request);
            Navigate(settingsDestination);
            // A destination deferred behind a modal dialog was not accepted: the one-shot request must
            // never bind later, after the dialog closes.
            if (Destination != settingsDestination)
                _pendingSettingsRequest.Clear();
            return;
        }
        _pendingSettingsRequest.Clear();
        var destination = request.WorkspaceDestination;
        _chat.BindNativeSetupRequest(request);
        Navigate(destination, preserveConversation: false);
        // An already selected session does not render again; apply the new verified binding too.
        if (Root.XamlRoot is not null)
            _chat.Initialize(this);
    }

    private string NormalizeNativeSetupTag(string tag) =>
        WorkspaceNavigation.NormalizeSettingsTag(tag, _settings?.CurrentAgentId ?? _agentId ?? "main");

    internal async Task WaitForNativeSetupAsync(SetupNativeNavigationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.WorkspaceDestination is null)
        {
            if (!IsSettingsVisible)
                throw new InvalidOperationException("The selected setup destination changed.");
            // A cold window renders Settings on Loaded; the request stays pending until then and
            // the wait ends promptly if another destination supersedes it first.
            if (_settings is null)
                await _pendingSettingsRequest.WaitForRenderAsync(request, _settingsRendered.Task, ct);
            ct.ThrowIfCancellationRequested();
            if (_settings is null || IsClosed || !IsSettingsVisible)
                throw new InvalidOperationException("The native window closed.");
            await _settings.WaitForNativeSetupAsync(request, ct);
            ct.ThrowIfCancellationRequested();
            if (IsClosed || !IsSettingsVisible)
                throw new InvalidOperationException("The native window closed.");
            return;
        }
        request.RequireWorkspaceDestination(Destination);
        await _chat.WaitForNativeSetupAsync(request, ct);
        ct.ThrowIfCancellationRequested();
        request.RequireWorkspaceDestination(Destination);
        if (IsClosed)
            throw new InvalidOperationException("The native window closed.");
    }

    internal async Task StartAgentChatAsync(WorkspaceAgent agent)
    {
        _chat.InvalidateNativeSetupForNavigation();
        _agentId = agent.Id;
        RefreshSidebar();
        if (agent.LatestSessionKey is { } sessionKey)
            SelectSession(sessionKey);
        else
            await NewSessionAsync();
    }

    private void RenderDestination()
    {
        _chat.InvalidatePendingSessionNavigation();
        _chat.RetainNativeSetupForDestination(Destination);
        if (Destination.Page == WorkspacePageId.Settings)
        {
            RenderSettings();
            return;
        }
        _pendingSettingsRequest.Clear();
        LeaveSettings();
        if (Destination.Page == WorkspacePageId.Home && Destination.SessionKey is { } sessionKey)
        {
            var session = _state.Sessions.FirstOrDefault(session => session.Key == sessionKey)
                ?? new SessionInfo { Key = sessionKey };
            _agentId = SessionDisplayResolver.Resolve(session).AgentId;
            if (session.Unread)
                _ = _sessionMenu.AcknowledgeReadAsync(session);
            RefreshSidebar();
            // Queue before initialization so history restores the existing chat host's session.
            _chat.QueueSession(sessionKey);
        }
        if (Destination.Page == WorkspacePageId.Home && ContentHost.Children.Contains(_chat))
        {
            QueueChatApply();
            UpdateNavigationSelection();
            UpdateHistoryButtons();
            return;
        }
        ContentHost.Children.Clear();
        var destination = Destination;
        if (destination.Page == WorkspacePageId.Home)
        {
            ContentHost.Children.Add(_chat);
        }
        else
        {
            var notifications = new NotificationsPage();
            notifications.Initialize(_notifications);
            ContentHost.Children.Add(notifications);
        }

        UpdateNavigationSelection();
        UpdateHistoryButtons();
        if (ContentHost.Children.FirstOrDefault() is FrameworkElement content)
            SignalContentReady(content);
    }

    private void UpdateHistoryButtons()
    {
        var back = _navigation.CanGoBack;
        var forward = _navigation.CanGoForward;
        BackButton.IsEnabled = back;
        ForwardButton.IsEnabled = forward;
        SettingsBackButton.IsEnabled = back;
        SettingsForwardButton.IsEnabled = forward;
    }

    private SettingsShellView EnsureSettings()
    {
        if (_settings is { } existing)
            return existing;
        var view = new SettingsShellView
        {
            AppModel = _state,
            ConnectAction = () => ((IAppCommands)CurrentApp).Reconnect(),
            ReconnectAction = () => ((IAppCommands)CurrentApp).Reconnect(),
            NavigateRequested = tag => OpenSettings(tag),
            WorkspaceRouteRequested = OnSettingsWorkspaceRoute,
            PruneRequested = PruneSettingsHistory,
        };
        view.SettingsSaved += OnSettingsViewSaved;
        view.BindAppNotifications(_notifications);
        if (CurrentApp.Settings is { } settings)
            view.ApplyNavPaneState(settings);
        view.BindToAppState();
        SettingsSection.Children.Add(view);
        _settings = view;
        return view;
    }

    private void OnSettingsViewSaved(object? sender, EventArgs e) => SettingsSaved?.Invoke(this, e);

    private void RenderSettings()
    {
        var view = EnsureSettings();
        if (SettingsSection.Visibility != Visibility.Visible)
        {
            _chatFocus = Root.XamlRoot is { } root
                ? Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) as Microsoft.UI.Xaml.Controls.Control
                : null;
            NavigationSection.Visibility = Visibility.Collapsed;
            SettingsSection.Visibility = Visibility.Visible;
            SettingsHistoryBar.Visibility = Visibility.Visible;
        }
        UpdateHistoryButtons();
        var request = _pendingSettingsRequest.Consume();
        var tag = Destination.CompanionTag ?? "settings";
        view.ShowSection(tag, request is not null && NormalizeNativeSetupTag(request.PageTag) == tag ? request : null);
        _settingsRendered.TrySetResult();
        AccessibilityNavigationSignal.WritePageReady(view.CurrentPage?.GetType().Name ?? nameof(SettingsShellView));
    }

    /// <summary>
    /// Leaves Settings without recreating chat: deactivates every Settings page and returns focus
    /// to the control that held it before Settings opened.
    /// </summary>
    private void LeaveSettings()
    {
        if (SettingsSection.Visibility != Visibility.Visible)
            return;
        _settings?.Deactivate();
        SettingsSection.Visibility = Visibility.Collapsed;
        SettingsHistoryBar.Visibility = Visibility.Collapsed;
        NavigationSection.Visibility = Visibility.Visible;
        var focus = _chatFocus;
        _chatFocus = null;
        if (focus is not null)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!IsClosed && Destination.Page != WorkspacePageId.Settings && focus.IsLoaded)
                    focus.Focus(FocusState.Programmatic);
            });
    }

    private void OnSettingsWorkspaceRoute(string tag)
    {
        WorkspaceNavigation.Dispatch(tag, destination =>
        {
            if (destination.Page == WorkspacePageId.Home && destination.SessionKey is null &&
                CurrentApp.PendingChatSessionKey is { Length: > 0 } sessionKey)
                SelectSession(sessionKey);
            else
                Navigate(destination);
        }, OpenSettings);
    }

    /// <summary>
    /// Drops unavailable Settings sections from the single history. A pruned current section is
    /// replaced by <paramref name="fallbackTag"/> without a history entry and rendered.
    /// </summary>
    private void PruneSettingsHistory(Func<string, bool> shouldRemove, string? fallbackTag)
    {
        if (IsClosed) return;
        var changed = _navigation.PruneSettings(
            shouldRemove, fallbackTag is null ? _navigation.Current : WorkspaceDestination.Settings(fallbackTag));
        UpdateHistoryButtons();
        if (!changed || fallbackTag is null || Root.XamlRoot is null)
            return;
        // A pending deferred Back/Forward/Navigate runs after the pruned destination is rendered first.
        if (_deferred.HasIntent)
            _deferred.DeferRender();
        else if (!DeferWhileModalOpen(RenderPrunedDestination))
            RenderDestination();
    }

    private void RenderPrunedDestination()
    {
        if (!IsClosed && Root.XamlRoot is not null)
            RenderDestination();
    }

    // Back and Forward never land on a Gateway section while the Gateway is unavailable.
    private void PruneUnavailableGatewayHistory()
    {
        if (_state.Status != ConnectionStatus.Connected)
            PruneSettingsHistory(HubPageRegistry.IsGatewayPageTag, null);
    }

    private void QueueChatApply()
    {
        if (_chatApplyQueued) return;
        // TryEnqueue fails only while the dispatcher shuts down; the window is closing then.
        _chatApplyQueued = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ApplyQueuedChat);
    }

    private void ApplyQueuedChat()
    {
        _chatApplyQueued = false;
        if (IsClosed || Destination.Page != WorkspacePageId.Home || !ContentHost.Children.Contains(_chat))
            return;
        _chat.Initialize(this);
        SignalContentReady(_chat);
    }

    private void UpdateNavigationSelection()
    {
        _updating = true;
        var items = NavView.MenuItems.OfType<NavigationViewItem>();
        NavView.SelectedItem = Destination.Page == WorkspacePageId.Home
            ? items.FirstOrDefault(item => item.Tag is WorkspaceSession session && session.Key == Destination.SessionKey)
                ?? HomeItem
            : null;
        _updating = false;
    }

    private void SignalContentReady(FrameworkElement content)
    {
        var destination = Destination;
        void Signal() => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
        {
            if (Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST") == "1")
                await Task.Delay(400); // Let native entry transitions settle for capture.
            if (!IsClosed && Destination == destination && ContentHost.Children.Contains(content))
            {
                var paneSuffix = NavView.IsPaneOpen ? "" : "-Hidden";
                await VisualTestCapture.CaptureAsync(Root, $"Workspace-{destination.Page}-{Root.ActualTheme}{paneSuffix}");
                AccessibilityNavigationSignal.WritePageReady(content.GetType().Name);
            }
        });
        if (content.IsLoaded) Signal();
        else
        {
            RoutedEventHandler? loaded = null;
            loaded = (_, _) =>
            {
                content.Loaded -= loaded;
                Signal();
            };
            content.Loaded += loaded;
        }
    }

    private void RefreshSidebar()
    {
        _updating = true;
        var agents = WorkspaceProjection.Agents(_state.AgentsList, _state.Sessions);
        var previousItems = AssistantSelector.Items.OfType<ComboBoxItem>()
            .Where(item => item.Tag is WorkspaceAgent)
            .ToDictionary(item => ((WorkspaceAgent)item.Tag).Id, StringComparer.Ordinal);
        var desiredItems = new List<ComboBoxItem>();
        foreach (var agent in agents)
        {
            var item = previousItems.GetValueOrDefault(agent.Id) ?? new ComboBoxItem
            {
                ContentTemplate = (DataTemplate)Root.Resources["AgentIdentityTemplate"],
                Padding = new Thickness(2, 8, 12, 8),
                // Compensate the native item's leading template margin, not the popup's scroll extent.
                Margin = new Thickness(-5, 0, 0, 0)
            };
            item.Content = agent;
            item.Tag = agent;
            AutomationProperties.SetName(item, $"{agent.Name}, {agent.Id}");
            AutomationProperties.SetAutomationId(item, $"WorkspaceAgent:{agent.Id}");
            desiredItems.Add(item);
        }
        desiredItems.Add(NewAgentOption);
        // Keep the open popup and selected container alive across independent roster/session responses.
        WorkspaceItemsSync.Arrange(AssistantSelector.Items, 0, desiredItems.Cast<object>().ToList());
        while (AssistantSelector.Items.Count > desiredItems.Count)
            AssistantSelector.Items.RemoveAt(AssistantSelector.Items.Count - 1);
        _agentId = WorkspaceProjection.SelectedAgentId(_state.AgentsList, agents, _agentId);
        RestoreAssistantSelection();
        AssistantSelector.PlaceholderText = Text(agents.Count == 0 ? "NoAgents" : "SelectAssistant");
        var sessions = WorkspaceProjection.Sessions(_state.Sessions, _agentId, _sessionOrder);
        SyncSessionItems(_sessionItems, sessions, "WorkspaceSession", NavView.MenuItems.IndexOf(SessionsEmpty) + 1);
        SessionsEmpty.Content = Text("NoSessions");
        SessionsEmpty.Visibility = sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NewAgentOption.IsEnabled = !_showingAgentCreation;
        var selectionRequired = WorkspaceProjection.RequiresAgentSelection(_state.AgentsList, agents, _agentId);
        NewSessionButton.IsEnabled =
            !_creatingSession && _state.Status == ConnectionStatus.Connected && !selectionRequired;
        AutomationProperties.SetHelpText(NewSessionButton, selectionRequired ? Text("SelectAssistant") : string.Empty);
        ToolTipService.SetToolTip(NewSessionButton, selectionRequired ? Text("SelectAssistant") : null);
        _updating = false;
        // Before layout, NavigationView is still minimal and selecting an item closes its pane.
        if (Root.IsLoaded)
            UpdateNavigationSelection();
    }

    /// <summary>
    /// Reconciles one sidebar section in place. Rows are cached by session key so a reused
    /// row keeps its container; destroying the selected row inside SelectionChanged forced
    /// NavigationView to reselect and re-layout on every session switch.
    /// </summary>
    private void SyncSessionItems(
        Dictionary<string, NavigationViewItem> cache, IReadOnlyList<WorkspaceSession> sessions,
        string automationPrefix, int start)
    {
        var desired = new List<object>(sessions.Count);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            keys.Add(session.Key);
            if (cache.TryGetValue(session.Key, out var item))
            {
                if (!Equals(item.Tag, session))
                    ApplySessionItem(item, session);
            }
            else
            {
                item = CreateSessionItem(session, automationPrefix);
                cache[session.Key] = item;
            }
            desired.Add(item);
        }
        foreach (var (key, item) in cache.Where(entry => !keys.Contains(entry.Key)).ToArray())
        {
            NavView.MenuItems.Remove(item);
            cache.Remove(key);
        }
        WorkspaceItemsSync.Arrange(NavView.MenuItems, start, desired);
    }

    private NavigationViewItem CreateSessionItem(WorkspaceSession session, string automationPrefix)
    {
        var item = new NavigationViewItem();
        AutomationProperties.SetAutomationId(item, $"{automationPrefix}:{session.Key}");
        ApplySessionItem(item, session);
        return item;
    }

    internal static Grid BuildSessionContent(WorkspaceSession session)
    {
        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        if (session.IsPinned)
        {
            var pin = FluentIconCatalog.Build(FluentIconCatalog.Pin, 12);
            pin.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            pin.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetAccessibilityView(pin, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            Grid.SetColumn(pin, 0);
            grid.Children.Add(pin);
        }
        var title = new TextBlock
        {
            Text = session.Title,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        if (session.IsWorking)
        {
            var busy = new ProgressRing
            {
                IsActive = true,
                Width = 14,
                Height = 14,
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetAccessibilityView(busy, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            Grid.SetColumn(busy, 2);
            grid.Children.Add(busy);
        }
        else if (session.IsUnread)
        {
            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetAccessibilityView(dot, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            Grid.SetColumn(dot, 2);
            grid.Children.Add(dot);
        }
        return grid;
    }

    private void ApplySessionItem(NavigationViewItem item, WorkspaceSession session)
    {
        item.Tag = session;
        item.Content = BuildSessionContent(session);
        item.ContextFlyout = _sessionMenu.CreateFlyout(session);
        AutomationProperties.SetName(item, session.Title);
        ToolTipService.SetToolTip(item, session.Title);
        var statusParts = new[]
        {
            session.IsPinned ? Text("SessionPinned") : null,
            session.IsWorking ? Text("SessionWorking") : null,
            session.IsUnread ? Text("SessionUnread") : null,
        }.Where(part => part is not null).ToArray();
        // Always set: a reused row must clear a stale "Unread" or "Working" status.
        AutomationProperties.SetItemStatus(item, string.Join(", ", statusParts));
    }

    internal async Task RefreshAsync()
    {
        if (IsClosed || CurrentApp.GatewayClient is not { IsConnectedToGateway: true } client ||
            ReferenceEquals(client, _refreshingClient))
            return;
        _refreshingClient = client;
        try
        {
            await Task.WhenAll(client.RequestAgentsListAsync(), client.RequestSessionsAsync());
        }
        catch (Exception ex)
        {
            if (!IsClosed && ReferenceEquals(client, CurrentApp.GatewayClient))
                ReportError("refresh", ex);
        }
        finally
        {
            if (!IsClosed && ReferenceEquals(client, _refreshingClient))
            {
                _refreshingClient = null;
            }
        }
    }

    private async Task NewSessionAsync()
    {
        if (_creatingSession)
            return;
        if (CurrentApp.GatewayClient is not { IsConnectedToGateway: true } client)
        {
            ShowError(Text("ConnectionRequired"));
            OpenCompanion(CompanionPageId.Connection);
            return;
        }

        RefreshSidebar();
        if (WorkspaceProjection.RequiresAgentSelection(
            _state.AgentsList, WorkspaceProjection.Agents(_state.AgentsList, _state.Sessions), _agentId))
        {
            ShowError(Text("SelectAssistant"));
            AssistantSelector.Focus(FocusState.Programmatic);
            return;
        }

        await CreateAndSelectSessionAsync(
            client, new SessionCreateRequest { AgentId = _agentId },
            "create-session", "SessionFailed", "SessionUnsupported");
    }

    private Task ForkSessionAsync(SessionCreateRequest request)
    {
        if (_creatingSession)
            return Task.CompletedTask;
        if (CurrentApp.GatewayClient is not { IsConnectedToGateway: true } client)
        {
            ShowError(Text("ConnectionRequired"));
            return Task.CompletedTask;
        }
        return CreateAndSelectSessionAsync(client, request, "fork-session", "ForkFailed", "ForkUnsupported");
    }

    private async Task CreateAndSelectSessionAsync(
        IOperatorGatewayClient client, SessionCreateRequest request,
        string operation, string failedKey, string unsupportedKey)
    {
        _chat.InvalidateNativeSetupForNavigation();
        _creatingSession = true;
        RefreshSidebar();
        try
        {
            var result = await client.CreateSessionAsync(request);
            if (IsClosed || !ReferenceEquals(client, CurrentApp.GatewayClient) || !client.IsConnectedToGateway)
                return;
            if (!result.IsSupported || !result.Ok || string.IsNullOrWhiteSpace(result.Key))
            {
                ShowError(result.Error ?? Text(result.IsSupported ? failedKey : unsupportedKey));
                return;
            }
            SelectSession(result.Key);
            await client.RequestSessionsAsync();
        }
        catch (Exception ex)
        {
            if (!IsClosed && ReferenceEquals(client, CurrentApp.GatewayClient))
                ReportError(operation, ex);
        }
        finally
        {
            _creatingSession = false;
            if (!IsClosed) RefreshSidebar();
        }
    }

    internal void ShowInfo(string message, InfoBarSeverity severity)
    {
        Logger.Info($"[Workspace] {message}");
        if (IsClosed) return;
        OperationInfo.Severity = severity;
        OperationInfo.Message = message;
        OperationInfo.IsOpen = true;
    }

    internal void ShowError(string message)
    {
        Logger.Warn($"[Workspace] {message}");
        if (IsClosed) return;
        OperationInfo.Severity = InfoBarSeverity.Error;
        OperationInfo.Message = message;
        OperationInfo.IsOpen = true;
    }

    private void ReportError(string operation, Exception ex)
    {
        Logger.Error($"[Workspace] {operation} failed: {ex}");
        ShowError(ex.Message);
    }

    private void LeaveSession(string key)
    {
        var next = WorkspaceProjection.Sessions(_state.Sessions, _agentId, _sessionOrder)
            .FirstOrDefault(session => session.Key != key);
        var wasChatDestination = _navigation.ChatDestination.SessionKey == key;
        var wasCurrent = _navigation.RemoveSession(key, next?.Key);
        if (wasChatDestination)
            _chat.ClearRemovedSession(key);
        if (wasCurrent)
            RenderDestination();
        UpdateNavigationSelection();
        UpdateHistoryButtons();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.Status) or nameof(AppState.Presence) or nameof(AppState.SelfProfileRevision))
            RefreshOwnerIdentity(e.PropertyName == nameof(AppState.SelfProfileRevision));
        if (e.PropertyName is nameof(AppState.AgentsList) or nameof(AppState.Sessions) or nameof(AppState.Status))
            RefreshSidebar();
        if (e.PropertyName == nameof(AppState.Status) && _state.Status == ConnectionStatus.Connected)
            _ = RefreshAsync();
        if (e.PropertyName == nameof(AppState.Status))
            UpdateConnectionStatus(CurrentApp.ConnectionManager?.CurrentSnapshot, _state.Status);
    }

    internal void UpdateConnectionStatus(GatewayConnectionSnapshot? snapshot, ConnectionStatus status)
    {
        _settings?.UpdateStatus(snapshot, status);
        if (_identity.SetConnection(CurrentApp.GatewayClient, status))
            _ = _identity.RefreshAsync();
        var (labelKey, accent) = ConnectionStatusPresenter.Pill(snapshot?.OverallState, status);
        var label = LocalizationHelper.GetString(labelKey);
        OwnerDetail.Text = label;
        OwnerStatusDot.Style = (Style)Root.Resources[$"ConnectionBadge{accent}"];
        AutomationProperties.SetHelpText(OwnerButton, label);
        _connectionStatusItem.Text = label;
        _connectionStatusIcon.Style = (Style)Root.Resources[$"ConnectionBadge{accent}"];
        AutomationProperties.SetName(_connectionStatusItem,
            $"{LocalizationHelper.GetString("ConnectionStatusWindow.Title")}: {label}");
    }

    private void RefreshOwnerIdentity(bool invalidate = false)
    {
        _identity.SetConnection(CurrentApp.GatewayClient, _state.Status);
        _ = _identity.RefreshAsync(invalidate);
    }

    private void UpdateOwnerIdentity()
    {
        OwnerName.Text = _identity.DisplayName ?? Text("Owner.Text");
        OwnerPicture.DisplayName = _identity.DisplayName ?? "";
        AutomationProperties.SetName(OwnerButton,
            string.IsNullOrWhiteSpace(OwnerName.Text) ? Text("Owner.Text") : OwnerName.Text);
    }

    private void OnAssistantChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        if (ReferenceEquals(AssistantSelector.SelectedItem, NewAgentOption))
        {
            _updating = true;
            RestoreAssistantSelection();
            _updating = false;
            AssistantSelector.IsDropDownOpen = false;
            AsyncEventHandlerGuard.Run(NewAgentAsync, new AppLogger(), nameof(OnAssistantChanged));
            return;
        }
        if (AssistantSelector.SelectedItem is not ComboBoxItem { Tag: WorkspaceAgent agent }) return;
        AsyncEventHandlerGuard.Run(() => StartAgentChatAsync(agent), new AppLogger(), nameof(OnAssistantChanged));
    }

    private void RestoreAssistantSelection() =>
        AssistantSelector.SelectedItem = AssistantSelector.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is WorkspaceAgent agent && agent.Id == _agentId);

    private async Task NewAgentAsync()
    {
        if (_showingAgentCreation || Root.XamlRoot is null) return;
        _showingAgentCreation = true;
        NewAgentOption.IsEnabled = false;
        try
        {
            await new AgentCreationDialog(Root.XamlRoot,
                new AgentCreationService(() => IsClosed ? null : CurrentApp.GatewayClient)).ShowAsync();
            await RefreshAsync();
        }
        finally
        {
            _showingAgentCreation = false;
            if (!IsClosed) NewAgentOption.IsEnabled = true;
        }
    }

    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (_updating) return;
        if (e.SelectedItemContainer?.Tag is string route)
        {
            Navigate(new(WorkspaceNavigation.Routes[route]), preserveConversation: false);
            UpdateNavigationSelection();
        }
        else if (e.SelectedItemContainer?.Tag is WorkspaceSession session)
            SelectSession(session.Key);
    }

    private void OnNewConversation(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(NewSessionAsync, new AppLogger(), nameof(OnNewConversation));
    private void OnNotificationsOpening(object sender, object e) =>
        NotificationContent.Initialize(_notifications, () =>
        {
            NotificationsFlyout.Hide();
            Navigate(new(WorkspacePageId.Notifications));
        });

    private void OnNotificationsClosed(object sender, object e) => NotificationContent.Unbind();

    private void OnNotificationsChanged(object? sender, AppNotificationChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsClosed) UpdateNotificationsBadge();
        });

    private void UpdateNotificationsBadge()
    {
        var count = _notifications.Snapshot.ActiveNotifications.Count;
        NotificationsBadge.Value = count;
        NotificationsBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetHelpText(NotificationsButton, LocalizationHelper.Format("NotificationsFlyout_ActiveCountFormat", count));
    }
    private void OnBack(object sender, RoutedEventArgs e) => NavigateBack();
    private void OnForward(object sender, RoutedEventArgs e) => NavigateForward();
    private void OnTogglePane(object sender, RoutedEventArgs e)
    {
        var open = !NavView.IsPaneOpen;
        if (open)
        {
            NavView.IsPaneVisible = true;
        }
        NavView.IsPaneOpen = open;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsClosed)
                (NavView.IsPaneOpen ? CollapsePaneButton : ReopenPaneButton).Focus(FocusState.Programmatic);
        });
    }
    private void UpdatePanePresentation()
    {
        ReopenPaneButton.Visibility = NavView.IsPaneOpen ? Visibility.Collapsed : Visibility.Visible;
        ReopenPaneSlot.Visibility = ReopenPaneButton.Visibility;
        CollapsePaneButton.Visibility = NavView.IsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnPaneClosed(NavigationView sender, object args)
    {
        if (NavView.IsPaneOpen || IsClosed) return;
        NavView.IsPaneVisible = false;
    }
    internal void NavigateBack()
    {
        if (DeferWhileModalOpen(NavigateBack)) return;
        PruneUnavailableGatewayHistory();
        if (_navigation.GoBack()) RenderDestination();
        else UpdateHistoryButtons();
    }
    private void NavigateForward()
    {
        if (DeferWhileModalOpen(NavigateForward)) return;
        PruneUnavailableGatewayHistory();
        if (_navigation.GoForward()) RenderDestination();
        else UpdateHistoryButtons();
    }

    private readonly DeferredNavigation _deferred = new();

    private ContentDialog? OpenModalDialog() =>
        Root.XamlRoot is { } xamlRoot
            ? Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot)
                .Select(popup => popup.Child).OfType<ContentDialog>().FirstOrDefault()
            : null;

    /// <summary>
    /// Policy: while Settings is visible and a ContentDialog is open, destination changes wait for
    /// that dialog to close, then the latest deferred change runs once. Dialogs are never cancelled
    /// by navigation, so app-wide update, failure and approval dialogs keep their own outcome, and no
    /// dialog can be left over a page that did not open it.
    /// </summary>
    private bool DeferWhileModalOpen(Action navigate)
    {
        if (!IsSettingsVisible || OpenModalDialog() is not { } dialog)
            return false;
        if (!_deferred.Defer(navigate))
        {
            dialog.Closed += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (!IsClosed) _deferred.Run(() => OpenModalDialog() is not null, RenderPrunedDestination);
            });
        }
        return true;
    }
    private void OnKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Control)
            .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Menu)
            .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);
        // Ctrl+E is the Settings search shortcut shown in its placeholder; it is inert in chat.
        if (ctrl && (e.Key == global::Windows.System.VirtualKey.K ||
                     (IsSettingsVisible && e.Key == global::Windows.System.VirtualKey.E)))
        {
            e.Handled = true;
            OpenCommandCenter();
        }
        else if (alt && e.Key == global::Windows.System.VirtualKey.Left)
        {
            e.Handled = true;
            NavigateBack();
        }
        else if (alt && e.Key == global::Windows.System.VirtualKey.Right)
        {
            e.Handled = true;
            NavigateForward();
        }
    }

    private void BuildOwnerMenu()
    {
        var menu = new MenuFlyout();
        void Add(string label, Action action, string glyph)
        {
            var item = new MenuFlyoutItem { Text = Text(label), Icon = FluentIconCatalog.Build(glyph) };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("Settings", () => OpenCompanion(CompanionPageId.Settings), FluentIconCatalog.Settings);
        Add("Usage", () => OpenCompanion(CompanionPageId.Usage), FluentIconCatalog.Money);
        Add("PairDevice", () => OpenCompanion(CompanionPageId.Channels), FluentIconCatalog.Devices);
        var showConnectionStatus = false;
        _connectionStatusItem.Icon = _connectionStatusIcon;
        AutomationProperties.SetAutomationId(_connectionStatusItem, "WorkspaceOwnerConnectionStatus");
        _connectionStatusItem.Click += (_, _) => showConnectionStatus = true;
        menu.Opening += (_, _) => UpdateConnectionStatus(CurrentApp.ConnectionManager?.CurrentSnapshot, _state.Status);
        UpdateConnectionStatus(CurrentApp.ConnectionManager?.CurrentSnapshot, _state.Status);
        menu.Closed += (_, _) =>
        {
            if (!showConnectionStatus || IsClosed) return;
            showConnectionStatus = false;
            _gatewayStatusFlyout.ShowAt(OwnerButton);
        };
        menu.Items.Add(_connectionStatusItem);
        Add("GetApps", () => _ = OpenLinkAsync("https://docs.openclaw.ai/platforms"), FluentIconCatalog.OpenInBrowser);
        Add("ConnectionTimeline", OpenTimeline, FluentIconCatalog.AgentEvents);
        menu.Items.Add(new MenuFlyoutSeparator());
        var help = new MenuFlyoutSubItem { Text = Text("Help") };
        foreach (var (label, url) in new[]
        {
            ("Documentation", "https://docs.openclaw.ai"),
            ("Support", SupportLinks.OfficialSupportUrl),
            ("Community", "https://discord.gg/clawd"),
            ("ReleaseNotes", "https://docs.openclaw.ai/releases")
        })
        {
            var item = new MenuFlyoutItem { Text = Text(label) };
            item.Click += async (_, _) => await OpenLinkAsync(url);
            help.Items.Add(item);
        }
        var github = new MenuFlyoutItem
        {
            Text = LocalizationHelper.GetString("SettingsPage_AppInfoGitHub.Content")
        };
        github.Click += async (_, _) => await OpenLinkAsync("https://github.com/openclaw/openclaw-windows-node");
        help.Items.Add(github);
        menu.Items.Add(help);
        Add("About", () => OpenCompanion(CompanionPageId.About), FluentIconCatalog.About);
        OwnerButton.Flyout = menu;
    }

    internal async Task OpenLinkAsync(string url)
    {
        try
        {
            if (!await global::Windows.System.Launcher.LaunchUriAsync(new Uri(url)))
                ShowError(Text("LinkFailed"));
        }
        catch (Exception ex) { ReportError("open-link", ex); }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        IsClosed = true;
        _identity.SetConnection(null, ConnectionStatus.Disconnected);
        _state.PropertyChanged -= OnStateChanged;
        _notifications.Changed -= OnNotificationsChanged;
        NotificationsFlyout.Hide();
        NotificationContent.Unbind();
        _gatewayStatusFlyout.Hide();
        ContentHost.Children.Clear();
        if (_settings is { } settings)
        {
            settings.SettingsSaved -= OnSettingsViewSaved;
            settings.Dispose();
            SettingsSection.Children.Clear();
            _settings = null;
        }
        try { CurrentApp.PageActivator?.Reset(); }
        catch (Exception ex) { Logger.Warn($"[Workspace] Navigation scope reset on close failed: {ex.Message}"); }
        _pendingSettingsRequest.Clear();
        _settingsRendered.TrySetCanceled();
        _deferred.Clear();
        _chatFocus = null;
        _chat.CloseSurface();
        _chat.SessionSelected -= OnChatSessionSelected;
    }
}
