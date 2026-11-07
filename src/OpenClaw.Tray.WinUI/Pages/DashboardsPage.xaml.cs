using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClaw.Shared.Dashboards;
using OpenClawTray.Chat;
using OpenClawTray.Controls;
using OpenClawTray.Dashboards;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Services;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Pages;

/// <summary>
/// WinUI applicator for the native Dashboards surface. It owns the Reactor
/// host and feeds <see cref="DashboardsPageState"/> from the hub (agent scope,
/// route) and the app (operator connection). All presentation is Reactor.
/// </summary>
public sealed partial class DashboardsPage : Page, ISettingsPageContentInset
{
    private static App CurrentApp => (App)Application.Current!;

    private DashboardsPageState? _state;
    private ReactorHostControl? _host;
    private AppState? _appState;
    private SettingsShellView? _hub;
    private IDisposable? _copyNotice;
    private double _topInset;
    private static readonly TimeSpan s_copyNoticeDuration = TimeSpan.FromSeconds(4);

    public DashboardsPage()
    {
        InitializeComponent();
        Unloaded += (_, _) => Detach();
        ActualThemeChanged += (_, _) => _state?.SetTheme(ActualTheme == ElementTheme.Dark);
    }

    void ISettingsPageContentInset.ApplyTopInset(double topInset)
    {
        _topInset = topInset;
        _state?.SetTopInset(topInset);
    }

    internal void Initialize(SettingsShellView hub, string? tag)
    {
        _state ??= new DashboardsPageState
        {
            Post = DispatcherQueue.AsPost(),
            Schedule = Schedule,
            OpenChat = sessionKey =>
            {
                CurrentApp.PendingChatSessionKey = sessionKey;
                ((IAppCommands)CurrentApp).Navigate("chat");
            },
            OpenConnection = () => ((IAppCommands)CurrentApp).Navigate("connection"),
            OpenAgentChat = _ => ((IAppCommands)CurrentApp).Navigate("chat"),
            OpenWebDashboards = () => ((IAppCommands)CurrentApp).OpenDashboard(DashboardsRoute.WebGalleryPath),
            CopySessionKey = CopySessionKey,
        };
        _state.SetTopInset(_topInset);
        _hub = hub;
        _state.SetTheme(ActualTheme == ElementTheme.Dark);
        UpdateAgentNames();
        _state.Changed -= OnPageStateChanged;
        _state.Changed += OnPageStateChanged;
        _state.ApplyNavigation(hub.GetAgentIds(), hub.CurrentAgentId, DashboardsRoute.TryParseSession(tag));

        if (_appState is null)
        {
            _appState = CurrentApp.AppState;
            if (_appState is not null)
                _appState.PropertyChanged += OnAppStateChanged;
        }

        BindConnection();

        if (_host is null)
        {
            var state = _state;
            _host = new ReactorHostControl();
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_host, "DashboardsContentHost");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_host, LocalizationHelper.GetString("Dashboards_Title"));
            _host.SizeChanged += (_, args) => state.SetViewportWidth(args.NewSize.Width);
            _host.Mount(_ => Component<ReactorDashboardsRoot, DashboardsRootProps>(new DashboardsRootProps(state)));
            ReactorTarget.Child = _host;
        }
    }

    internal bool HasOpenBoard => _state?.OpenTarget is not null;

    internal bool TryReturnToGallery()
    {
        if (!HasOpenBoard)
            return false;
        _state!.Open(null);
        return true;
    }

    private void OnPageStateChanged() => _hub?.RefreshPageNavigation();

    private void CopySessionKey(string key)
    {
        var copied = ClipboardHelper.TryCopyText(key);
        _state?.ReportCopyResult(copied);
        _copyNotice?.Dispose();
        _copyNotice = Schedule(s_copyNoticeDuration, () => _state?.ReportCopyResult(null));
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.Status))
            DispatcherQueue.TryEnqueue(BindConnection);
        else if (e.PropertyName == nameof(AppState.AgentsList))
            DispatcherQueue.TryEnqueue(RefreshAgents);
    }

    /// <summary>Follows agent-list updates while keeping the user's current agent selected.</summary>
    private void RefreshAgents()
    {
        if (_state is not null && _hub is not null)
        {
            UpdateAgentNames();
            _state.SetAgents(_hub.GetAgentIds(), _state.AgentId);
        }
    }

    private void UpdateAgentNames()
    {
        if (_state is null || _hub?.LastAgentsData is not { } data ||
            !data.TryGetProperty("agents", out var agents) || agents.ValueKind != System.Text.Json.JsonValueKind.Array)
            return;
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var agent in agents.EnumerateArray())
        {
            if (agent.TryGetProperty("id", out var id) && id.GetString() is { } key &&
                agent.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } label)
                names[key] = label;
        }
        _state.AgentNames = names;
    }

    private void BindConnection()
    {
        if (_state is null)
            return;
        var client = CurrentApp.GatewayClient as IBoardGatewayClient;
        _state.SetConnection(client, client?.IsConnectedToGateway == true && _appState?.Status == ConnectionStatus.Connected);
    }

    private IDisposable Schedule(TimeSpan delay, Action action)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = delay;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => action();
        timer.Start();
        return new TimerLease(timer);
    }

    private void Detach()
    {
        _copyNotice?.Dispose();
        _copyNotice = null;
        if (_state is not null)
            _state.Changed -= OnPageStateChanged;
        if (_appState is not null)
            _appState.PropertyChanged -= OnAppStateChanged;
        _appState = null;
        if (_host is not null)
        {
            if (ReferenceEquals(ReactorTarget.Child, _host))
                ReactorTarget.Child = null;
            _host.Dispose();
            _host = null;
        }
        _state = null;
        _hub = null;
    }

    private sealed class TimerLease(DispatcherQueueTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}
