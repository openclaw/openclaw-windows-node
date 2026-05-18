using OpenClaw.Shared;
using OpenClaw.Connection;
using OpenClawTray.Services.LocalGatewaySetup;

namespace OpenClawTray.Services;

/// <summary>
/// Implements <see cref="IWindowsNodeConnector"/> by delegating to the
/// app's <see cref="GatewayConnectionManager"/>. All node handshake/pairing
/// events flow through the manager's diagnostics pipeline, giving full
/// visibility in the Connection Status window during the WSL local-setup flow,
/// and the manager's per-gateway identity store + credential resolver are used
/// uniformly with the normal (post-setup) connection lifecycle.
/// </summary>
/// <remarks>
/// Sibling to <see cref="ConnectionManagerOperatorConnector"/> — both are
/// thin facades over <see cref="GatewayConnectionManager"/> for the easy-button
/// setup engine.
/// </remarks>
public sealed class ConnectionManagerWindowsNodeConnector : IWindowsNodeConnector
{
    private readonly GatewayConnectionManager _manager;
    private readonly GatewayRegistry _registry;
    private readonly IOpenClawLogger _logger;

    public ConnectionManagerWindowsNodeConnector(
        GatewayConnectionManager manager,
        GatewayRegistry registry,
        IOpenClawLogger logger)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ConnectAsync(
        string gatewayUrl,
        string token,
        string? bootstrapToken,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The operator connector (ConnectionManagerOperatorConnector) created the
        // registry record during PairOperator phase. Refresh it with any newly-minted
        // bootstrap/shared token the engine may have provisioned in between phases —
        // the credential resolver will then surface them through ResolveNode.
        var normalized = GatewayUrlHelper.NormalizeForWebSocket(gatewayUrl);
        var existing = _registry.FindByUrl(normalized);
        if (existing == null)
        {
            throw new InvalidOperationException(
                "Operator pairing did not create a gateway record for the setup gateway.");
        }

        // Patch in any newly-supplied tokens — never overwrite a stored value with empty.
        var updated = existing with
        {
            SharedGatewayToken = !string.IsNullOrWhiteSpace(token) ? token : existing.SharedGatewayToken,
            BootstrapToken = !string.IsNullOrWhiteSpace(bootstrapToken) ? bootstrapToken : existing.BootstrapToken,
        };
        _registry.AddOrUpdate(updated);
        _registry.Save();

        _logger.Info(
            $"[SetupNodeConnector] Driving node connection via manager to {GatewayUrlHelper.SanitizeForDisplay(normalized)}");

        // Surface any manager exception with a message stable enough for the
        // existing role-upgrade auto-approve catch in
        // SettingsWindowsTrayNodeProvisioner.PairAsync (which catches generic Exception
        // and runs the WSL CLI device approver before retrying once).
        await _manager.EnsureNodeConnectedAsync(cancellationToken);
    }
}
