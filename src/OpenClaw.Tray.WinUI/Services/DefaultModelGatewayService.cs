using System.ComponentModel;
using System.Text.Json;
using OpenClaw.Chat;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Presentation;

namespace OpenClawTray.Services;

/// <summary>
/// Projects the operator connection's cached models list, agents list and config into the
/// Default model page. Writes go through <see cref="BeginWrite"/>, which binds them to the
/// Gateway connection the change was admitted for (<see cref="DefaultModelGatewayWrite"/>).
/// Local AI route ownership stays with the Local AI runtime.
/// </summary>
internal sealed class DefaultModelGatewayService : IDefaultModelGatewayService, IDisposable
{
    private readonly AppState _appState;
    private readonly Func<IOperatorGatewayClient?> _getClient;
    private readonly Func<GatewayRegistry?> _getRegistry;
    private readonly Func<string?> _getConnectedGatewayId;
    private readonly LocalAiGatewayLifecycle _localAiLifecycle;

    public DefaultModelGatewayService(
        AppState appState,
        Func<IOperatorGatewayClient?> getClient,
        Func<GatewayRegistry?> getRegistry,
        Func<string?> getConnectedGatewayId,
        LocalAiGatewayLifecycle localAiLifecycle)
    {
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
        _getClient = getClient ?? throw new ArgumentNullException(nameof(getClient));
        _getRegistry = getRegistry ?? throw new ArgumentNullException(nameof(getRegistry));
        _getConnectedGatewayId = getConnectedGatewayId ?? throw new ArgumentNullException(nameof(getConnectedGatewayId));
        _localAiLifecycle = localAiLifecycle ?? throw new ArgumentNullException(nameof(localAiLifecycle));
        _appState.PropertyChanged += OnAppStateChanged;
    }

    public event EventHandler? Changed;

    public DefaultModelGatewayState Current
    {
        get
        {
            IOperatorGatewayClient? client = _getClient();
            bool connected = client?.IsConnectedToGateway == true;
            string? primary = null;
            string? agentModel = null;
            if (_appState.Config is { } envelope && GatewayConfigEnvelope.TryRead(envelope, out JsonElement config, out _))
            {
                primary = GatewayConfigEnvelope.ReadPrimaryModel(config);
                agentModel = GatewayConfigEnvelope.ReadAgentPrimaryModel(
                    config, GatewayConfigEnvelope.ResolveDefaultAgentId(config, _appState.AgentsList));
            }
            GatewayRegistry? registry = _getRegistry();
            string? activeId = registry?.GetActive()?.Id;
            return new DefaultModelGatewayState(
                connected,
                connected && OperatorScopeHelper.CanWriteConfig(client!.GrantedOperatorScopes),
                ChatModelChoice.FromModelsList(_appState.ModelsList),
                primary,
                IsLocalAiRouteAvailable(registry, activeId),
                agentModel,
                activeId,
                LocalAiReceiptOwnsGateway: activeId is not null && _localAiLifecycle.IsNativeMode &&
                    _localAiLifecycle.OwnsGateway(activeId));
        }
    }

    private bool IsLocalAiRouteAvailable(GatewayRegistry? registry, string? activeId)
    {
        if (registry is null || activeId is null)
            return false;
        return _localAiLifecycle.IsNativeMode
            ? _localAiLifecycle.OwnsGateway(activeId)
            : LocalAiGatewayDistroResolver.FindOwners(registry.GetAll()).Any(owner => owner.Id == activeId);
    }

    public async Task RefreshAsync()
    {
        if (_getClient() is not { IsConnectedToGateway: true } client)
            return;
        await client.RequestModelsListAsync().ConfigureAwait(false);
        await client.RequestAgentsListAsync().ConfigureAwait(false);
        await client.RequestConfigAsync().ConfigureAwait(false);
    }

    public IDefaultModelGatewayWrite BeginWrite(string? expectedGatewayId)
    {
        IOperatorGatewayClient client = _getClient() is { IsConnectedToGateway: true } connected
            ? connected
            : throw new InvalidOperationException("Connect to a Gateway first.");
        if (!OperatorScopeHelper.CanWriteConfig(client.GrantedOperatorScopes))
            throw new InvalidOperationException("Your Gateway access cannot change configuration.");
        if (expectedGatewayId is null)
            throw new DefaultModelGatewayChangedException();
        return new DefaultModelGatewayWrite(client, expectedGatewayId, _getClient,
            () => _getRegistry()?.GetActive()?.Id, _getConnectedGatewayId, () => _appState.AgentsList);
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.ModelsList) or nameof(AppState.Config) or nameof(AppState.AgentsList) or null)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _appState.PropertyChanged -= OnAppStateChanged;
}
