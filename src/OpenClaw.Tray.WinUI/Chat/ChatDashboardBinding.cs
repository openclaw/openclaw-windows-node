// Chat-owned dashboard inputs for the conversation side panel. ChatPage keeps one
// instance and passes View to the native chat root:
//   _dashboards ??= new ChatDashboardBinding(DispatcherQueue);
//   _dashboards.Attach(app);              // while the page is active
//   _dashboards.RequestOpen(sessionKey);  // Dashboards page "Open chat" handoff
//   _dashboards.Detach();
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using OpenClaw.Shared;
using OpenClaw.Shared.Dashboards;
using OpenClawTray.Dashboards;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

/// <summary>
/// Binds the embedded <see cref="DashboardsPageState"/> to the operator
/// connection, as the Dashboards page does, and retains one explicit
/// "open beside chat" request until the chat root shows that session.
/// </summary>
internal sealed class ChatDashboardBinding
{
    private App? _app;
    private AppState? _appState;

    internal ChatDashboardBinding(DispatcherQueue dispatcher)
    {
        View = new DashboardsPageState
        {
            Post = dispatcher.AsPost(),
            Schedule = dispatcher.Schedule,
            OpenConnection = () => ((IAppCommands)(App)Microsoft.UI.Xaml.Application.Current).Navigate("connection"),
            OpenWebDashboards = () => ((IAppCommands)(App)Microsoft.UI.Xaml.Application.Current)
                .OpenDashboard(DashboardsRoute.WebGalleryPath),
        };
    }

    internal DashboardsPageState View { get; }

    /// <summary>Session whose dashboard should open beside chat once that session is shown.</summary>
    internal string? PendingOpen { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="PendingOpen"/> is requested.</summary>
    internal event Action? PendingOpenChanged;

    internal void RequestOpen(string sessionKey)
    {
        PendingOpen = sessionKey;
        PendingOpenChanged?.Invoke();
    }

    internal bool TryTakePendingOpen(string? sessionKey)
    {
        if (sessionKey is null || PendingOpen != sessionKey) return false;
        PendingOpen = null;
        return true;
    }

    /// <summary>Selecting a different session abandons an unconsumed request.</summary>
    internal void CancelPendingOpenExcept(string sessionKey)
    {
        if (PendingOpen is not null && PendingOpen != sessionKey) PendingOpen = null;
    }

    /// <summary>Navigation without a dashboard request, or removal of the session, abandons any earlier request.</summary>
    internal void CancelPendingOpen() => PendingOpen = null;

    /// <summary>
    /// Reveals <paramref name="target"/>'s dashboard beside chat when <c>board.get</c>
    /// shows content, and re-checks after <c>board.changed</c> while no tab is shown.
    /// Call from the UI thread; returns the cleanup.
    /// </summary>
    internal Action Watch(ChatConversationPanelState panels, BoardSessionTarget target)
    {
        if (View.Client is not { } client || !View.Connected || !BoardAccessPolicy.CanRead(client))
            return static () => { };
        var current = true;
        var probing = false;
        var stale = false;
        void Probe()
        {
            if (!current || !panels.CanRevealDashboard(target.SessionKey)) return;
            if (probing)
            {
                // A change during an in-flight read may postdate its snapshot.
                stale = true;
                return;
            }
            probing = true;
            stale = false;
            _ = ProbeAsync();
        }
        async Task ProbeAsync()
        {
            var hasContent = false;
            try
            {
                hasContent = HasContent(await client.GetBoardAsync(target).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                Logger.Debug($"[ChatDashboard] board probe failed: {ex.GetType().Name}");
            }
            View.Post(() =>
            {
                probing = false;
                if (current && hasContent) panels.RevealDashboard(target);
                else if (stale) Probe();
            });
        }
        void OnBoardChanged(object? sender, BoardChangedEvent e)
        {
            if (string.Equals(e.SessionKey, target.SessionKey, StringComparison.Ordinal)) View.Post(Probe);
        }
        client.BoardChanged += OnBoardChanged;
        Probe();
        return () =>
        {
            current = false;
            client.BoardChanged -= OnBoardChanged;
        };
    }

    internal static bool HasContent(BoardSnapshot board) =>
        !board.Tabs.IsDefaultOrEmpty || !board.Widgets.IsDefaultOrEmpty;

    internal void Attach(App app)
    {
        if (!ReferenceEquals(_app, app))
        {
            Detach();
            _app = app;
            _appState = app.AppState;
            if (_appState is not null)
                _appState.PropertyChanged += OnAppStateChanged;
        }
        BindConnection();
    }

    internal void Detach()
    {
        if (_appState is not null)
            _appState.PropertyChanged -= OnAppStateChanged;
        _appState = null;
        _app = null;
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.Status))
            View.Post(BindConnection);
    }

    private void BindConnection()
    {
        if (_app is not { } app) return;
        var client = app.GatewayClient as IBoardGatewayClient;
        View.SetConnection(client, client?.IsConnectedToGateway == true && _appState?.Status == ConnectionStatus.Connected);
    }
}
