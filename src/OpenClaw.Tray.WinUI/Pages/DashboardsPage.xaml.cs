using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClaw.Shared.Dashboards;
using OpenClawTray.Chat;
using OpenClawTray.Dashboards;
using OpenClawTray.Presentation;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Pages;

/// <summary>
/// WinUI applicator for the native Dashboards surface. It owns the Reactor
/// host and feeds <see cref="DashboardsPageState"/> from the hub (agent scope,
/// route) and the app (operator connection). All presentation is Reactor.
/// </summary>
public sealed partial class DashboardsPage : Page
{
    private static App CurrentApp => (App)Application.Current!;

    private DashboardsPageState? _state;
    private ReactorHostControl? _host;
    private AppState? _appState;
    private HubWindow? _hub;

    public DashboardsPage()
    {
        InitializeComponent();
        Unloaded += (_, _) => Detach();
    }

    internal void Initialize(HubWindow hub, string? tag)
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
        };
        _hub = hub;
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
            _host.Mount(_ => Component<ReactorDashboardsRoot, DashboardsRootProps>(new DashboardsRootProps(state)));
            ReactorTarget.Child = _host;
        }
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
            _state.SetAgents(_hub.GetAgentIds(), _state.AgentId);
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
