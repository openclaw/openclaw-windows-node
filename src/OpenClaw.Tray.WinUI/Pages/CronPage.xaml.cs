using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClaw.Shared.Cron;
using OpenClawTray.Automations;
using OpenClawTray.Chat;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.ComponentModel;
using System.Text.Json;
using System.Threading.Tasks;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Pages;

/// <summary>
/// WinUI host for the native Automations surface. Owns the agent-scoped
/// <see cref="AutomationsStore"/>, gateway event wiring and modal confirmation;
/// all presentation lives in the Reactor tree under <c>Automations\</c>.
/// </summary>
public sealed partial class CronPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;

    private AppState? _appState;
    private AutomationsStore? _store;
    private ReactorHostControl? _host;
    private IOperatorGatewayClient? _subscribedClient;
    private string? _storeAgentId;

    /// <summary>Agent from the <c>agent:{id}:cron</c> route; set by the hub before <see cref="Initialize"/>.</summary>
    public string AgentId { get; set; } = "main";

    public CronPage()
    {
        InitializeComponent();
        Unloaded += (_, _) => Teardown();
    }

    public void Initialize()
    {
        var agentId = string.IsNullOrWhiteSpace(AgentId) ? "main" : AgentId.Trim();
        if (_store is null || !string.Equals(_storeAgentId, agentId, StringComparison.Ordinal))
            Mount(agentId);

        if (_appState is null)
        {
            _appState = CurrentApp.AppState;
            if (_appState is not null)
                _appState.PropertyChanged += OnAppStateChanged;
        }
        SubscribeClient();
        _store!.Refresh();
    }

    private void Mount(string agentId)
    {
        DisposeStore();
        _storeAgentId = agentId;
        var dispatcher = DispatcherQueue;
        _store = new AutomationsStore(
            agentId,
            () => CurrentApp.GatewayClient,
            action =>
            {
                if (dispatcher.HasThreadAccess) action();
                else dispatcher.TryEnqueue(() => action());
            });
        _store.NoticeRaised += OnNoticeRaised;

        var host = new AutomationsHost(
            _store,
            new AutomationsText(LocalizationHelper.GetString),
            () => CurrentApp.GatewayClient,
            () => ((IAppCommands)CurrentApp).Navigate("connection"),
            OpenChat);
        _host?.Dispose();
        _host = new ReactorHostControl();
        _host.Mount(_ => Component<AutomationsRoot, AutomationsRootProps>(new AutomationsRootProps(host)));
        ReactorTarget.Child = _host;
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppState.Status))
            return;
        SubscribeClient();
        _store?.Refresh();
    }

    private void SubscribeClient()
    {
        var client = CurrentApp.GatewayClient;
        if (ReferenceEquals(client, _subscribedClient))
            return;
        if (_subscribedClient is not null)
            _subscribedClient.CronEventReceived -= OnCronEvent;
        _subscribedClient = client;
        if (client is not null)
            client.CronEventReceived += OnCronEvent;
    }

    private void OnCronEvent(object? sender, JsonElement payload) =>
        DispatcherQueue?.TryEnqueue(() => _store?.OnGatewayCronEvent());

    /// <summary>Completed one-shot jobs and failed runs are mirrored to the app notification center.</summary>
    private void OnNoticeRaised(object? sender, AutomationsNotice notice)
    {
        if (notice.Kind is not (AutomationsNoticeKind.JobCompleted or AutomationsNoticeKind.RunFailed or AutomationsNoticeKind.RunNotStarted))
            return;
        if (notice.Kind == AutomationsNoticeKind.RunNotStarted && !notice.IsError)
            return;
        var text = new AutomationsText(LocalizationHelper.GetString);
        var (title, message) = text.Notice(notice);
        AppNotificationPublisher.Show(
            CurrentApp.AppNotifications,
            title,
            message,
            "cron",
            "jobs",
            notice.Kind == AutomationsNoticeKind.JobCompleted ? AppNotificationSeverity.Success : AppNotificationSeverity.Error,
            $"cron:{notice.Kind}:{notice.JobName}",
            "cron",
            LocalizationHelper.GetString("AppNotification_ActionOpenCron"));
    }

    private static void OpenChat(string sessionKey)
    {
        if (string.IsNullOrWhiteSpace(sessionKey))
            return;
        CurrentApp.PendingChatSessionKey = sessionKey;
        ((IAppCommands)CurrentApp).Navigate("chat");
    }

    private void DisposeStore()
    {
        if (_store is null)
            return;
        _store.NoticeRaised -= OnNoticeRaised;
        _store.Dispose();
        _store = null;
    }

    private void Teardown()
    {
        if (_appState is not null)
        {
            _appState.PropertyChanged -= OnAppStateChanged;
            _appState = null;
        }
        if (_subscribedClient is not null)
        {
            _subscribedClient.CronEventReceived -= OnCronEvent;
            _subscribedClient = null;
        }
        DisposeStore();
        _storeAgentId = null;
        if (_host is not null)
        {
            if (ReferenceEquals(ReactorTarget.Child, _host))
                ReactorTarget.Child = null;
            _host.Dispose();
            _host = null;
        }
    }
}
