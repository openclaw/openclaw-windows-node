using System.ComponentModel;
using System.Text.Json;
using OpenClaw.Chat;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Presentation;

namespace OpenClawTray.Services;

/// <summary>
/// Projects the operator connection's cached models list and config into the Default model
/// page, and changes <c>agents.defaults.model.primary</c> with a guarded partial
/// <c>config.patch</c>. Local AI route ownership stays with the Local AI runtime.
/// </summary>
internal sealed class DefaultModelGatewayService : IDefaultModelGatewayService, IDisposable
{
    private readonly AppState _appState;
    private readonly Func<IOperatorGatewayClient?> _getClient;
    private readonly Func<GatewayRegistry?> _getRegistry;
    private readonly LocalAiGatewayLifecycle _localAiLifecycle;

    public DefaultModelGatewayService(
        AppState appState,
        Func<IOperatorGatewayClient?> getClient,
        Func<GatewayRegistry?> getRegistry,
        LocalAiGatewayLifecycle localAiLifecycle)
    {
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
        _getClient = getClient ?? throw new ArgumentNullException(nameof(getClient));
        _getRegistry = getRegistry ?? throw new ArgumentNullException(nameof(getRegistry));
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
            string? primary = _appState.Config is { } envelope &&
                GatewayConfigEnvelope.TryRead(envelope, out JsonElement config, out _)
                    ? GatewayConfigEnvelope.ReadPrimaryModel(config)
                    : null;
            return new DefaultModelGatewayState(
                connected,
                connected && OperatorScopeHelper.CanWriteConfig(client!.GrantedOperatorScopes),
                ChatModelChoice.FromModelsList(_appState.ModelsList),
                primary,
                IsLocalAiRouteAvailable());
        }
    }

    private bool IsLocalAiRouteAvailable()
    {
        GatewayRegistry? registry = _getRegistry();
        string? activeId = registry?.GetActive()?.Id;
        if (activeId is null)
            return false;
        return _localAiLifecycle.IsNativeMode
            ? _localAiLifecycle.OwnsGateway(activeId)
            : LocalAiGatewayDistroResolver.FindOwners(registry!.GetAll()).Any(owner => owner.Id == activeId);
    }

    public async Task RefreshAsync()
    {
        if (_getClient() is not { IsConnectedToGateway: true } client)
            return;
        await client.RequestModelsListAsync().ConfigureAwait(false);
        await client.RequestConfigAsync().ConfigureAwait(false);
    }

    public async Task SetPrimaryModelAsync(string modelRef)
    {
        if (string.IsNullOrWhiteSpace(modelRef) || modelRef.Length > 512 ||
            modelRef.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new InvalidOperationException("Choose a valid model.");
        IOperatorGatewayClient client = _getClient() is { IsConnectedToGateway: true } connected
            ? connected
            : throw new InvalidOperationException("Connect to a Gateway first.");
        if (!OperatorScopeHelper.CanWriteConfig(client.GrantedOperatorScopes))
            throw new InvalidOperationException("Your Gateway access cannot change configuration.");

        for (int attempt = 0; attempt < 2; attempt++)
        {
            JsonElement response = await client.SendWizardRequestAsync("config.get", new { }, 15_000).ConfigureAwait(false);
            if (!GatewayConfigEnvelope.TryRead(response, out JsonElement config, out string? baseHash))
                throw new InvalidDataException("The Gateway returned an unreadable configuration.");
            ConfigPatchResult result = await client.PatchConfigDetailedAsync(
                    JsonSerializer.SerializeToElement(GatewayConfigEnvelope.BuildPrimaryPatch(config, modelRef)),
                    baseHash)
                .ConfigureAwait(false);
            if (result.Ok)
            {
                await client.RequestConfigAsync().ConfigureAwait(false);
                return;
            }
            if (!result.LooksLikeStaleBaseHash || attempt == 1)
                throw new InvalidOperationException(result.Error ?? "The Gateway rejected the model change.");
        }
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.ModelsList) or nameof(AppState.Config) or null)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _appState.PropertyChanged -= OnAppStateChanged;
}
