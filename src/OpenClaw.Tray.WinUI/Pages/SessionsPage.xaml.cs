using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Services;
using OpenClawTray.Sessions;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Pages;

/// <summary>
/// Settings Sessions page host. Mounts the Reactor <see cref="SessionsHubRoot"/>
/// once and forwards navigation, connection, and agent-roster changes to its
/// <see cref="SessionsHubController"/>. Routes: <c>sessions</c> keeps the user's
/// agent choice; <c>agent:{id}:sessions</c> scopes the roster to that agent.
/// </summary>
public sealed partial class SessionsPage : Page, ISessionsHubHost
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private SessionsHubController? _controller;
    private ReactorHostControl? _reactorHost;
    private AppState? _appState;

    public SessionsPage()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            _controller?.Deactivate();
            if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
            _appState = null;
        };
    }

    public void Initialize() => Initialize(agentId: null);

    public void Initialize(string? agentId)
    {
        if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
        _appState = CurrentApp.AppState;
        if (_appState != null) _appState.PropertyChanged += OnAppStateChanged;
        var controller = EnsureMounted();

        // The real-process accessibility suite has no gateway. Give it an
        // isolated, deterministic duplicate-name scenario so UI Automation can
        // prove both the rendered titles and the row-to-chat key hand-off.
        // Requiring the test data directory as well as the explicit flag keeps
        // this path unreachable from a normal app launch.
        if (_appState is { } appState
            && Environment.GetEnvironmentVariable("OPENCLAW_ACCESSIBILITY_TEST_SESSIONS") == "1"
            && Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR") is { Length: > 0 })
        {
        if (Environment.GetEnvironmentVariable("OPENCLAW_ACCESSIBILITY_TEST_AGENT_IDENTITIES") == "1")
        {
            using var agents = System.Text.Json.JsonDocument.Parse("""
                {"defaultId":"main","mainKey":"main","agents":[
                  {"id":"main","name":"Roster alias","identity":{"name":"Configured assistant","emoji":"C","avatarUrl":"data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20width='32'%20height='32'%3E%3Crect%20width='32'%20height='32'%20fill='teal'/%3E%3Ccircle%20cx='16'%20cy='16'%20r='8'%20fill='white'/%3E%3C/svg%3E"}},
                  {"id":"research","identity":{"name":"Research assistant","emoji":"\ud83d\udd2c"}},
                  {"id":"fallback","name":"No configured icon"}
                ]}
                """);
            appState.AgentsList = agents.RootElement.Clone();
        }
        // Use the same test-only snapshot for the companion and Workspace sidebar.
        appState.Sessions =
        [
            new SessionInfo
            {
                Key = "agent:main:main",
                IsMain = true,
                Status = "active",
                HasActiveRun = true,
                DisplayName = "OpenClaw Windows Tray",
                UpdatedAt = DateTime.UtcNow,
            },
            new SessionInfo
            {
                Key = "agent:main:fork",
                Status = "running",
                HasActiveRun = false,
                DisplayName = "OpenClaw Windows Tray",
                UpdatedAt = DateTime.UtcNow.AddSeconds(-1),
            },
            new SessionInfo
            {
                Key = "agent:main:deploy-migration",
                Status = "failed",
                DisplayName = "Deploy migration",
                UpdatedAt = DateTime.UtcNow.AddMinutes(-2),
            },
            new SessionInfo
            {
                Key = "agent:main:research-labels",
                Status = "timeout",
                DisplayName = "Research status labels",
                UpdatedAt = DateTime.UtcNow.AddMinutes(-4),
            },
            new SessionInfo
            {
                Key = "agent:main:release-notes",
                Status = "killed",
                DisplayName = "Draft release notes",
                UpdatedAt = DateTime.UtcNow.AddMinutes(-6),
            },
            new SessionInfo
            {
                Key = "agent:main:cron:nightly-cleanup",
                Status = "active",
                HasActiveRun = true,
                DisplayName = "Nightly cleanup",
                Classification = "cron",
                IsBackground = true,
                UpdatedAt = DateTime.UtcNow.AddMinutes(-7),
            },
            new SessionInfo
            {
                Key = "agent:main:completed-cleanup",
                Status = "done",
                DisplayName = "Completed cleanup",
                UpdatedAt = DateTime.UtcNow.AddMinutes(-8),
            },
        ];

            controller.Activate(agentId);
            controller.UseSnapshot(appState.Sessions);
            return;
        }

        controller.Activate(agentId);
    }

    private SessionsHubController EnsureMounted()
    {
        if (_controller is not null)
            return _controller;
        _controller = new SessionsHubController(this, action =>
        {
            if (!DispatcherQueue.TryEnqueue(() => action()))
                new AppLogger().Warn("[SessionsPage] Dropped a UI update because the dispatcher rejected it.");
        });
        var controller = _controller;
        _reactorHost = new ReactorHostControl();
        _reactorHost.Mount(_ => Component<SessionsHubRoot, SessionsHubRootProps>(new SessionsHubRootProps(controller)));
        HubHost.Child = _reactorHost;
        return controller;
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.Status):
                _controller?.RefreshConnection();
                break;
            case nameof(AppState.AgentsList):
                _controller?.RefreshAgents();
                break;
        }
    }

    XamlRoot? ISessionsHubHost.XamlRoot => XamlRoot;

    IntPtr ISessionsHubHost.WindowHandle
    {
        get
        {
            var window = CurrentApp.ActiveHubWindow;
            if (window == null) return IntPtr.Zero;
            try { return WinRT.Interop.WindowNative.GetWindowHandle(window); }
            catch (Exception) { return IntPtr.Zero; }
        }
    }

    IOperatorGatewayClient? ISessionsHubHost.Client => CurrentApp.GatewayClient;

    IReadOnlyList<SessionInfo> ISessionsHubHost.TrackedSessions => _appState?.Sessions ?? Array.Empty<SessionInfo>();

    JsonElement? ISessionsHubHost.AgentsList => _appState?.AgentsList;

    bool ISessionsHubHost.ShowCompletedSessions
    {
        get
        {
            try { return CurrentApp.Settings.ShowCompletedSessions; }
            catch (InvalidOperationException) { return false; }
        }
        set
        {
            try
            {
                CurrentApp.Settings.ShowCompletedSessions = value;
                CurrentApp.Settings.Save();
            }
            catch (InvalidOperationException) { }
        }
    }

    void ISessionsHubHost.OpenChat(string sessionKey)
    {
        CurrentApp.PendingChatSessionKey = sessionKey;
        ((IAppCommands)CurrentApp).Navigate("chat");
    }

    void ISessionsHubHost.OpenConnection() => ((IAppCommands)CurrentApp).Navigate("connection");
}
