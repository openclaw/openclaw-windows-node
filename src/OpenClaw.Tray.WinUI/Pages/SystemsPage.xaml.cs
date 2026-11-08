// Connects the Settings frame to Systems state and actions. Usage: navigate to "systems" or "systems:<environment-id>".
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.Shared.Systems;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Systems;
using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Pages;

/// <summary>
/// Systems page (formerly Instances). Hosts the Reactor <see cref="SystemsPageView"/>,
/// feeds it app state and Gateway events through <see cref="SystemsController"/>, and owns
/// the node rename/forget operations its confirmation dialogs run. Pairing decisions stay on
/// the Connection page and web handoffs use the app's dashboard launcher.
/// </summary>
public sealed partial class SystemsPage : Page, IDisposable, Presentation.ISettingsPageContentInset
{
    private static App CurrentApp => (App)Application.Current!;
    private readonly SystemsController _controller;
    private readonly SystemsViewport _viewport = new();
    private ReactorHostControl? _host;
    private AppState? _appState;
    private IOperatorGatewayClient? _client;
    private PairingApprovalCoordinator? _approvals;
    private DispatcherQueueTimer? _timer;
    private string? _requestedMachine;
    private bool _connected;
    private string? _gatewayId;
    private bool _disposed;

    public SystemsPage()
    {
        InitializeComponent();
        _controller = new SystemsController(() => CurrentApp.GatewayClient is { } client ? SystemsGatewayClient.For(client) : null);
        Unloaded += (_, _) => Teardown();
    }

    void Presentation.ISettingsPageContentInset.ApplyTopInset(double topInset) =>
        _viewport.ApplyTopInset(topInset);

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // systems:<environment id> opens the page with that machine selected. Frame.Navigated
        // (which runs Initialize) fires before OnNavigatedTo, so select here as well.
        _requestedMachine = Presentation.HubPageRegistry.SystemsMachine(e.Parameter as string);
        if (_requestedMachine is { } machine)
            _controller.Select(machine);
    }

    /// <summary>
    /// Systems pages are not cached, so leaving the frame ends this instance. Reactor keeps its tree
    /// across Unloaded (which also fires on reparenting), so the final cleanup runs here and when
    /// the owning Settings surface is deactivated or disposed (see <see cref="Dispose"/>).
    /// </summary>
    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Dispose();
    }

    /// <summary>Final, idempotent cleanup of the Reactor tree, controller and subscriptions.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Teardown();
        if (_host is { } host)
        {
            _host = null;
            if (ReferenceEquals(ReactorHost.Child, host))
                ReactorHost.Child = null;
            host.Dispose();
        }
        _controller.Dispose();
    }

    /// <summary>Called by SettingsShellView when this page becomes the navigation target.</summary>
    public void Initialize()
    {
        if (_disposed)
            return;
        Teardown();
        _appState = CurrentApp.AppState;
        if (_appState is not null)
            _appState.PropertyChanged += OnAppStateChanged;
        _approvals = CurrentApp.PairingApprovals;
        if (_approvals is not null)
            _approvals.ApprovalsChanged += OnApprovalsChanged;
        AttachClient(CurrentApp.GatewayClient);

        if (_host is null)
        {
            _host = new ReactorHostControl
            {
                // The page owns its scrolling regions, so the Reactor root must fill the page.
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
            };
            var props = new SystemsPageProps(_controller, CreateActions()) { Viewport = _viewport };
            _host.Mount(_ => Component<SystemsPageView, SystemsPageProps>(props));
            ReactorHost.Child = _host;
        }

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = SystemsController.PollInterval;
        _timer.Tick += (_, _) => _ = _controller.TickAsync();
        _timer.Start();

        // The controller starts disconnected, so the first connected input starts the inventory load.
        _connected = false;
        PushInputs();
        if (_requestedMachine is { } machine)
            _controller.Select(machine);
        if (_appState?.Nodes is { } nodes)
            _controller.UpdateNodes(nodes);
    }

    /// <summary>Sessions and pairings the app clears on disconnect; reloaded on each connection.</summary>
    private async Task RefreshAuxiliaryAsync()
    {
        if (CurrentApp.GatewayClient is not { } client)
            return;
        try
        {
            await client.RequestSessionsAsync();
            await (_approvals?.RefreshFromGatewayAsync() ?? Task.CompletedTask);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[SystemsPage] Auxiliary refresh failed: {ex.Message}");
        }
    }

    private void Teardown()
    {
        _timer?.Stop();
        _timer = null;
        if (_appState is not null)
            _appState.PropertyChanged -= OnAppStateChanged;
        _appState = null;
        if (_approvals is not null)
            _approvals.ApprovalsChanged -= OnApprovalsChanged;
        _approvals = null;
        AttachClient(null);
    }

    private void AttachClient(IOperatorGatewayClient? client)
    {
        if (ReferenceEquals(client, _client))
            return;
        if (_client is not null)
        {
            _client.SystemsInventoryChanged -= OnInventoryChanged;
            _client.NodeHostStatsReceived -= OnHostStats;
        }
        _client = client;
        if (client is not null)
        {
            client.SystemsInventoryChanged += OnInventoryChanged;
            client.NodeHostStatsReceived += OnHostStats;
        }
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e) => OnUi(() =>
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.Nodes):
                if (_appState?.Nodes is { } nodes)
                    _controller.UpdateNodes(nodes);
                break;
            case nameof(AppState.Status):
                AttachClient(CurrentApp.GatewayClient);
                PushInputs();
                break;
            case nameof(AppState.Presence):
            case nameof(AppState.Sessions):
            case nameof(AppState.NodePairList):
            case nameof(AppState.DevicePairList):
                PushInputs();
                break;
        }
    });

    private void OnApprovalsChanged(object? sender, EventArgs e) => OnUi(PushInputs);

    private void OnInventoryChanged(object? sender, string eventName) => OnUi(_controller.RequestRefresh);

    private void OnHostStats(object? sender, NodeHostStatsEvent update) =>
        OnUi(() => _controller.ApplyHostStats(update));

    private void PushInputs()
    {
        if (_disposed)
            return;
        var client = CurrentApp.GatewayClient;
        var connected = _appState?.Status == ConnectionStatus.Connected && client is { IsConnectedToGateway: true, HasHandshakeSnapshot: true };
        var gatewayId = CurrentApp.Registry?.GetActive()?.Id;
        var reconnected = connected && (!_connected || !string.Equals(gatewayId, _gatewayId, StringComparison.Ordinal));
        _connected = connected;
        _gatewayId = gatewayId;
        _controller.UpdateInputs(new SystemsInputs
        {
            Connected = connected,
            GatewayId = gatewayId,
            GatewayName = CurrentApp.Registry?.GetActive()?.FriendlyName,
            Scopes = client?.GrantedOperatorScopes ?? Array.Empty<string>(),
            Presence = _appState?.Presence ?? Array.Empty<PresenceEntry>(),
            Sessions = _appState?.Sessions ?? Array.Empty<SessionInfo>(),
            PendingApprovals = _approvals?.Current ?? Array.Empty<PendingApproval>(),
            NodePairList = _appState?.NodePairList,
            OwnNodeIds = CurrentApp.OwnNodeIds,
            LocalNodeId = CurrentApp.NodeFullDeviceId,
            LocalHost = Environment.MachineName,
        });
        if (reconnected)
            _ = RefreshAuxiliaryAsync();
    }

    private void OnUi(Action action)
    {
        if (DispatcherQueue.HasThreadAccess)
            action();
        else
            DispatcherQueue.TryEnqueue(() => action());
    }

    private SystemsPageActions CreateActions() => new()
    {
        OpenSession = key =>
        {
            CurrentApp.PendingChatSessionKey = key;
            ((IAppCommands)CurrentApp).Navigate("chat");
        },
        Navigate = tag => ((IAppCommands)CurrentApp).Navigate(tag),
        CopyText = text => ClipboardHelper.CopyText(text),
        // The launcher owns tunnel, credential and browser failures; device tokens never enter the URL.
        OpenWeb = path => ((IAppCommands)CurrentApp).OpenDashboard(path),
        PostUi = action => DispatcherQueue.TryEnqueue(() => { if (!_disposed) action(); }),
        RenameNode = RenameNodeAsync,
        ForgetNode = ForgetNodeAsync,
    };

    /// <summary>The live client, or the reason a node management request must not be sent.</summary>
    private static (IOperatorGatewayClient? Client, string? Error) ManagementClient()
    {
        if (CurrentApp.GatewayClient is not { IsConnectedToGateway: true } client)
            return (null, SystemsText.S("Disconnected_Title"));
        // Scopes can change after the dialog opened, so check again before sending.
        return SystemsController.HasScope(client.GrantedOperatorScopes, "operator.pairing", legacyDefault: true)
            ? (client, null)
            : (null, SystemsText.S("Manage_PairingRequired"));
    }

    private async Task<string?> RenameNodeAsync(GatewayNodeInfo node, string name)
    {
        var (client, error) = ManagementClient();
        if (client is null)
            return error;
        var result = await client.NodeRenameAsync(node.NodeId, name);
        if (!result.Success)
            return result.ErrorMessage ?? LocalizationHelper.GetString("NodesPage_Rename_Error_Generic");
        // The Gateway does not broadcast renames. Reload node.list for the app and the inventory,
        // whose environment label is the name the page shows.
        _ = client.RequestNodesAsync();
        _ = _controller.RefreshAsync();
        return null;
    }

    private static async Task<string?> ForgetNodeAsync(GatewayNodeInfo node)
    {
        var (client, error) = ManagementClient();
        if (client is null)
            return error;
        var result = await client.NodePairRemoveAsync(node.NodeId);
        // On success the Gateway's node.pair.resolved broadcast refreshes the inventory.
        return result.Success ? null : result.ErrorMessage ?? LocalizationHelper.GetString("NodesPage_Forget_Error_Generic");
    }
}
