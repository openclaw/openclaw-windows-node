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
    private CodexAccessGeneration? _codexAccessGeneration;
    private IReadOnlyList<INodeCapability> _sharedSnapshot = Array.Empty<INodeCapability>();
    private IReadOnlyList<INodeCapability> _mcpOnlySnapshot = Array.Empty<INodeCapability>();

    public NodeCapabilityRegistry(IOpenClawLogger logger)
        : this(
            logger,
            () => new CodexExecutableResolver().Resolve(),
            new CodexAppServerProcessFactory())
    {
    }

    internal NodeCapabilityRegistry(
        IOpenClawLogger logger,
        Func<CodexLaunchPlan?> codexLaunchPlanResolver,
        ICodexAppServerProcessFactory codexProcessFactory)
        : this(() => CreateCodexCapability(
            logger,
            codexLaunchPlanResolver,
            codexProcessFactory))
    {
        ArgumentNullException.ThrowIfNull(codexLaunchPlanResolver);
        ArgumentNullException.ThrowIfNull(codexProcessFactory);
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

        lock (_gate)
        {
            var rebuilt = capabilities.ToList();
            INodeCapability? codex = null;
            if (codexAccess is CodexSessionAccessMode.ReadOnly or CodexSessionAccessMode.ReadAndSteer)
                codex = _codexCapabilityFactory();

            RevokeCodexAccessNoLock();
            if (codex is not null)
                rebuilt.Add(CreateRevocableCodexCapabilityNoLock(codex));

            var snapshot = Freeze(rebuilt);
            _sharedSnapshot = snapshot;
            return snapshot;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            RevokeCodexAccessNoLock();
            _sharedSnapshot = Array.Empty<INodeCapability>();
        }
    }

    public IReadOnlyList<INodeCapability> RefreshCodexSessionAccess(
        CodexSessionAccessMode codexAccess,
        WindowsNodeClient? client,
        IOpenClawLogger logger)
    {
        IReadOnlyList<INodeCapability> snapshot;
        lock (_gate)
        {
            RevokeCodexAccessNoLock();
            var refreshed = _sharedSnapshot
                .Where(capability => !string.Equals(
                    capability.Category,
                    "codex-app-server-threads",
                    StringComparison.Ordinal))
                .ToList();
            if (codexAccess is CodexSessionAccessMode.ReadOnly or CodexSessionAccessMode.ReadAndSteer)
            {
                var codex = _codexCapabilityFactory();
                if (codex is not null)
                    refreshed.Add(CreateRevocableCodexCapabilityNoLock(codex));
            }

            snapshot = Freeze(refreshed);
            _sharedSnapshot = snapshot;
        }

        RegisterGateway(client, logger);
        return snapshot;
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

        var gatewayCapabilities = new List<INodeCapability>();
        foreach (var capability in GetGatewaySnapshot())
        {
            if (IsLocalOnly(capability))
            {
                logger.Warn($"Capability {capability.Category} contains local-only commands and will not be registered with the gateway node transport.");
                continue;
            }

            gatewayCapabilities.Add(capability);
        }
        client.ReplaceCapabilities(gatewayCapabilities);
    }

    private static bool IsLocalOnly(INodeCapability capability) =>
        capability.Commands.Any(command =>
            command.StartsWith("app.connection.", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<INodeCapability> Freeze(IEnumerable<INodeCapability> capabilities) =>
        new ReadOnlyCollection<INodeCapability>(capabilities.ToArray());

    private INodeCapability CreateRevocableCodexCapabilityNoLock(INodeCapability capability)
    {
        _codexAccessGeneration = new CodexAccessGeneration();
        return new RevocableCodexSessionCapability(capability, _codexAccessGeneration);
    }

    private void RevokeCodexAccessNoLock()
    {
        _codexAccessGeneration?.Revoke();
        _codexAccessGeneration = null;
    }

    private static INodeCapability? CreateCodexCapability(
        IOpenClawLogger logger,
        Func<CodexLaunchPlan?> launchPlanResolver,
        ICodexAppServerProcessFactory processFactory)
    {
        var launchPlan = launchPlanResolver();
        return launchPlan is null
            ? null
            : new DeferredCodexSessionCapability(logger, launchPlan, processFactory);
    }

    private sealed class DeferredCodexSessionCapability(
        IOpenClawLogger logger,
        CodexLaunchPlan launchPlan,
        ICodexAppServerProcessFactory processFactory) : NodeCapabilityBase(logger)
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
            try
            {
                await using var client = await CodexAppServerClient.ConnectCatalogAsync(
                    launchPlan,
                    processFactory,
                    cancellationToken).ConfigureAwait(false);
                var capability = new CodexSessionCapability(
                    Logger,
                    new CodexSessionCatalogService(client));
                return await capability.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return Error(request.Command == CodexSessionCapability.ThreadTurnsListCommand
                    ? "Codex app-server transcript is unavailable"
                    : "Codex app-server catalog is unavailable");
            }
        }
    }

    private sealed class RevocableCodexSessionCapability(
        INodeCapability inner,
        CodexAccessGeneration accessGeneration) : INodeCapability, INodeCapabilityDeliveryLeaseProvider
    {
        public string Category => inner.Category;

        public IReadOnlyList<string> Commands => inner.Commands;

        public bool CanHandle(string command) => inner.CanHandle(command);

        public Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request) =>
            ExecuteAsync(request, CancellationToken.None);

        public async Task<NodeInvokeResponse> ExecuteAsync(
            NodeInvokeRequest request,
            CancellationToken cancellationToken)
        {
            using var accessExecution = accessGeneration.BeginExecution();
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                accessExecution.Token);
            var response = await inner.ExecuteAsync(request, execution.Token).ConfigureAwait(false);
            accessExecution.Token.ThrowIfCancellationRequested();
            return response;
        }

        public IDisposable? TryAcquireDeliveryLease() =>
            accessGeneration.TryAcquireDeliveryLease();
    }

    private sealed class CodexAccessGeneration
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly CancellationToken _token;
        private bool _revoked;
        private bool _disposed;
        private int _activeExecutions;
        private int _activeDeliveries;

        public CodexAccessGeneration()
        {
            _token = _cancellation.Token;
        }

        public ExecutionLease BeginExecution()
        {
            lock (_gate)
            {
                if (_revoked)
                    throw new OperationCanceledException(_token);
                _activeExecutions++;
                return new ExecutionLease(this, _token);
            }
        }

        public IDisposable? TryAcquireDeliveryLease()
        {
            lock (_gate)
            {
                if (_revoked)
                    return null;
                _activeDeliveries++;
                return new DeliveryLease(this);
            }
        }

        public void Revoke()
        {
            lock (_gate)
            {
                if (_revoked)
                    return;
                _revoked = true;
            }

            _cancellation.Cancel();

            lock (_gate)
            {
                while (_activeDeliveries > 0)
                    Monitor.Wait(_gate);
                DisposeIfRetiredNoLock();
            }
        }

        private void EndExecution()
        {
            lock (_gate)
            {
                _activeExecutions--;
                DisposeIfRetiredNoLock();
            }
        }

        private void EndDelivery()
        {
            lock (_gate)
            {
                _activeDeliveries--;
                Monitor.PulseAll(_gate);
                DisposeIfRetiredNoLock();
            }
        }

        private void DisposeIfRetiredNoLock()
        {
            if (_revoked && !_disposed && _activeExecutions == 0 && _activeDeliveries == 0)
            {
                _disposed = true;
                _cancellation.Dispose();
            }
        }

        internal sealed class ExecutionLease : IDisposable
        {
            private CodexAccessGeneration? _owner;

            public ExecutionLease(CodexAccessGeneration owner, CancellationToken token)
            {
                _owner = owner;
                Token = token;
            }

            public CancellationToken Token { get; }

            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndExecution();
        }

        private sealed class DeliveryLease(CodexAccessGeneration owner) : IDisposable
        {
            private CodexAccessGeneration? _owner = owner;

            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndDelivery();
        }
    }
}
