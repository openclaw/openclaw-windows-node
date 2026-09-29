using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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
    private readonly WorkspaceNavigationHistory _navigation = new();
    private readonly ChatPage _chat = new();
    private readonly GatewayStatusContent _gatewayStatusContent = new();
    private readonly Flyout _gatewayStatusFlyout = new() { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft };
    private readonly MenuFlyoutItem _connectionStatusItem = new();
    private readonly FontIcon _connectionStatusIcon = new();
    private bool _updating;
    private bool _creatingSession;
    private bool _showingAgentCreation;
    private IOperatorGatewayClient? _refreshingClient;
    private string? _agentId;

    public bool IsClosed { get; private set; }
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

    internal async Task StartAgentChatAsync(WorkspaceAgent agent)
    {
        _agentId = agent.Id;
        RefreshSidebar();
        if (agent.LatestSessionKey is { } sessionKey)
            SelectSession(sessionKey);
        else
            await NewSessionAsync();
    }

    private void RenderDestination()
    {
        if (Destination.Page == WorkspacePageId.Home && Destination.SessionKey is { } sessionKey)
        {
            var session = _state.Sessions.FirstOrDefault(session => session.Key == sessionKey)
                ?? new SessionInfo { Key = sessionKey };
            _agentId = SessionDisplayResolver.Resolve(session).AgentId;
            RefreshSidebar();
            // Queue before initialization so history restores the existing chat host's session.
            _chat.QueueSession(sessionKey);
        }
        if (Destination.Page == WorkspacePageId.Home && ContentHost.Children.Contains(_chat))
        {
            _chat.Initialize(this);
            UpdateNavigationSelection();
            BackButton.IsEnabled = _navigation.CanGoBack;
            ForwardButton.IsEnabled = _navigation.CanGoForward;
            SignalContentReady(_chat);
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
        for (var index = 0; index < desiredItems.Count; index++)
        {
            var item = desiredItems[index];
            if (index < AssistantSelector.Items.Count && ReferenceEquals(AssistantSelector.Items[index], item))
                continue;
            AssistantSelector.Items.Remove(item);
            AssistantSelector.Items.Insert(index, item);
        }
        while (AssistantSelector.Items.Count > desiredItems.Count)
            AssistantSelector.Items.RemoveAt(AssistantSelector.Items.Count - 1);
        _agentId = WorkspaceProjection.SelectedAgentId(_state.AgentsList, agents, _agentId);
        RestoreAssistantSelection();
        AssistantSelector.PlaceholderText = Text(agents.Count == 0 ? "NoAgents" : "SelectAssistant");
        var sessions = WorkspaceProjection.Sessions(_state.Sessions, _agentId);
        foreach (var item in NavView.MenuItems.OfType<NavigationViewItem>()
            .Where(item => item.Tag is WorkspaceSession).ToArray())
            NavView.MenuItems.Remove(item);
        foreach (var session in sessions)
        {
            var item = new NavigationViewItem
            {
                Tag = session,
                Content = new TextBlock { Text = session.Title, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis }
            };
            AutomationProperties.SetName(item, session.Title);
            AutomationProperties.SetAutomationId(item, $"WorkspaceSession:{session.Key}");
            ToolTipService.SetToolTip(item, session.Title);
            NavView.MenuItems.Add(item);
        }
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

        _creatingSession = true;
        RefreshSidebar();
        try
        {
            var result = await client.CreateSessionAsync(new SessionCreateRequest { AgentId = _agentId });
            if (IsClosed || !ReferenceEquals(client, CurrentApp.GatewayClient) || !client.IsConnectedToGateway)
                return;
            if (!result.IsSupported || !result.Ok || string.IsNullOrWhiteSpace(result.Key))
            {
                ShowError(result.Error ?? Text(result.IsSupported ? "SessionFailed" : "SessionUnsupported"));
                return;
            }
            SelectSession(result.Key);
            await client.RequestSessionsAsync();
        }
        catch (Exception ex)
        {
            if (!IsClosed && ReferenceEquals(client, CurrentApp.GatewayClient))
                ReportError("create-session", ex);
        }
        finally
        {
            _creatingSession = false;
            if (!IsClosed) RefreshSidebar();
        }
    }

    internal void ShowError(string message)
    {
        Logger.Warn($"[Workspace] {message}");
        if (IsClosed) return;
        OperationInfo.Message = message;
        OperationInfo.IsOpen = true;
    }

    private void ReportError(string operation, Exception ex)
    {
        Logger.Error($"[Workspace] {operation} failed: {ex}");
        ShowError(ex.Message);
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
