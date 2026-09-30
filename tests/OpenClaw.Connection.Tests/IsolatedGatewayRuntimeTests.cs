using System.Net;
using System.Text.Json;
using OpenClaw.Connection.NativeGateway;
using OpenClaw.Shared;

namespace OpenClaw.Connection.Tests;

public sealed class IsolatedGatewayRuntimeTests : IAsyncDisposable
{
    private const int Port = 19001;
    private const int ProcessId = 4321;
    private const string Sid = "S-1-5-21-fixture";
    private const string Family = "OpenClaw.Gateway_123456789abcd";
    private static readonly DateTime s_startTime =
        new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    private readonly NativeGatewayPackage _package = FixturePackage();
    private readonly GatewayRecord _record = new()
    {
        Id = "gw-isolated",
        Url = $"ws://127.0.0.1:{Port}",
        NativePackageFamilyName = Family,
        NativeRuntimeContract = NativeGatewayPackageClient.IsolatedContract,
        SharedGatewayToken = "fixture-token",
        IsLocal = true
    };
    private readonly List<string> _calls = [];
    private readonly IsolatedGatewayRuntime _runtime;
    private WindowsTcpListenerInfo[] _listeners =
        [new(IPAddress.Loopback, Port, ProcessId, "fixture", null)];
    private string _state = "running";
    private bool _listenOnStart;
    private ulong _hostSequence = 77;

    public IsolatedGatewayRuntimeTests()
    {
        var client = new NativeGatewayPackageClient((_, args, _) =>
        {
            _calls.Add(string.Join(" ", args));
            if (args.SequenceEqual(["gateway-service", "start", "--json"]))
            {
                _state = "running";
                if (_listenOnStart)
                    _listeners = [new(IPAddress.Loopback, Port, ProcessId, "fixture", null)];
            }
            if (args.SequenceEqual(["gateway-service", "stop", "--json"]))
            {
                _state = "stopped";
                _listeners = [];
            }
            object gateway = _state == "running"
                ? new
                {
                    state = _state,
                    port = Port,
                    ownership = new
                    {
                        sandboxId = "iso:fixture",
                        agentUserSid = Sid,
                        listeners = new[] { new { port = Port, processId = ProcessId,
                            processStartTimeUtc = s_startTime, sequenceNumber = (ulong)77 } }
                    }
                }
                : new { state = _state };
            return Task.FromResult(new NativeGatewayCommandResult(0,
                JsonSerializer.Serialize(new
                {
                    ok = true,
                    schemaVersion = 1,
                    command = string.Join(" ", args.Take(2)),
                    integration = new { kind = "isolated-session", version = 1 },
                    gateway
                })));
        });
        _runtime = new IsolatedGatewayRuntime(new Resolver(_package), client,
            () => new WindowsTcpListenerSnapshotResult(_listeners, true, true),
            () => new Dictionary<int, ulong> { [ProcessId] = _hostSequence, [4567] = 88 },
            _ => true);
    }

    [Fact]
    public async Task PackageAttestationAndFreshListenerIdentityAuthorizeCredentials()
    {
        var authorizer = new InteractiveGatewayEndpointAuthorizer(_runtime,
            (_, _) => throw new InvalidOperationException("Native credentials must not use a WSL fallback."),
            NullLogger.Instance);
        var credential = new GatewayCredential("fixture-token", false, CredentialResolver.SourceSharedGatewayToken);
        Assert.False(authorizer.IsCredentialAllowed(_record, credential));

        await _runtime.EnsureRunningAsync(_record, CancellationToken.None);
        Assert.True(authorizer.IsCredentialAllowed(_record, credential));
        Assert.Equal(s_startTime, _runtime.Inspect(_record).ProcessStartTimeUtc);
        Assert.Contains("gateway-service status --json", _calls);
        Assert.DoesNotContain("gateway-service start --json", _calls);

        _listeners = [new(IPAddress.Loopback, Port, 4567, "unrelated", null)];
        Assert.False(authorizer.IsCredentialAllowed(_record, credential));
        Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener, _runtime.Inspect(_record).Kind);
    }

    [Fact]
    public async Task AbsentServiceStartsThroughPackageAndStopsOnlyOwnedStart()
    {
        _state = "not-started";
        _listeners = [];

        await Assert.ThrowsAsync<NativeGatewayListenerException>(() =>
            _runtime.EnsureRunningAsync(_record, CancellationToken.None));
        Assert.Contains("gateway-service start --json", _calls);
        Assert.Contains("gateway-service stop --json", _calls);
    }

    [Fact]
    public async Task FreshServiceStartsAndStopsThroughThePackage()
    {
        _state = "not-started";
        _listeners = [];
        _listenOnStart = true;

        await _runtime.EnsureRunningAsync(_record, CancellationToken.None);
        Assert.Equal(GatewayEndpointProvenanceKind.ExpectedManagedGateway,
            (await _runtime.InspectAsync(_record, CancellationToken.None)).Kind);
        await _runtime.StopAsync(CancellationToken.None);

        Assert.Contains("gateway-service start --json", _calls);
        Assert.Contains("gateway-service stop --json", _calls);
    }

    [Fact]
    public async Task ExistingPackageServiceIsNotStoppedWhenCompanionDetaches()
    {
        await _runtime.EnsureRunningAsync(_record, CancellationToken.None);

        await _runtime.StopAsync(CancellationToken.None);

        Assert.DoesNotContain("gateway-service stop --json", _calls);
    }

    [Fact]
    public async Task ReusedPidCannotReuseCachedPackageOwnership()
    {
        await _runtime.EnsureRunningAsync(_record, CancellationToken.None);

        _hostSequence = 88;

        Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener, _runtime.Inspect(_record).Kind);
    }

    [Fact]
    public async Task UnsupportedPackageContractProvidesActionableCredentialDenial()
    {
        var unsupported = new NativeGatewayPackageClient((_, _, _) =>
            Task.FromResult(new NativeGatewayCommandResult(0,
                """{"ok":true,"schemaVersion":1,"command":"gateway-service status","integration":{"kind":"isolated-session","version":2},"gateway":{"state":"running","port":19001}}""")));
        await using var runtime = new IsolatedGatewayRuntime(new Resolver(_package), unsupported,
            () => new WindowsTcpListenerSnapshotResult(_listeners, true, true),
            () => new Dictionary<int, ulong> { [ProcessId] = 77 }, _ => true);

        EndpointCredentialAuthorization authorization = await NativeGatewayEndpointSecurity.AuthorizeAsync(
            runtime, _record, CancellationToken.None);

        Assert.False(authorization.Allowed);
        Assert.Contains("Update the Gateway MSIX", authorization.Detail, StringComparison.Ordinal);
        Assert.Equal(GatewayErrorKind.Network, authorization.FailureKind);
    }

    [Fact]
    public async Task MissingOsSequenceSupportReportsRecoveryInsteadOfPortConflict()
    {
        var client = new NativeGatewayPackageClient((_, _, _) =>
            Task.FromResult(new NativeGatewayCommandResult(0,
                JsonSerializer.Serialize(new
                {
                    ok = true,
                    schemaVersion = 1,
                    command = "gateway-service status",
                    integration = new { kind = "isolated-session", version = 1 },
                    gateway = new
                    {
                        state = "running",
                        port = Port,
                        ownership = new
                        {
                            sandboxId = "iso:fixture",
                            agentUserSid = Sid,
                            listeners = new[]
                            {
                                new { port = Port, processId = ProcessId,
                                    processStartTimeUtc = s_startTime, sequenceNumber = (ulong)77 }
                            }
                        }
                    }
                }))));
        await using var runtime = new IsolatedGatewayRuntime(new Resolver(_package), client,
            () => new WindowsTcpListenerSnapshotResult(_listeners, true, true),
            () => throw new NotSupportedException("Update Windows for process sequence inspection."),
            _ => true);

        EndpointCredentialAuthorization authorization =
            await NativeGatewayEndpointSecurity.AuthorizeAsync(runtime, _record, CancellationToken.None);

        Assert.False(authorization.Allowed);
        Assert.Equal(GatewayErrorKind.Network, authorization.FailureKind);
        Assert.Contains("Update Windows", authorization.Detail, StringComparison.Ordinal);
    }

    public ValueTask DisposeAsync() => _runtime.DisposeAsync();

    private static NativeGatewayPackage FixturePackage()
    {
        string aliases = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", Family);
        return new NativeGatewayPackage(Family, "2026.9.5.5",
            Path.Combine(aliases, "openclaw.exe"), Path.Combine(aliases, "clawctl.exe"));
    }

    private sealed class Resolver(NativeGatewayPackage package) : INativeGatewayPackageResolver
    {
        public Task<NativeGatewayPackage> ResolveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(package);
    }
}
