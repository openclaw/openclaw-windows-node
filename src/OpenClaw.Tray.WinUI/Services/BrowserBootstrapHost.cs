using System.Runtime.Versioning;
using OpenClaw.Connection;
using OpenClaw.Shared.Browser;

namespace OpenClawTray.Services;

/// <summary>Composes native pairing with the existing registry, intent, settings and endpoint-provenance owners.</summary>
[SupportedOSPlatform("windows")]
internal sealed class BrowserBootstrapHost : IAsyncDisposable
{
    private readonly GatewayRegistry _registry;
    private readonly IGatewayConnectionManager _manager;
    private readonly SettingsManager _settings;
    private readonly BrowserBootstrapService _service;
    private readonly BrowserBootstrapPipeServer _pipe;

    public BrowserBootstrapHost(GatewayRegistry registry, IGatewayConnectionManager manager,
        SettingsManager settings, ManagedLocalGatewayPortProvenanceService provenance)
    {
        _registry = registry;
        _manager = manager;
        _settings = settings;
        _service = new BrowserBootstrapService(
            registry.GetActive,
            record => settings.NodeBrowserProxyEnabled &&
                manager.CurrentSnapshot.GatewayId == record.Id &&
                manager.CurrentSnapshot.OperatorState == RoleConnectionState.Connected &&
                manager.IsAutomaticReconnectAllowed(record.Id) && !manager.IsManualGatewayLifecycleInProgress,
            async (record, ct) => (await provenance.InspectAsync(record, ct)).Kind ==
                GatewayEndpointProvenanceKind.ExpectedManagedGateway,
            BrowserBootstrapWslCommand.RunAsync);
        _pipe = new BrowserBootstrapPipeServer(_service.HandleAsync);
    }

    public void Start()
    {
        // Installer registers first; release portable/MSIX starts can register the same packaged helper.
        // A conflicting existing manifest is preserved, never adopted or overwritten.
        if (_settings.NodeBrowserProxyEnabled)
        {
            try
            {
                BrowserNativeRegistration.Apply(Path.Combine(AppContext.BaseDirectory,
                    "tools", "browser-bootstrap", "OpenClaw.BrowserNativeHost.exe"));
            }
            catch (Exception) { /* Optional integration must not prevent tray startup. */ }
        }
        _registry.Changed += OnRegistryChanged;
        _manager.StateChanged += OnStateChanged;
        _settings.Saved += OnSettingsSaved;
        _pipe.Start();
    }

    private void OnRegistryChanged(object? sender, GatewayRegistryChangedEventArgs e) => _service.Invalidate();
    private void OnStateChanged(object? sender, GatewayConnectionSnapshot e) => _service.Invalidate();
    private void OnSettingsSaved(object? sender, EventArgs e) => _service.Invalidate();

    public async ValueTask DisposeAsync()
    {
        _service.Invalidate();
        _registry.Changed -= OnRegistryChanged;
        _manager.StateChanged -= OnStateChanged;
        _settings.Saved -= OnSettingsSaved;
        await _pipe.DisposeAsync();
    }
}
