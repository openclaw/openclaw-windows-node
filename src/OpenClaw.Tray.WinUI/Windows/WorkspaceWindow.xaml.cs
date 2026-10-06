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
    private readonly Action<string> _openCompanion;
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
        AppState state, AppNotificationService notifications, Action<string> openCompanion,
        Action openTimeline)
    {
        InitializeComponent();
        _state = state;
        _notifications = notifications;
        _openCompanion = openCompanion;
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
        _chat.RetainNativeSetupForDestination(destination);
        if (!_navigation.Navigate(destination) && ContentHost.Children.FirstOrDefault() is FrameworkElement current)
        {
            SignalContentReady(current);
            return;
        }
        if (Root.XamlRoot is not null)
            RenderDestination();
    }

    internal void OpenCompanion(CompanionPageId page, string? agentId = null) =>
        _openCompanion(WorkspaceNavigation.CompanionTag(page, agentId ?? _agentId ?? "main"));

    internal void OpenTimeline() => _openTimeline();
    internal void OpenCommandCenter() => _openCompanion("command-center");
    internal void SelectSession(string sessionKey) =>
        Navigate(new(WorkspacePageId.Home, sessionKey));

    internal void NavigateNativeSetup(SetupNativeNavigationRequest request)
    {
        var destination = request.WorkspaceDestination
            ?? throw new InvalidOperationException("The setup destination is not Workspace chat.");
        _chat.BindNativeSetupRequest(request);
        Navigate(destination, preserveConversation: false);
        // An already selected session does not render again; apply the new verified binding too.
        if (Root.XamlRoot is not null)
            _chat.Initialize(this);
    }

    internal async Task WaitForNativeSetupAsync(SetupNativeNavigationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
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
        _chat.RetainNativeSetupForDestination(Destination);
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
            BackButton.IsEnabled = _navigation.CanGoBack;
            ForwardButton.IsEnabled = _navigation.CanGoForward;
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
        BackButton.IsEnabled = _navigation.CanGoBack;
        ForwardButton.IsEnabled = _navigation.CanGoForward;
        if (ContentHost.Children.FirstOrDefault() is FrameworkElement content)
            SignalContentReady(content);
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
        BackButton.IsEnabled = _navigation.CanGoBack;
        ForwardButton.IsEnabled = _navigation.CanGoForward;
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
        if (_navigation.GoBack()) RenderDestination();
    }
    private void NavigateForward()
    {
        if (_navigation.GoForward()) RenderDestination();
    }
    private void OnKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Control)
            .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Menu)
            .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl && e.Key == global::Windows.System.VirtualKey.K)
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
            ("Support", "https://docs.openclaw.ai/help"),
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
        _chat.CloseSurface();
    }
}
