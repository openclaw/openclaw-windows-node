using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClaw.Shared.Cron;
using OpenClawTray.Automations;
using OpenClawTray.Chat;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;
using System.Threading.Tasks;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Pages;

/// <summary>
/// WinUI host for the native Automations surface. Owns the <see cref="AutomationsStore"/>
/// (initially filtered to the route agent), scoped gateway event wiring and app handoffs.
/// Borrows the host-owned global <see cref="AutomationsOverviewSource"/> without disposing it;
/// all presentation lives in the Reactor tree under <c>Automations\</c>.
/// </summary>
public sealed partial class CronPage : Page, ISettingsPageContentInset
{
    private const string SearchUnavailableMarker = "[Search] automation-destination-unavailable";

    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;

    private AppState? _appState;
    private AutomationsStore? _store;
    private AutomationsRevealChannel? _reveals;
    private ReactorHostControl? _host;
    private IOperatorGatewayClient? _subscribedClient;
    private string? _storeAgentId;
    private readonly AutomationsViewportState _viewport = new();

    void ISettingsPageContentInset.ApplyTopInset(double topInset) => _viewport.ApplyTopInset(topInset);

    /// <summary>Agent from the <c>agent:{id}:cron</c> route; set by the Settings shell before <see cref="Initialize"/>.</summary>
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
            },
            overviewSource: CurrentApp.AutomationsOverview);

        var host = new AutomationsHost(
            _store,
            new AutomationsText(LocalizationHelper.GetString),
            () => CurrentApp.GatewayClient,
            () => ((IAppCommands)CurrentApp).Navigate("connection"),
            OpenChat,
            // The shared Dashboard launcher owns tunnel, credential export and visible failure handling.
            jobId => ((IAppCommands)CurrentApp).OpenDashboard(CronProtocol.WebPath(jobId)),
            text => ClipboardHelper.TryCopyText(text),
            () => CurrentApp.AppState?.GetAgentIds() ?? new List<string>())
        {
            GatewayDisplayName = ConnectedGatewayDisplayName,
            ActualTheme = () => ActualTheme,
            Viewport = _viewport,
            DraftScope = () => $"{CurrentApp.ConnectionManager?.CurrentSnapshot.GatewayId
                ?? CurrentApp.ConnectionManager?.CurrentSnapshot.GatewayUrl ?? "offline"}:{agentId}",
        };
        _reveals = host.Reveals;
        _host?.Dispose();
        _host = new ReactorHostControl();
        _host.Mount(_ => Component<AutomationsRoot, AutomationsRootProps>(new AutomationsRootProps(host)));
        ReactorTarget.Child = _host;
    }

    /// <summary>
    /// Hub search handoff. Shows one automation in the full inventory, switching to All agents when it
    /// belongs to another agent. An automation that is gone raises the page's own notice; this only logs.
    /// </summary>
    internal void RevealSearchJob(string jobId)
    {
        if (_reveals is not { } reveals)
            return;
        AsyncEventHandlerGuard.Run(async () =>
        {
            var outcome = await reveals.RevealAsync(jobId);
            if (outcome is AutomationRevealOutcome.NotFound or AutomationRevealOutcome.Failed)
                Logger.Warn(SearchUnavailableMarker);
        }, new AppLogger(), nameof(RevealSearchJob));
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
        DispatcherQueue?.TryEnqueue(() => _store?.OnGatewayCronEvent(globalOverviewHandled: true));

    private static void OpenChat(string sessionKey)
    {
        if (string.IsNullOrWhiteSpace(sessionKey))
            return;
        CurrentApp.PendingChatSessionKey = sessionKey;
        ((IAppCommands)CurrentApp).Navigate("chat");
    }

    private static string? ConnectedGatewayDisplayName()
    {
        var snapshot = CurrentApp.ConnectionManager?.CurrentSnapshot;
        if (!string.IsNullOrWhiteSpace(snapshot?.GatewayName))
            return snapshot.GatewayName;
        return Uri.TryCreate(snapshot?.GatewayUrl, UriKind.Absolute, out var endpoint)
            ? $"{endpoint.Scheme}://{endpoint.Authority}" : null;
    }

    private void DisposeStore()
    {
        _reveals?.Dispose();
        _reveals = null;
        if (_store is null)
            return;
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
