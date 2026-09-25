using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
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
    private readonly Func<Task> _openSetup;
    private readonly Action? _openTray;
    private readonly AppNotificationService _notifications;
    private readonly WorkspaceNavigationHistory _navigation = new();
    private readonly ChatPage _chat = new();
    private bool _updating;
    private bool _creatingSession;
    private IOperatorGatewayClient? _refreshingClient;
    private string? _agentId;
    private WorkspaceContentPage? _workspacePage;

    public bool IsClosed { get; private set; }
    internal WorkspaceDestination Destination => _navigation.Current;
    internal ChatPage ChatPage => _chat;
    internal string? SelectedAgentId => _agentId;
    internal AppState State => _state;
    internal bool IsRefreshing => _refreshingClient is not null;
    internal event Action? RefreshStateChanged;
    internal bool CanGoBack => _navigation.CanGoBack;

    internal WorkspaceWindow(
        AppState state, AppNotificationService notifications, Action<string> openCompanion,
        Action openTimeline, Func<Task> openSetup, Action? openTray)
    {
        InitializeComponent();
        _state = state;
        _notifications = notifications;
        _openCompanion = openCompanion;
        _openTimeline = openTimeline;
        _openSetup = openSetup;
        _openTray = openTray;
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
        PagesHeader.Content = LocalizationHelper.GetString("WorkspaceShell_Pages.Text");
        NewConversationLabel.Text = AutomationProperties.GetName(NewConversationOption);
        NavView.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, (_, _) => UpdatePanePresentation());
        UpdatePanePresentation();
        foreach (var item in NavView.MenuItems.OfType<NavigationViewItem>().Where(item => item.Tag is string))
            item.Content = Text(WorkspaceNavigation.Routes[(string)item.Tag].ToString());
        BuildOwnerMenu();
        _state.PropertyChanged += OnStateChanged;
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

    internal void Navigate(WorkspaceDestination destination)
    {
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

    internal void OpenAgentFiles(string agentId) => _openCompanion($"agent:{agentId}:workspace");
    internal void OpenTimeline() => _openTimeline();
    internal void OpenCommandCenter() => _openCompanion("command-center");
    internal void OpenTray()
    {
        if (_openTray is null)
            ShowError(Text("TrayUnavailable"));
        else
            _openTray();
    }

    internal async Task OpenSetupAsync()
    {
        try { await _openSetup(); }
        catch (Exception ex) { ReportError("setup", ex); }
    }

    internal void SelectSession(string sessionKey)
    {
        var session = _state.Sessions.FirstOrDefault(session => session.Key == sessionKey);
        if (session is not null)
        {
            _agentId = session.AgentId;
            RefreshSidebar();
        }
        _chat.QueueSession(sessionKey);
        Navigate(new(WorkspacePageId.Home));
        if (_chat.IsLoaded) _chat.SelectSession(sessionKey);
    }

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
        if (Destination.Page == WorkspacePageId.Home && ContentHost.Children.Contains(_chat))
        {
            _chat.Initialize(this);
            BackButton.IsEnabled = _navigation.CanGoBack;
            ForwardButton.IsEnabled = _navigation.CanGoForward;
            SignalContentReady(_chat);
            return;
        }
        _workspacePage?.Detach();
        _workspacePage = null;
        ContentHost.Children.Clear();
        var destination = Destination;
        if (destination.Page == WorkspacePageId.Home)
        {
            ContentHost.Children.Add(_chat);
        }
        else
        {
            var page = CreateExistingPage(destination.Page);
            if (page is not null)
                ContentHost.Children.Add(page);
            else
            {
                _workspacePage = new WorkspaceContentPage(this, destination);
                ContentHost.Children.Add(_workspacePage);
            }
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
        NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(item => item.Tag is string route && WorkspaceNavigation.Routes[route] == WorkspaceNavigation.Section(Destination.Page));
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
                var paneSuffix = NavView.IsPaneOpen ? "" : "-Compact";
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

    private Page? CreateExistingPage(WorkspacePageId pageId)
    {
        // These pages own their gateway workflows and subscriptions. They do not
        // participate in the companion's single navigation-scoped settings VM.
        switch (pageId)
        {
            case WorkspacePageId.Automations:
            case WorkspacePageId.AutomationDetail:
                var cron = new CronPage();
                cron.UseWorkspaceLayout(Destination.Page == WorkspacePageId.AutomationDetail, Destination.ItemId,
                    jobId => Navigate(new(WorkspacePageId.AutomationDetail, jobId)),
                    () => Navigate(new(WorkspacePageId.Automations)));
                cron.Initialize();
                return cron;
            case WorkspacePageId.Sessions:
                var sessions = new SessionsPage();
                sessions.HostWindow = this;
                sessions.Initialize();
                return sessions;
            case WorkspacePageId.Skills:
                var skills = new SkillsPage();
                skills.Initialize();
                return skills;
            case WorkspacePageId.Usage:
                var usage = new UsagePage();
                usage.Initialize();
                return usage;
            case WorkspacePageId.Notifications:
                var notifications = new NotificationsPage();
                notifications.Initialize(_notifications);
                return notifications;
            default:
                return null;
        }
    }

    private void RefreshSidebar()
    {
        _updating = true;
        var agents = WorkspaceProjection.Agents(_state.AgentsList, _state.Sessions);
        AssistantSelector.Items.Clear();
        foreach (var agent in agents)
            AssistantSelector.Items.Add(new ComboBoxItem { Content = agent.Name, Tag = agent });
        AssistantSelector.Items.Add(NewConversationOption);
        if (_agentId is not null && agents.Count > 0 && agents.All(agent => agent.Id != _agentId))
            _agentId = null;
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
                SelectsOnInvoked = false,
                Content = new TextBlock { Text = session.Title, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis }
            };
            AutomationProperties.SetName(item, session.Title);
            ToolTipService.SetToolTip(item, session.Title);
            NavView.MenuItems.Add(item);
        }
        SessionsEmpty.Content = Text("NoSessions");
        SessionsEmpty.Visibility = sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NewConversationOption.IsEnabled = NewSessionButton.IsEnabled =
            !_creatingSession && _state.Status == ConnectionStatus.Connected;
        _updating = false;
    }

    internal async Task RefreshAsync()
    {
        if (IsClosed || CurrentApp.GatewayClient is not { IsConnectedToGateway: true } client ||
            ReferenceEquals(client, _refreshingClient))
            return;
        _refreshingClient = client;
        RefreshStateChanged?.Invoke();
        try
        {
            await Task.WhenAll(client.RequestAgentsListAsync(), client.RequestSessionsAsync(), client.RequestNodesAsync());
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
                RefreshStateChanged?.Invoke();
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
        if (e.PropertyName is nameof(AppState.AgentsList) or nameof(AppState.Sessions) or nameof(AppState.Status))
            RefreshSidebar();
        if (e.PropertyName == nameof(AppState.Status) && _state.Status == ConnectionStatus.Connected)
            _ = RefreshAsync();
    }

    private void OnAssistantChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        if (ReferenceEquals(AssistantSelector.SelectedItem, NewConversationOption))
        {
            _updating = true;
            RestoreAssistantSelection();
            _updating = false;
            AssistantSelector.IsDropDownOpen = false;
            AsyncEventHandlerGuard.Run(NewSessionAsync, new AppLogger(), nameof(OnAssistantChanged));
            return;
        }
        if (AssistantSelector.SelectedItem is not ComboBoxItem { Tag: WorkspaceAgent agent }) return;
        AsyncEventHandlerGuard.Run(() => StartAgentChatAsync(agent), new AppLogger(), nameof(OnAssistantChanged));
    }

    private void RestoreAssistantSelection() =>
        AssistantSelector.SelectedItem = AssistantSelector.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is WorkspaceAgent agent && agent.Id == _agentId);

    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (_updating) return;
        if (e.SelectedItemContainer?.Tag is string route)
            Navigate(new(WorkspaceNavigation.Routes[route]));
    }

    private void OnNewConversation(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(NewSessionAsync, new AppLogger(), nameof(OnNewConversation));
    private void OnNavigationInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs e)
    {
        if (e.InvokedItemContainer?.Tag is WorkspaceSession session) SelectSession(session.Key);
    }
    private void OnNotifications(object sender, RoutedEventArgs e) => Navigate(new(WorkspacePageId.Notifications));
    private void OnBack(object sender, RoutedEventArgs e) => NavigateBack();
    private void OnForward(object sender, RoutedEventArgs e) => NavigateForward();
    private void OnTogglePane(object sender, RoutedEventArgs e) => NavView.IsPaneOpen = !NavView.IsPaneOpen;
    private void UpdatePanePresentation()
    {
        var visibility = NavView.IsPaneOpen ? Visibility.Collapsed : Visibility.Visible;
        CompactPaneItem.Visibility = visibility;
        CompactNotificationsItem.Visibility = visibility;
        ExpandedFooter.Visibility = visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
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
        Add("Apps", () => Navigate(new(WorkspacePageId.Apps)), FluentIconCatalog.OpenInBrowser);
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
        _state.PropertyChanged -= OnStateChanged;
        _workspacePage?.Detach();
        ContentHost.Children.Clear();
        _chat.CloseSurface();
    }
}
