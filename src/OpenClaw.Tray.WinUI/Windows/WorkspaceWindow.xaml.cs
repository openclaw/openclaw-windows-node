using System.ComponentModel;
using System.Collections.Immutable;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OpenClaw.Connection;
using OpenClaw.SetupEngine;
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
    // VK_OEM_COMMA; VirtualKey has no named member for it.
    private const global::Windows.System.VirtualKey SettingsShortcutKey = (global::Windows.System.VirtualKey)188;
    private const string SettingsShortcutText = "Ctrl+,";
    private readonly AppState _state;
    private readonly Action _openTimeline;
    private readonly AppNotificationService _notifications;
    private readonly InboxFeed _inbox;
    private readonly WorkspaceIdentitySource _identity;
    private readonly WorkspaceSessionMenuController _sessionMenu;
    private readonly WorkspaceNavigationHistory _navigation = new();
    private readonly WorkspaceSessionOrder _sessionOrder = new();
    private readonly ChatPage _chat = new();
    private readonly GatewayStatusContent _gatewayStatusContent = new();
    private readonly Flyout _gatewayStatusFlyout = new() { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft };
    private readonly FlyoutShowRequest _workspaceSearchShow;
    // Sidebar rows keyed by session key; SyncSessionItems keeps MenuItems in step with these.
    private readonly Dictionary<string, NavigationViewItem> _sessionItems = new(StringComparer.Ordinal);
    private bool _updating;
    private bool _creatingSession;
    // Session switches commit sidebar/navigation state synchronously; the chat surface is
    // applied on one later low-priority dispatcher turn so the click paints first, and rapid
    // clicks coalesce onto the latest ChatPage.QueueSession value.
    private bool _chatApplyQueued;
    private bool _showingAgentCreation;
    private readonly ProfileAutoFillCoordinator _profileAutoFill = CurrentApp.ProfileAutoFill;
    private IOperatorGatewayClient? _refreshingClient;
    private readonly HubCommandExecutor _commandExecutor = new();
    private readonly HubSearchController _search;
    private readonly WorkspaceAgentSelection _agentSelection = new();
    // Settings lives in this window as a retained sibling of the chat section. Chat is only
    // collapsed while Settings is visible, so its control identity, draft and focus survive.
    private SettingsShellView? _settings;
    private SettingsShellView? _preparedSettings;
    private Microsoft.UI.Xaml.Controls.Control? _chatFocus;

    /// <summary>Raised when a Settings page saved a user setting.</summary>
    internal event EventHandler? SettingsSaved;

    public bool IsClosed { get; private set; }
    internal bool IsChatVisible => !IsClosed && ChatVisibilityPolicy.IsWorkspaceChatVisible(
        IsClosed,
        AppWindow.IsVisible,
        AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
            { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized },
        Destination.Page);
    internal string? SelectedAgentId => _agentSelection.AgentId;
    internal WorkspaceDestination Destination => _navigation.Current;
    internal ChatPage ChatPage => _chat;
    internal bool CanGoBack => (IsSettingsVisible && _settings?.HasOpenDashboard == true) || _navigation.CanGoBack;

    internal WorkspaceWindow(
        AppState state, AppNotificationService notifications, Action openTimeline)
    {
        InitializeComponent();
        _state = state;
        WorkspaceSearchFlyout.FlyoutPresenterStyle = SearchHost.CreatePresenterStyle();
        _search = new(CaptureSearch, new Presentation.Adapters.WinUIDispatcher(DispatcherQueue),
            category => Logger.Warn($"[Search] {category}"));
        _notifications = notifications;
        // App creates the feed with the notification service; a window-owned fallback would never unsubscribe.
        _inbox = CurrentApp.InboxFeed
            ?? throw new InvalidOperationException("The Inbox feed is created with the app notification service.");
        _openTimeline = openTimeline;
        _identity = new WorkspaceIdentitySource(UpdateOwnerIdentity,
            category => Logger.Warn(WorkspaceIdentitySource.DescribeUnavailable(category)));
        _profileAutoFill.ProfileChanged += OnProfileAutoFilled;
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
        _workspaceSearchShow = new FlyoutShowRequest(WorkspaceSearchFlyout, OpenCommandCenter);
        var searchLabel = LocalizationHelper.GetString("TitleSearchBox.PlaceholderText");
        AutomationProperties.SetName(WorkspaceSearchButton, searchLabel);
        ToolTipService.SetToolTip(WorkspaceSearchButton, searchLabel);
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
        TitleBarFooter.Layout = WorkspacePaneFooterLayout.TitleBar;
        ConfigureFooter(ChatFooter);
        ConfigureFooter(TitleBarFooter);
        _state.PropertyChanged += OnStateChanged;
        _inbox.Changed += OnNotificationsChanged;
        _chat.Loaded += (_, _) =>
        {
            if (Destination.Page == WorkspacePageId.Home) _chat.Initialize(this);
        };
        _chat.SessionSelected += OnChatSessionSelected;
        Closed += OnClosed;
        Root.Loaded += OnLoaded;
        // The no-gateway accessibility fixture publishes its explicit roster before this window exists.
        RefreshSidebar(rosterUpdated:
            _state.AgentsListUnsupported ||
            (Environment.GetEnvironmentVariable("OPENCLAW_ACCESSIBILITY_TEST_SESSIONS") == "1"
            && Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR") is { Length: > 0 }));
        WirePins();
    }

    internal static string Text(string key) => LocalizationHelper.GetString($"WorkspaceShell_{key}");

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RenderDestination();
        _ = RefreshAsync();
    }

    internal void Navigate(WorkspaceDestination destination, bool preserveConversation = true) =>
        TryNavigate(destination, preserveConversation);

    internal bool TryNavigate(WorkspaceDestination destination, bool preserveConversation = true)
    {
        if (IsClosed) return false;
        if (preserveConversation && destination is { Page: WorkspacePageId.Home, SessionKey: null })
            destination = _navigation.ChatDestination;
        if (_deferred.ShouldDefer(destination != Destination) && DeferWhileModalOpen(() => Navigate(destination, preserveConversation)))
            return false;
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
            return IsDestinationShown(destination);
        }
        if (Root.XamlRoot is not null)
            RenderDestination();
        return IsDestinationShown(destination);
    }

    private bool IsDestinationShown(WorkspaceDestination destination) =>
        !IsClosed && Root.XamlRoot is not null && Destination == destination &&
        (destination.Page == WorkspacePageId.Settings
            ? _settings?.IsSectionShown(destination.CompanionTag ?? "settings") == true
            : destination.Page == WorkspacePageId.Home
                ? ContentHost.Children.Contains(_chat)
                : ContentHost.Children.FirstOrDefault() is NotificationsPage);

    internal void OpenCompanion(CompanionPageId page, string? agentId = null) =>
        OpenSettings(WorkspaceNavigation.CompanionTag(page, agentId ?? SelectedAgentId ?? "main"));

    /// <summary>
    /// Shows a Settings section in this window. The section becomes one history entry; the visible
    /// chat's agent selection is never changed by Settings agent routes.
    /// </summary>
    internal void OpenSettings(string tag) => OpenSettings(tag, embedded: false);

    internal void OpenSettings(string tag, bool embedded) => TryOpenSettings(tag, embedded);

    internal bool TryOpenSettings(string tag, bool embedded = false)
    {
        if (IsClosed) return false;
        if (tag == "command-center")
        {
            OpenCommandCenter();
            return false;
        }
        return TryNavigate(WorkspaceDestination.Settings(
            WorkspaceNavigation.NormalizeSettingsTag(tag, _settings?.CurrentAgentId ?? SelectedAgentId ?? "main"), embedded));
    }

    /// <summary>The Settings view while it exists; null before Settings was first opened.</summary>
    internal SettingsShellView? SettingsView => _settings;
    internal bool IsSettingsVisible => !IsClosed && Destination.Page == WorkspacePageId.Settings;

    internal void OpenTimeline() => _openTimeline();

    /// <summary>
    /// Opens the search of the visible surface: the Settings flyout while Settings is shown,
    /// otherwise the main-screen flyout. Neither changes the page, conversation or draft.
    /// </summary>
    private bool _commandCenterPending;

    internal void OpenCommandCenter()
    {
        if (IsClosed) return;
        if (!Root.IsLoaded)
        {
            // A window opened by app.navigate has no loaded anchors yet; open once its tree is live.
            if (_commandCenterPending) return;
            _commandCenterPending = true;
            void Retry(object? s, RoutedEventArgs e)
            {
                Root.Loaded -= Retry;
                DispatcherQueue.TryEnqueue(() =>
                {
                    _commandCenterPending = false;
                    OpenCommandCenter();
                });
            }
            Root.Loaded += Retry;
            return;
        }
        if (OpenModalDialog() is { } dialog)
        {
            // Keep one request and open search after the dialog closes; navigation intents are not touched.
            // The second hop runs after the deferred navigation drain queued by the same Closed event,
            // so search opens on the surface navigation leaves visible.
            if (_commandCenterPending) return;
            _commandCenterPending = true;
            dialog.Closed += (_, _) => DispatcherQueue.TryEnqueue(() => DispatcherQueue.TryEnqueue(() =>
            {
                _commandCenterPending = false;
                OpenCommandCenter();
            }));
            return;
        }
        if (_workspaceSearchShow.DeferUntilClosed() || _settings?.DeferCommandSearchUntilClosed() == true)
            return;
        if (IsSettingsVisible && !Destination.Embedded)
        {
            if (_settings is { IsLoaded: true }) _settings.OpenCommandCenter();
            else if (_settings is not null && !_commandCenterPending)
            {
                // Settings was just created by navigation; open its search once it is live.
                _commandCenterPending = true;
                void SettingsLoaded(object? s, RoutedEventArgs e)
                {
                    _settings.Loaded -= SettingsLoaded;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _commandCenterPending = false;
                        OpenCommandCenter();
                    });
                }
                _settings.Loaded += SettingsLoaded;
            }
            return;
        }
        var anchor = WorkspaceSearchButton.IsLoaded && NavView.IsPaneOpen
            ? (FrameworkElement)WorkspaceSearchButton
            : ReopenPaneButton;
        _workspaceSearchShow.ShowAt(anchor);
    }

    private HubSearchContext CaptureSearch()
    {
        var agent = IsSettingsVisible ? _settings!.CurrentAgentId : SelectedAgentId ?? "";
        var client = CurrentApp.GatewayClient;
        var items = HubCommandCatalog.BuildSearch(_state, agent, client?.GrantedOperatorScopes.Contains("operator.admin") == true);
        return new(client, agent, _state.GatewaySelf?.ServerVersion, items,
            _state.Sessions.Select(session => session.Clone()).ToArray(), _state.SelfProfileRevision,
            _state.Status == ConnectionStatus.Connected);
    }

    private void OnWorkspaceSearchOpening(object sender, object e) =>
        WorkspaceSearchHost.Begin(_search, ExecuteSearch, () => WorkspaceSearchFlyout.Hide(), "WorkspaceSearchBox");

    private void OnWorkspaceSearchClosed(object sender, object e) => WorkspaceSearchHost.End();
    private void OnWorkspaceSearchOpened(object sender, object e) => WorkspaceSearchHost.FocusInput();

    private void ExecuteSearch(HubSearchTarget target) => _commandExecutor.Execute(target, destination =>
    {
        if (destination.Kind == HubSearchTargetKind.Session)
            SelectSession(destination.Value);
        else
        {
            OnSettingsWorkspaceRoute(destination.Value);
            if (destination.Focus is not null) _settings?.RevealSearchTarget(destination);
        }
    });

    internal void SelectSession(string sessionKey) =>
        Navigate(new(WorkspacePageId.Home, sessionKey));

    private void NavigateHome() =>
        Navigate(new(WorkspacePageId.Home,
            WorkspaceProjection.HomeSessionKey(_state.Sessions, SelectedAgentId, CurrentApp.GatewayClient?.MainSessionKey)),
            preserveConversation: false);

    private void OnChatSessionSelected(string sessionKey, string? previousSessionKey)
    {
        if (IsClosed || !_navigation.SelectChatSession(sessionKey, previousSessionKey))
            return;

        var session = _state.Sessions.FirstOrDefault(session => session.Key == sessionKey)
            ?? new SessionInfo { Key = sessionKey };
        _agentSelection.Select(SessionDisplayResolver.Resolve(session).AgentId);
        RefreshSidebar();
        UpdateNavigationSelection();
        UpdateHistoryButtons();
    }

    internal void NavigateNativeSetup(SetupNativeNavigationRequest request)
    {
        var destination = request.WorkspaceDestination
            ?? throw new SetupNativeOwnershipException();
        // A one-shot request never binds or initializes a surface underneath any shared-root dialog.
        // Only ordinary destination intent may survive until the dialog closes.
        if (OpenModalDialog() is not null)
        {
            if (destination != Destination) Navigate(destination);
            return;
        }
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
        _agentSelection.Select(agent.Id);
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
        LeaveSettings();
        if (Destination.Page == WorkspacePageId.Home && Destination.SessionKey is { } sessionKey)
        {
            var session = _state.Sessions.FirstOrDefault(session => session.Key == sessionKey)
                ?? new SessionInfo { Key = sessionKey };
            _agentSelection.Select(SessionDisplayResolver.Resolve(session).AgentId);
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
            notifications.Initialize(_inbox, InboxCommands(openPage: null));
            ContentHost.Children.Add(notifications);
        }

        UpdateNavigationSelection();
        UpdateHistoryButtons();
        if (ContentHost.Children.FirstOrDefault() is FrameworkElement content)
            SignalContentReady(content);
    }

    private void UpdateHistoryButtons()
    {
        var back = CanGoBack;
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
        var view = _preparedSettings ?? new SettingsShellView();
        _preparedSettings = null;
        view.AppModel = _state;
        view.NavigateRequested = tag => OpenSettings(tag);
        view.PageNavigationChanged = UpdateHistoryButtons;
        view.PageContentChanged = () => UpdateEmbeddedPageTopAllocation(view);
        view.WorkspaceRouteRequested = OnSettingsWorkspaceRoute;
        view.ExitRequested = ExitSettings;
        view.PruneRequested = PruneSettingsHistory;
        view.SearchController = _search;
        view.SearchNavigation = ExecuteSearch;
        view.CommandCenterRequested = OpenCommandCenter;
        view.SettingsSaved += OnSettingsViewSaved;
        view.BindAppNotifications(_notifications);
        if (CurrentApp.Settings is { } settings)
            view.ApplyNavPaneState(settings);
        view.BindToAppState();
        SettingsSection.Children.Insert(0, view);
        _settings = view;
        ConfigureFooter(view.Footer);
        return view;
    }

    private void OnSettingsViewSaved(object? sender, EventArgs e) => SettingsSaved?.Invoke(this, e);

    private void RenderSettings()
    {
        var view = EnsureSettings();
        _sidebarInvoked = false;
        if (SettingsSection.Visibility != Visibility.Visible)
        {
            _chatFocus = Root.XamlRoot is { } root
                ? Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) as Microsoft.UI.Xaml.Controls.Control
                : null;
            _chat.SuspendSurface();
            NavigationSection.Visibility = Visibility.Collapsed;
            SettingsSection.Visibility = Visibility.Visible;
            SettingsHistoryBar.Visibility = Visibility.Visible;
            HideFooterFlyouts();
            UpdateTitleBarActions();
        }
        ApplySettingsPresentation(view);
        UpdateNavigationSelection();
        UpdateHistoryButtons();
        var tag = Destination.CompanionTag ?? "settings";
        view.ShowSection(tag);
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
        // A deliberate click or key press on another sidebar row owns focus; only Back/Forward and
        // other non-row exits restore the control that opened Settings.
        var focus = WorkspaceDestination.SettingsExitFocus(_chatFocus,
            _sidebarInvoked ? SettingsExitCause.SidebarActivation : SettingsExitCause.History);
        _sidebarInvoked = false;
        _chatFocus = null;
        _settings?.Deactivate();
        _settings?.ContentOnly = false;
        _settings?.SetHostContentTopInset(0);
        SettingsSection.Margin = new Thickness(0);
        EmbeddedPageBackground.Visibility = Visibility.Collapsed;
        ContentHost.Visibility = Visibility.Visible;
        SettingsSection.Visibility = Visibility.Collapsed;
        SettingsHistoryBar.Visibility = Visibility.Collapsed;
        EmbeddedOperationInfo.IsOpen = false;
        NavigationSection.Visibility = Visibility.Visible;
        HideFooterFlyouts();
        UpdateTitleBarActions();
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
        }, OpenSettings, OpenDashboard);
    }

    /// <summary>
    /// Back to app: leaves full Settings for the app destination it was opened from, as one ordinary
    /// history entry. The retained chat host and draft are reused, a modal dialog defers the change,
    /// and focus returns to the control that opened Settings.
    /// </summary>
    private void ExitSettings()
    {
        if (!IsSettingsVisible || Destination.Embedded) return;
        // Resolve the target and its focus only when the exit actually runs, after any dialog closes.
        if (DeferWhileModalOpen(ExitSettings)) return;
        _sidebarInvoked = false;
        // Like Back/Forward, never return to a pinned Gateway page that is no longer available.
        PruneUnavailableGatewayHistory();
        var destination = _navigation.AppDestination;
        Navigate(destination);
        // LeaveSettings restores chat focus. A rebuilt Notifications page or a pinned page beside the
        // sidebar has no saved control, so focus moves to the destination instead of a hidden button.
        if (destination.Page != WorkspacePageId.Home)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => FocusAppDestination(destination));
    }

    private void FocusAppDestination(WorkspaceDestination destination)
    {
        if (IsClosed || Destination != destination) return;
        // The pin row is unfocusable while the main sidebar is hidden; the page itself is the fallback.
        if (destination.Embedded && TryFocus(NavView.SelectedItem as DependencyObject)) return;
        var page = destination.Embedded ? (DependencyObject)SettingsSection : ContentHost;
        TryFocus(Microsoft.UI.Xaml.Input.FocusManager.FindFirstFocusableElement(page));
    }

    private static bool TryFocus(DependencyObject? target) => target switch
    {
        Control control => control.Focus(FocusState.Programmatic),
        Microsoft.UI.Xaml.Documents.Hyperlink link => link.Focus(FocusState.Programmatic),
        _ => false,
    };

    // Bubbling KeyDown, so the focused control handles Escape first (PreviewKeyDown would steal it).
    private void OnRootKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != global::Windows.System.VirtualKey.Escape) return;
        if (!SettingsEscapePolicy.ExitsSettings(Destination, e.Handled, e.KeyStatus.WasKeyDown,
                overlayOpen: HasOpenOverlay(), focusInEditor: IsInEscapeOwningEditor(e.OriginalSource as DependencyObject)))
            return;
        e.Handled = true;
        ExitSettings();
    }

    private bool HasOpenOverlay() =>
        Root.XamlRoot is { } xamlRoot && Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot)
            .Any(popup => popup.Child is not ToolTip);

    private bool IsInEscapeOwningEditor(DependencyObject? source)
    {
        for (var element = source; element is not null; element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element))
        {
            if (element is TextBox or PasswordBox or RichEditBox or AutoSuggestBox or ComboBox or NumberBox)
                return true;
            if (ReferenceEquals(element, Root))
                return false;
        }
        return false;
    }

    /// <summary>
    /// Drops unavailable Settings sections from the single history. A pruned current section is
    /// replaced by <paramref name="fallbackTag"/> without a history entry and rendered.
    /// </summary>
    private void PruneSettingsHistory(Func<string, bool> shouldRemove, string? fallbackTag)
    {
        if (IsClosed) return;
        var changed = _navigation.PruneSettings(
            shouldRemove, fallbackTag is null ? _navigation.Current : WorkspaceDestination.PrunedFallback(_navigation.Current, fallbackTag));
        UpdateHistoryButtons();
        if (!changed || fallbackTag is null || Root.XamlRoot is null)
            return;
        // The required render is retained apart from any replaceable intent and waits for every open dialog.
        var modal = OpenModalDialog();
        if (modal is not null || _deferred.HasPending)
        {
            _deferred.DeferRender();
            if (modal is not null) ArmDeferredDrain(modal);
        }
        else
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
        var wasUpdating = _updating;
        _updating = true;
        try
        {
            var items = NavView.MenuItems.OfType<NavigationViewItem>();
            var selected = Destination.Page == WorkspacePageId.Home
                ? items.FirstOrDefault(item => item.Tag is WorkspaceSession session && session.Key == Destination.SessionKey)
                    ?? HomeItem
                : Destination is { Page: WorkspacePageId.Settings, Embedded: true }
                    ? items.FirstOrDefault(item => item.Tag is PinnedPageTag pin && IsPinDestination(pin))
                    : null;
            if (ReferenceEquals(NavView.SelectedItem, selected)) return;
            // WinUI treats selecting a nonselecting item as an invocation and restores the old
            // selection. Allow only this guarded projection; actual pin invocations stay nonselecting.
            var selectsOnInvoked = selected?.SelectsOnInvoked ?? true;
            try
            {
                if (selected is not null) selected.SelectsOnInvoked = true;
                NavView.SelectedItem = selected;
            }
            finally
            {
                if (selected is not null) selected.SelectsOnInvoked = selectsOnInvoked;
            }
        }
        finally { _updating = wasUpdating; }
    }

    private bool IsPinDestination(PinnedPageTag pin) =>
        pin.Tag == DashboardsRoute.Tag
            ? DashboardsRoute.IsDashboardsTag(Destination.CompanionTag)
            : WorkspaceNavigation.NormalizeSettingsTag(PinnedPages.ResolveRoute(pin.Tag, SelectedAgentId),
                _settings?.CurrentAgentId ?? SelectedAgentId ?? "main") == Destination.CompanionTag;

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

    private void RefreshSidebar(bool rosterUpdated = false)
    {
        _updating = true;
        var rosterAgents = WorkspaceProjection.Agents(_state.AgentsList, _state.Sessions, _state.AgentIdentities);
        _agentSelection.Refresh(CurrentApp.ConnectionManager?.CurrentSnapshot.GatewayId,
            _state.AgentsList, rosterAgents, rosterUpdated, _state.AgentsListUnsupported);
        IReadOnlyList<WorkspaceAgent> agents = _agentSelection.HasCurrentRoster ? rosterAgents : [];
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
            UpdateAssistantItem(AssistantSelector, item, agent);
            desiredItems.Add(item);
        }
        desiredItems.Add(NewAgentOption);
        // Keep the open popup and selected container alive across independent roster/session responses.
        WorkspaceItemsSync.Arrange(AssistantSelector.Items, 0, desiredItems.Cast<object>().ToList());
        while (AssistantSelector.Items.Count > desiredItems.Count)
            AssistantSelector.Items.RemoveAt(AssistantSelector.Items.Count - 1);
        AssistantSelector.IsEnabled = _agentSelection.CanOpenAssistantMenu(_state.Status);
        RestoreAssistantSelection();
        AssistantSelector.PlaceholderText = Text(agents.Count == 0 ? "NoAgents" : "SelectAssistant");
        IReadOnlyList<WorkspaceSession> sessions = _agentSelection.HasSessionAccess
            ? WorkspaceProjection.Sessions(_state.Sessions, SelectedAgentId, _sessionOrder, CurrentApp.GatewayClient?.MainSessionKey) : [];
        SyncSessionItems(_sessionItems, sessions, "WorkspaceSession", NavView.MenuItems.IndexOf(SessionsEmpty) + 1);
        SessionsEmpty.Content = Text("NoSessions");
        SessionsEmpty.Visibility = sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NewAgentOption.IsEnabled = !_showingAgentCreation;
        var selectionRequired = !_agentSelection.IsReady;
        NewSessionButton.IsEnabled =
            !_creatingSession && _state.Status == ConnectionStatus.Connected && !selectionRequired;
        AutomationProperties.SetHelpText(NewSessionButton, selectionRequired ? Text("SelectAssistant") : string.Empty);
        ToolTipService.SetToolTip(NewSessionButton, selectionRequired ? Text("SelectAssistant") : null);
        _updating = false;
        // Before layout, NavigationView is still minimal and selecting an item closes its pane.
        if (Root.IsLoaded)
            UpdateNavigationSelection();
    }

    internal static void UpdateAssistantItem(ComboBox selector, ComboBoxItem item, WorkspaceAgent agent)
    {
        if (Equals(item.Content, agent))
            return;

        // ComboBox caches its closed selection separately from the item's content.
        // Re-select under RefreshSidebar's _updating guard so identity hydration cannot navigate.
        var selected = ReferenceEquals(selector.SelectedItem, item);
        if (selected) selector.SelectedItem = null;
        item.Content = agent;
        item.Tag = agent;
        AutomationProperties.SetName(item, $"{agent.Name}, {agent.Id}");
        AutomationProperties.SetAutomationId(item, $"WorkspaceAgent:{agent.Id}");
        if (selected) selector.SelectedItem = item;
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
        if (!_agentSelection.IsReady)
        {
            ShowError(Text("SelectAssistant"));
            AssistantSelector.Focus(FocusState.Programmatic);
            return;
        }

        await CreateAndSelectSessionAsync(
            client, new SessionCreateRequest { AgentId = SelectedAgentId },
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
        ShowOperation(message, severity);
    }

    internal void ShowError(string message)
    {
        Logger.Warn($"[Workspace] {message}");
        if (IsClosed) return;
        ShowOperation(message, InfoBarSeverity.Error);
    }

    // An embedded Settings section covers the chat's InfoBar, so the same message also shows above it.
    private void ShowOperation(string message, InfoBarSeverity severity)
    {
        OperationInfo.Severity = severity;
        OperationInfo.Message = message;
        OperationInfo.IsOpen = true;
        EmbeddedOperationInfo.Severity = severity;
        EmbeddedOperationInfo.Message = message;
        EmbeddedOperationInfo.IsOpen = OperationInfo.IsOpen && IsSettingsVisible && Destination.Embedded;
    }

    // Only the user's close button dismisses the shared message; hiding the mirror must not.
    private void OnEmbeddedOperationClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (args.Reason == InfoBarCloseReason.CloseButton) OperationInfo.IsOpen = false;
    }

    // Set by a sidebar row invocation and consumed by the next Settings exit or render, so a row that is
    // merely focused (then Back/Forward) never counts as the cause of leaving Settings.
    private bool _sidebarInvoked;

    private void ReportError(string operation, Exception ex)
    {
        Logger.Error($"[Workspace] {operation} failed: {ex}");
        ShowError(ex.Message);
    }

    private void LeaveSession(string key)
    {
        var next = WorkspaceProjection.Sessions(_state.Sessions, SelectedAgentId, _sessionOrder)
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
        if (e.PropertyName is nameof(AppState.Sessions) or nameof(AppState.AgentsList) or
            nameof(AppState.Status) or nameof(AppState.SelfProfileRevision))
            _search.Refresh();
        if (e.PropertyName is nameof(AppState.Status) or nameof(AppState.Presence) or nameof(AppState.SelfProfileRevision))
            RefreshOwnerIdentity(e.PropertyName == nameof(AppState.SelfProfileRevision));
        if (e.PropertyName is nameof(AppState.AgentsList) or nameof(AppState.AgentsListUnsupported)
            or nameof(AppState.AgentIdentities) or nameof(AppState.Sessions) or nameof(AppState.Status))
            RefreshSidebar(rosterUpdated: e.PropertyName == nameof(AppState.AgentsList) ||
                e.PropertyName == nameof(AppState.AgentsListUnsupported) && _state.AgentsListUnsupported);
        if (e.PropertyName == nameof(AppState.Status))
            RebuildPins();
        if (e.PropertyName == nameof(AppState.Status) && _state.Status == ConnectionStatus.Connected)
            _ = RefreshAsync();
        if (e.PropertyName == nameof(AppState.Status))
            UpdateConnectionStatus(CurrentApp.ConnectionManager?.CurrentSnapshot, _state.Status);
        if (e.PropertyName == nameof(AppState.UpdateInfo))
            UpdateAvailableBadge();
    }

    internal void UpdateConnectionStatus(GatewayConnectionSnapshot? snapshot, ConnectionStatus status)
    {
        if (_identity.SetConnection(CurrentApp.GatewayClient, status))
            _ = _identity.RefreshAsync();
        var (labelKey, accent) = ConnectionStatusPresenter.Pill(snapshot?.OverallState, status);
        var label = LocalizationHelper.GetString(labelKey);
        foreach (var footer in Footers)
            footer.ApplyConnection(label, accent);
    }

    private void RefreshOwnerIdentity(bool invalidate = false)
    {
        _identity.SetConnection(CurrentApp.GatewayClient, _state.Status);
        _ = _identity.RefreshAsync(invalidate);
    }

    private UserAvatarImage? _shownAvatar;
    private Microsoft.UI.Xaml.Media.Imaging.BitmapImage? _ownerImage;

    // Web UI parity: the shared-credential owner keeps its marker beside any saved name; no known profile shows Owner.
    private string OwnerLabel() =>
        _identity.FooterLabel(Text("Owner.Text"), Text("SharedOwner.Text"),
            ownerName => LocalizationHelper.Format("WorkspaceShell_SharedOwnerNamedFormat", ownerName));

    private void UpdateOwnerIdentity()
    {
        var name = _identity.DisplayName;
        var label = OwnerLabel();
        if (!IsClosed && name is not null)
            AsyncEventHandlerGuard.Run(_profileAutoFill.ObserveAsync, new AppLogger(), nameof(UpdateOwnerIdentity));
        var avatar = _identity.Avatar;
        var avatarChanged = !ReferenceEquals(avatar, _shownAvatar);
        if (avatarChanged)
        {
            _shownAvatar = avatar;
            _ownerImage = null;
        }
        foreach (var footer in Footers)
        {
            footer.ApplyIdentity(label, name ?? "");
            footer.ApplyAvatar(_ownerImage);
        }
        _chat.ApplyUserIdentity(name ?? "", _ownerImage);
        if (!avatarChanged || avatar is null) return;
        AsyncEventHandlerGuard.Run(() => ShowOwnerAvatarAsync(avatar), new AppLogger(), nameof(UpdateOwnerIdentity));
    }

    private async Task ShowOwnerAvatarAsync(UserAvatarImage avatar)
    {
        Microsoft.UI.Xaml.Media.Imaging.BitmapImage image;
        try
        {
            using var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
            await stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(avatar.Bytes));
            stream.Seek(0);
            image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelWidth = 48 };
            await image.SetSourceAsync(stream);
        }
        catch (Exception ex) when (WorkspaceIdentitySource.IsAvatarDecodeFailure(ex))
        {
            new AppLogger().Warn($"Owner avatar could not be decoded ({ex.GetType().Name}); initials shown.");
            return;
        }
        if (ReferenceEquals(avatar, _shownAvatar))
        {
            _ownerImage = image;
            foreach (var footer in Footers)
                footer.ApplyAvatar(image);
            _chat.ApplyUserIdentity(_identity.DisplayName ?? "", image);
        }
    }

    /// <summary>Every live footer: Chat pane, Chat title bar and, once created, Settings pane.</summary>
    private IEnumerable<WorkspacePaneFooter> Footers
    {
        get
        {
            yield return ChatFooter;
            yield return TitleBarFooter;
            if (_settings is { } settings)
                yield return settings.Footer;
        }
    }

    /// <summary>Wires one footer to the window-owned state and commands, then applies current state.</summary>
    private void ConfigureFooter(WorkspacePaneFooter footer)
    {
        footer.BindNotifications(_inbox, InboxCommands(openPage: () => Navigate(new(WorkspacePageId.Notifications))));
        footer.DashboardRequested += (_, _) => OpenDashboard();
        if (footer.Layout != WorkspacePaneFooterLayout.TitleBar)
            footer.SetOwnerMenu(BuildOwnerMenu(footer));
        UpdateOwnerIdentity();
        UpdateConnectionStatus(CurrentApp.ConnectionManager?.CurrentSnapshot, _state.Status);
        UpdateNotificationsBadge();
        UpdateAvailableBadge();
    }

    private void UpdateAvailableBadge()
    {
        var text = _state.UpdateInfo?.IsUpdateAvailable == true ? Text("AccountUpdateAvailable") : null;
        foreach (var footer in Footers)
            footer.ApplyUpdateAvailable(text);
    }

    private string? ResolveDashboardPath()
    {
        var destination = Destination;
        var sessionKey = destination.Page == WorkspacePageId.Home
            ? destination.SessionKey ?? _chat.CurrentSessionKey
            : null;
        bool? isMain = sessionKey is null
            ? null
            : _state.Sessions.FirstOrDefault(session => session.Key == sessionKey)?.IsMain;
        if (sessionKey is not null && isMain is null &&
            CurrentApp.GatewayClient?.MainSessionKey is { } mainSessionKey)
            isMain = string.Equals(sessionKey, mainSessionKey, StringComparison.Ordinal);

        return HubSearchCatalog.ContextualDashboardPath(
            destination, sessionKey, isMain, _state.GatewaySelf?.ServerVersion);
    }

    private void OpenDashboard(string? path = null) =>
        ((IAppCommands)CurrentApp).OpenDashboard(path ?? ResolveDashboardPath());

    /// <summary>Footer popups are anchored to one section; switching sections closes them.</summary>
    private void HideFooterFlyouts()
    {
        _workspaceSearchShow.Cancel();
        _gatewayStatusFlyout.Hide();
        foreach (var footer in Footers)
            footer.HideFlyouts();
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
            .FirstOrDefault(item => item.Tag is WorkspaceAgent agent && agent.Id == SelectedAgentId);

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

    private void OnProfileAutoFilled(object? sender, EventArgs e)
    {
        if (!IsClosed)
            RefreshOwnerIdentity(invalidate: true);
    }

    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (_updating) return;
        if (e.SelectedItemContainer?.Tag is string route)
        {
            if (route == "home") NavigateHome();
            else Navigate(new(WorkspaceNavigation.Routes[route]), preserveConversation: false);
            UpdateNavigationSelection();
        }
        else if (e.SelectedItemContainer?.Tag is WorkspaceSession session)
            SelectSession(session.Key);
    }

    private void OnNewConversation(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(NewSessionAsync, new AppLogger(), nameof(OnNewConversation));
    private void OnNotificationsChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsClosed) UpdateNotificationsBadge();
        });

    private void UpdateNotificationsBadge()
    {
        var badge = _inbox.Snapshot.Badge;
        var helpText = badge.IsVisible
            ? LocalizationHelper.Format("Inbox_CountFormat", badge.Count)
            : LocalizationHelper.GetString("Inbox_NothingWaiting");
        foreach (var footer in Footers)
            footer.ApplyInboxBadge(badge, helpText);
    }

    /// <summary>Gateway facts navigate without dismissal; app reminders use the separate admission callback.</summary>
    private InboxViewCommands InboxCommands(Action? openPage) => new(
        OpenGatewayInboxRoute,
        ((IAppCommands)CurrentApp).TryNavigateAsync,
        () => OnSettingsWorkspaceRoute(InboxRoutes.NotificationSettings),
        () => ((IAppCommands)CurrentApp).OpenDashboard(),
        openPage,
        AskOpenClaw);

    /// <summary>Sends an Inbox failure's explain prompt to the main conversation and shows it.</summary>
    private void AskOpenClaw(string question)
    {
        if (InboxChatPrompt.TrySend(CurrentApp.ChatProvider, CurrentApp.GatewayClient, question, out var sessionKey))
            SelectSession(sessionKey);
        else
            OpenCompanion(CompanionPageId.Connection);
    }

    private void OpenGatewayInboxRoute(string route)
    {
        if (AppNotificationActionRoutes.TryGetChatSessionKey(route, out var sessionKey) && sessionKey is not null)
        {
            SelectSession(sessionKey);
            return;
        }
        if (string.Equals(route, "chat", StringComparison.OrdinalIgnoreCase))
        {
            NavigateHome();
            return;
        }
        try
        {
            OnSettingsWorkspaceRoute(route);
        }
        catch (ArgumentException ex)
        {
            new AppLogger().Warn($"[Inbox] Ignored unknown notification route: {ex.Message}");
        }
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
        if (_settings is { } settings && IsSettingsVisible)
            ApplySettingsPresentation(settings);
        if (!NavView.IsPaneOpen)
        {
            ChatFooter.HideFlyouts();
            _gatewayStatusFlyout.Hide();
        }
        UpdateTitleBarActions();
    }

    // An embedded section sits to the right of the sidebar, which stays visible and usable. The
    // chat underneath is hidden so it cannot take focus, and the sidebar's Back/Forward serve
    // history unless the pane is collapsed, when the Settings history bar returns.
    private void ApplySettingsPresentation(SettingsShellView view)
    {
        var embedded = Destination.Embedded;
        if (embedded && !view.ContentOnly)
            view.Footer.HideFlyouts();
        view.ContentOnly = embedded;
        NavigationSection.Visibility = embedded ? Visibility.Visible : Visibility.Collapsed;
        ContentHost.Visibility = embedded ? Visibility.Collapsed : Visibility.Visible;
        var paneOpen = NavView.IsPaneOpen;
        UpdateEmbeddedPageTopAllocation(view);
        EmbeddedPageBackground.Visibility = embedded ? Visibility.Visible : Visibility.Collapsed;
        SettingsHistoryBar.Visibility = embedded && paneOpen ? Visibility.Collapsed : Visibility.Visible;
        EmbeddedOperationInfo.Severity = OperationInfo.Severity;
        EmbeddedOperationInfo.Message = OperationInfo.Message;
        EmbeddedOperationInfo.IsOpen = embedded && OperationInfo.IsOpen;
        UpdateTitleBarActions();
    }

    private void UpdateEmbeddedPageTopAllocation(SettingsShellView view)
    {
        var embedded = IsSettingsVisible && Destination.Embedded;
        var paneOpen = NavView.IsPaneOpen;
        var allocation = WorkspaceSettingsLayout.EmbeddedPageTopAllocation(
            embedded, paneOpen, view.CurrentPage is ISettingsPageContentInset);
        view.SetHostContentTopInset(allocation.PageTopInset);
        SettingsSection.Margin = embedded
            ? new Thickness(paneOpen ? NavView.OpenPaneLength : 0, allocation.HostMarginTop, 0, 0)
            : new Thickness(0);
        EmbeddedOperationInfo.Margin = new Thickness(0, allocation.PageTopInset, 0, 0);
    }

    private void UpdateTitleBarActions()
    {
        var settingsFooterVisible = SettingsSection.Visibility == Visibility.Visible
            && _settings is { ContentOnly: false };
        var visibility = WorkspaceFooterPlacement.ShowTitleBarActions(
            settingsFooterVisible: settingsFooterVisible,
            chatPaneOpen: NavView.IsPaneOpen) ? Visibility.Visible : Visibility.Collapsed;
        if (TitleBarFooter.Visibility == visibility) return;
        if (visibility == Visibility.Collapsed) TitleBarFooter.HideFlyouts();
        TitleBarFooter.Visibility = visibility;
        // TitleBar refreshes its pointer pass-through regions only on its own size or template
        // changes, not when RightHeader content shows or hides. Recompute after the new layout so
        // real clicks reach Dashboard and the bell instead of dragging the window.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!IsClosed) WorkspaceTitleBar.RecomputeDragRegions();
        });
    }
    private void OnPaneClosed(NavigationView sender, object args)
    {
        if (NavView.IsPaneOpen || IsClosed) return;
        NavView.IsPaneVisible = false;
    }
    internal void NavigateBack()
    {
        if (DeferWhileModalOpen(NavigateBack)) return;
        _sidebarInvoked = false;
        if (IsSettingsVisible && _settings?.TryReturnToDashboardGallery() == true)
            return;
        PruneUnavailableGatewayHistory();
        if (_navigation.GoBack()) RenderDestination();
        else UpdateHistoryButtons();
    }
    private void NavigateForward()
    {
        if (DeferWhileModalOpen(NavigateForward)) return;
        _sidebarInvoked = false;
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
    /// Policy: while a ContentDialog is open on the shared root, destination changes wait for
    /// that dialog to close, then the latest deferred change runs once. Dialogs are never cancelled
    /// by navigation, so app-wide update, failure and approval dialogs keep their own outcome, and no
    /// dialog can be left over a page that did not open it.
    /// </summary>
    private bool DeferWhileModalOpen(Action navigate)
    {
        if (OpenModalDialog() is not { } dialog)
            return false;
        _deferred.Defer(navigate);
        ArmDeferredDrain(dialog);
        return true;
    }

    private void ArmDeferredDrain(ContentDialog dialog)
    {
        if (!_deferred.TryArm()) return;
        dialog.Closed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (IsClosed) return;
            if (!_deferred.Run(() => OpenModalDialog() is not null, RenderPrunedDestination)
                && OpenModalDialog() is { } next && !ReferenceEquals(next, dialog))
                ArmDeferredDrain(next);
        });
    }
    private void OnKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Control)
            .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Menu)
            .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Shift)
            .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);
        // Ctrl+Shift+D opens the Gateway Dashboard from Chat and Settings, matching the footer tooltip.
        // Auto-repeat while the keys are held must not open one browser tab per repeat.
        if (ctrl && shift && !alt && e.Key == global::Windows.System.VirtualKey.D)
        {
            e.Handled = true;
            if (!e.KeyStatus.WasKeyDown) OpenDashboard();
        }
        // Ctrl+E is the search shortcut shown in the placeholder; Ctrl+K remains its alias.
        // Settings keeps its existing F alias; Chat leaves F to its own content.
        else if (ctrl && (e.Key == global::Windows.System.VirtualKey.K ||
                     e.Key == global::Windows.System.VirtualKey.E ||
                     (IsSettingsVisible && e.Key == global::Windows.System.VirtualKey.F)))
        {
            e.Handled = true;
            OpenCommandCenter();
        }
        else if (ctrl && !shift && !alt && e.Key == SettingsShortcutKey)
        {
            // Ctrl+, opens Settings, as shown beside the account menu's Settings row.
            e.Handled = true;
            if (!e.KeyStatus.WasKeyDown) OpenCompanion(CompanionPageId.Settings);
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

    private MenuFlyout BuildOwnerMenu(WorkspacePaneFooter footer)
    {
        var menu = new MenuFlyout();
        var showConnectionStatus = false;
        void RequestConnectionStatus() => showConnectionStatus = true;
        // Rebuilt on every open so the profile, saved Gateways, status and update badge are current.
        menu.Opening += (_, _) =>
        {
            if (IsClosed) return;
            UpdateConnectionStatus(CurrentApp.ConnectionManager?.CurrentSnapshot, _state.Status);
            PopulateOwnerMenu(menu, RequestConnectionStatus);
        };
        menu.Closed += (_, _) =>
        {
            if (!showConnectionStatus || IsClosed) return;
            showConnectionStatus = false;
            _gatewayStatusFlyout.Placement = footer.Layout == WorkspacePaneFooterLayout.Rail
                ? Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.RightEdgeAlignedBottom
                : Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft;
            _gatewayStatusFlyout.ShowAt(footer.OwnerAnchor);
        };
        PopulateOwnerMenu(menu, RequestConnectionStatus);
        return menu;
    }

    /// <summary>
    /// Account menu: profile, Gateway submenu (plus Retry now while offline), Settings with the update
    /// badge, Pair a device, and Help &amp; about. <see cref="WorkspaceAccountMenu"/> owns the projection.
    /// </summary>
    private void PopulateOwnerMenu(MenuFlyout menu, Action requestConnectionStatus)
    {
        var snapshot = CurrentApp.ConnectionManager?.CurrentSnapshot;
        var registry = CurrentApp.Registry;
        var model = WorkspaceAccountMenu.Project(new(
            registry?.GetAll() ?? [], registry?.GetActive()?.Id, snapshot?.OverallState, _state.Status,
            _state.GatewaySelf?.ServerVersion, _state.UpdateInfo,
            WorkspaceAccountMenuInput.IsOperatorConnected(snapshot, _state.Status)));
        var statusLabel = LocalizationHelper.GetString(model.StatusLabelKey);
        var connected = model.OperatorConnected;
        var commands = (IAppCommands)CurrentApp;

        menu.Items.Clear();
        var ownerLabel = OwnerLabel();
        var ownerDetail = WorkspaceAccountMenu.ProfileDetail(ownerLabel, _identity.Email);
        var profile = new AccountMenuItem
        {
            Style = AccountMenuItem.ProfileStyle,
            Text = ownerLabel,
            Description = ownerDetail,
            Picture = _ownerImage,
            PictureName = _identity.DisplayName ?? "",
            IsEnabled = connected,
        };
        AutomationProperties.SetAutomationId(profile, "WorkspaceOwnerProfile");
        AutomationProperties.SetName(profile, ownerLabel);
        if (ownerDetail is not null) AutomationProperties.SetHelpText(profile, ownerDetail);
        profile.Click += (_, _) => OpenDashboard(WorkspaceAccountMenu.ProfileDashboardPath);
        menu.Items.Add(profile);
        menu.Items.Add(new MenuFlyoutSeparator());

        var gatewayDetail = model.ServerVersion is { } version
            ? LocalizationHelper.Format("WorkspaceShell_AccountGatewayVersionFormat", statusLabel, version)
            : statusLabel;
        var gateway = new MenuFlyoutSubItem
        {
            Style = (Style)Application.Current.Resources["AccountMenuSubItemStyle"],
            Text = model.ActiveGatewayName ?? Text("AccountNoGateway"),
            Tag = gatewayDetail,
            Icon = new FontIcon { Style = ChatFooter.BadgeStyle(model.StatusAccent) },
        };
        AutomationProperties.SetAutomationId(gateway, "WorkspaceOwnerGateway");
        AutomationProperties.SetHelpText(gateway, gatewayDetail);
        PopulateGatewayMenu(gateway, model, statusLabel, requestConnectionStatus);
        menu.Items.Add(gateway);
        if (model.ShowRetry)
            menu.Items.Add(MenuItem("AccountRetry", "WorkspaceOwnerRetry", FluentIconCatalog.Refresh, () =>
            {
                if (WorkspaceAccountMenu.NeedsRetry(CurrentApp.ConnectionManager?.CurrentSnapshot?.OverallState, _state.Status))
                    commands.Reconnect();
            }));
        menu.Items.Add(new MenuFlyoutSeparator());

        var settingsLabel = Text("Settings");
        var settings = new AccountMenuItem
        {
            Style = AccountMenuItem.ItemStyle,
            Text = settingsLabel,
            Icon = FluentIconCatalog.Build(FluentIconCatalog.Settings),
            // While an update routes this row to About, Ctrl+, (always Settings) is not its shortcut.
            KeyboardAcceleratorTextOverride = model.SettingsTarget == CompanionPageId.Settings ? SettingsShortcutText : "",
            Badge = model.UpdateAvailable ? Text("AccountUpdateBadge") : null,
        };
        AutomationProperties.SetAutomationId(settings, "WorkspaceOwnerSettings");
        if (model.UpdateAvailable)
            AutomationProperties.SetName(settings,
                LocalizationHelper.Format("WorkspaceShell_AccountSettingsUpdateFormat", settingsLabel));
        settings.Click += (_, _) => OpenCompanion(model.SettingsTarget);
        menu.Items.Add(settings);
        menu.Items.Add(MenuItem("PairDevice", "WorkspaceOwnerPairDevice", FluentIconCatalog.Devices,
            () => OpenDashboard(WorkspaceAccountMenu.PairDeviceDashboardPath)));

        var help = new MenuFlyoutSubItem
        {
            Text = Text("Help"),
            Icon = FluentIconCatalog.Build(FluentIconCatalog.Help),
        };
        AutomationProperties.SetAutomationId(help, "WorkspaceOwnerHelp");
        foreach (var (label, url) in new[]
        {
            ("Documentation", "https://docs.openclaw.ai"),
            ("Support", SupportLinks.OfficialSupportUrl),
            ("Community", "https://openclaw.ai/discord?from=openclaw-windows-node-community"),
            ("ReleaseNotes", "https://docs.openclaw.ai/releases"),
        })
            help.Items.Add(MenuItem(label, null, null, () => _ = OpenLinkAsync(url)));
        var github = new MenuFlyoutItem { Text = LocalizationHelper.GetString("SettingsPage_AppInfoGitHub.Content") };
        github.Click += (_, _) => _ = OpenLinkAsync("https://github.com/openclaw/openclaw-windows-node");
        help.Items.Add(github);
        help.Items.Add(new MenuFlyoutSeparator());
        help.Items.Add(MenuItem("GetApps", null, null, () => _ = OpenLinkAsync("https://docs.openclaw.ai/platforms")));
        help.Items.Add(MenuItem("ConnectionTimeline", null, null, OpenTimeline));
        help.Items.Add(new MenuFlyoutSeparator());
        var about = MenuItem("About", "WorkspaceOwnerAbout", null, () => OpenCompanion(CompanionPageId.About),
            metadata: AppVersionInfo.Version);
        AutomationProperties.SetHelpText(about, AppVersionInfo.Version);
        help.Items.Add(about);
        menu.Items.Add(help);
    }

    private void PopulateGatewayMenu(
        MenuFlyoutSubItem menu, WorkspaceAccountMenuModel model, string statusLabel, Action requestConnectionStatus)
    {
        var commands = (IAppCommands)CurrentApp;
        for (var index = 0; index < model.Gateways.Count; index++)
        {
            var saved = model.Gateways[index];
            // Plain items keep one icon column like the parent menu; a radio check column would add a
            // second empty column before every row. The current Gateway carries its live status dot.
            var item = new AccountMenuItem
            {
                Style = AccountMenuItem.ItemStyle,
                Text = saved.Name,
                Metadata = saved.IsActive ? statusLabel : saved.Host,
            };
            if (saved.IsActive)
            {
                item.Icon = new FontIcon { Style = ChatFooter.BadgeStyle(model.StatusAccent) };
                AutomationProperties.SetItemStatus(item, Text("AccountCurrentGateway"));
            }
            AutomationProperties.SetAutomationId(item, $"WorkspaceOwnerGateway_{index}");
            AutomationProperties.SetHelpText(item, saved.IsActive
                ? LocalizationHelper.Format("WorkspaceShell_AccountCurrentGatewayStatusFormat", statusLabel)
                : saved.Host);
            if (!saved.IsActive)
                item.Click += (_, _) => AsyncEventHandlerGuard.Run(
                    () => SwitchGatewayAsync(saved.Id), new AppLogger(), nameof(PopulateGatewayMenu));
            menu.Items.Add(item);
        }
        if (model.Gateways.Count > 0)
            menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("AccountAddGateway", "WorkspaceOwnerAddGateway", FluentIconCatalog.Add, OpenAddGateway));
        var reconnect = MenuItem("AccountReconnect", "WorkspaceOwnerReconnect", FluentIconCatalog.Refresh, commands.Reconnect);
        reconnect.IsEnabled = model.ActiveGatewayName is not null;
        menu.Items.Add(reconnect);
        var title = LocalizationHelper.GetString("ConnectionStatusWindow.Title");
        var status = new AccountMenuItem
        {
            Style = AccountMenuItem.ItemStyle,
            Text = title,
            Icon = FluentIconCatalog.Build(FluentIconCatalog.About),
            Metadata = statusLabel,
        };
        AutomationProperties.SetAutomationId(status, "WorkspaceOwnerConnectionStatus");
        AutomationProperties.SetName(status, $"{title}: {statusLabel}");
        status.Click += (_, _) => requestConnectionStatus();
        menu.Items.Add(status);
        menu.Items.Add(MenuItem("AccountConnectionSettings", "WorkspaceOwnerConnectionSettings", FluentIconCatalog.Settings,
            () => OpenCompanion(CompanionPageId.Connection)));
        var dashboard = MenuItem("AccountGatewayDashboard", "WorkspaceOwnerGatewayDashboard", FluentIconCatalog.OpenInBrowser,
            () => OpenDashboard(WorkspaceAccountMenu.GatewaySettingsDashboardPath));
        dashboard.IsEnabled = model.ActiveGatewayName is not null;
        menu.Items.Add(dashboard);
    }

    private static MenuFlyoutItem MenuItem(string key, string? automationId, string? glyph, Action action, string? metadata = null)
    {
        MenuFlyoutItem item = metadata is null
            ? new MenuFlyoutItem()
            : new AccountMenuItem { Style = AccountMenuItem.ItemStyle, Metadata = metadata };
        item.Text = Text(key);
        if (glyph is not null) item.Icon = FluentIconCatalog.Build(glyph);
        if (automationId is not null) AutomationProperties.SetAutomationId(item, automationId);
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>Switches through the same manager path as the Connection page's saved-gateway Connect action.</summary>
    private static async Task SwitchGatewayAsync(string gatewayId)
    {
        if (CurrentApp.ConnectionManager is not { } manager) return;
        try
        {
            await manager.SwitchGatewayAsync(gatewayId);
        }
        catch (Exception ex)
        {
            CurrentApp.ShowTransientConnectionError(ex.Message);
        }
    }

    /// <summary>Opens Connection settings with its Add gateway form, once the page is shown.</summary>
    private void OpenAddGateway()
    {
        // Already on Connection: keep the live page (and any form in progress) instead of re-rendering it.
        if (IsSettingsVisible && _settings?.CurrentPage is ConnectionPage current && current.IsLoaded)
        {
            current.BeginAddGateway();
            return;
        }
        OpenCompanion(CompanionPageId.Connection);
        AsyncEventHandlerGuard.Run(async () =>
        {
            if (_settings is not { } settings) return;
            await settings.WaitForCurrentContentReadyAsync();
            if (IsClosed || settings.CurrentPage is not ConnectionPage page) return;
            if (page.IsLoaded) page.BeginAddGateway();
            else
            {
                void Loaded(object sender, RoutedEventArgs e) { page.Loaded -= Loaded; page.BeginAddGateway(); }
                page.Loaded += Loaded;
            }
        }, new AppLogger(), nameof(OpenAddGateway));
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
        WorkspaceSearchFlyout.Hide();
        _search.Dispose();
        _profileAutoFill.ProfileChanged -= OnProfileAutoFilled;
        _identity.SetConnection(null, ConnectionStatus.Disconnected);
        _state.PropertyChanged -= OnStateChanged;
        UnwirePins();
        _inbox.Changed -= OnNotificationsChanged;
        _workspaceSearchShow.Cancel();
        foreach (var footer in Footers)
            footer.Close();
        _gatewayStatusFlyout.Hide();
        ContentHost.Children.Clear();
        _preparedSettings?.Dispose();
        _preparedSettings = null;
        if (_settings is { } settings)
        {
            settings.SettingsSaved -= OnSettingsViewSaved;
            settings.Dispose();
            SettingsSection.Children.Clear();
            _settings = null;
        }
        try { CurrentApp.PageActivator?.Reset(); }
        catch (Exception ex) { Logger.Warn($"[Workspace] Navigation scope reset on close failed: {ex.Message}"); }
        _deferred.Clear();
        _chatFocus = null;
        _chat.CloseSurface();
        _chat.SessionSelected -= OnChatSessionSelected;
    }
}
