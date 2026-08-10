using System.Collections.ObjectModel;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClaw.Shared.Codex;

namespace OpenClawTray.Services;

/// <summary>
/// Canonical immutable capability snapshot shared by the gateway and MCP transports.
/// NodeService creates and wires UI-bound capabilities; this owner decides which
/// capabilities are advertised and publishes each rebuild atomically.
/// </summary>
public sealed class NodeCapabilityRegistry
{
    private readonly object _gate = new();
    private readonly Func<INodeCapability?> _codexCapabilityFactory;
    private IReadOnlyList<INodeCapability> _sharedSnapshot = Array.Empty<INodeCapability>();
    private IReadOnlyList<INodeCapability> _mcpOnlySnapshot = Array.Empty<INodeCapability>();

    public NodeCapabilityRegistry(IOpenClawLogger logger)
        : this(() => CreateCodexCapability(logger))
    {
    }

    internal NodeCapabilityRegistry(Func<INodeCapability?> codexCapabilityFactory)
    {
        _codexCapabilityFactory = codexCapabilityFactory
            ?? throw new ArgumentNullException(nameof(codexCapabilityFactory));
    }

    public IReadOnlyList<INodeCapability> Rebuild(
        IEnumerable<INodeCapability> capabilities,
        CodexSessionAccessMode codexAccess)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        var rebuilt = capabilities.ToList();
        if (codexAccess is CodexSessionAccessMode.ReadOnly or CodexSessionAccessMode.ReadAndSteer)
        {
            var codex = _codexCapabilityFactory();
            if (codex is not null)
                rebuilt.Add(codex);
        }

        var snapshot = Freeze(rebuilt);
        lock (_gate)
            _sharedSnapshot = snapshot;
        return snapshot;
    }

    public void Clear()
    {
        lock (_gate)
            _sharedSnapshot = Array.Empty<INodeCapability>();
    }

    public IReadOnlyList<INodeCapability> GetGatewaySnapshot()
    {
        lock (_gate)
            return _sharedSnapshot;
    }

    public IReadOnlyList<INodeCapability> GetMcpSnapshot()
    {
        lock (_gate)
        {
            if (_mcpOnlySnapshot.Count == 0)
                return _sharedSnapshot;

            return Freeze(_sharedSnapshot.Concat(_mcpOnlySnapshot));
        }
    }

    public void RegisterMcpOnly(INodeCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        lock (_gate)
            _mcpOnlySnapshot = Freeze(_mcpOnlySnapshot.Append(capability));
    }

    public void RegisterGateway(WindowsNodeClient? client, IOpenClawLogger logger)
    {
        if (client is null)
            return;

        foreach (var capability in GetGatewaySnapshot())
        {
            if (IsLocalOnly(capability))
            {
                logger.Warn($"Capability {capability.Category} contains local-only commands and will not be registered with the gateway node transport.");
                continue;
            }

            client.RegisterCapability(capability);
        }
    }

    private static bool IsLocalOnly(INodeCapability capability) =>
        capability.Commands.Any(command =>
            command.StartsWith("app.connection.", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<INodeCapability> Freeze(IEnumerable<INodeCapability> capabilities) =>
        new ReadOnlyCollection<INodeCapability>(capabilities.ToArray());

    private static INodeCapability? CreateCodexCapability(IOpenClawLogger logger)
    {
        var launchPlan = new CodexExecutableResolver().Resolve();
        return launchPlan is null ? null : new DeferredCodexSessionCapability(logger, launchPlan);
    }

    private sealed class DeferredCodexSessionCapability(
        IOpenClawLogger logger,
        CodexLaunchPlan launchPlan) : NodeCapabilityBase(logger)
    {
        private static readonly IReadOnlyList<string> ReadCommands = Array.AsReadOnly(
        [
            CodexSessionCapability.ThreadsListCommand,
            CodexSessionCapability.ThreadTurnsListCommand,
        ]);

        public override string Category => "codex-app-server-threads";

        public override IReadOnlyList<string> Commands => ReadCommands;

        public override Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request) =>
            ExecuteAsync(request, CancellationToken.None);

        public override async Task<NodeInvokeResponse> ExecuteAsync(
            NodeInvokeRequest request,
            CancellationToken cancellationToken)
        {
            await using var client = await CodexAppServerClient.ConnectCatalogAsync(
                launchPlan,
                cancellationToken).ConfigureAwait(false);
            var capability = new CodexSessionCapability(
                Logger,
                new CodexSessionCatalogService(client));
            return await capability.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
