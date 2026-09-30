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
    private const string OtherFamily = "OpenClawFoundation.OpenClawGateway_123456789abcd";
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
    private readonly List<(string Family, string Command)> _packageCalls = [];
    private readonly IsolatedGatewayRuntime _runtime;
    private WindowsTcpListenerInfo[] _listeners =
        [new(IPAddress.Loopback, Port, ProcessId, "fixture", null)];
    private string _state = "running";
    private bool _listenOnStart;
    private ulong _hostSequence = 77;
    private bool _snapshotComplete = true;
    private Exception? _statusFailure;
    private Exception? _stopFailure;
    private string _otherState = "running";
    private bool _stopKeepsRunning;

    public IsolatedGatewayRuntimeTests()
    {
        var client = new NativeGatewayPackageClient((package, args, _) =>
        {
            _calls.Add(string.Join(" ", args));
            _packageCalls.Add((package.PackageFamilyName, string.Join(" ", args)));
            bool other = package.PackageFamilyName == OtherFamily;
            int port = other ? Port + 1 : Port;
            int processId = other ? 4567 : ProcessId;
            if (args.SequenceEqual(["gateway-service", "status", "--json"]) && _statusFailure is not null)
                return Task.FromException<NativeGatewayCommandResult>(_statusFailure);
            if (args.SequenceEqual(["gateway-service", "start", "--json"]))
            {
                if (other) _otherState = "running";
                else _state = "running";
                if (_listenOnStart)
                    _listeners = [.. _listeners.Where(l => l.Port != port),
                        new(IPAddress.Loopback, port, processId, "fixture", null)];
            }
            if (args.SequenceEqual(["gateway-service", "stop", "--json"]))
            {
                if (_stopFailure is not null)
                    return Task.FromException<NativeGatewayCommandResult>(_stopFailure);
                if (!_stopKeepsRunning)
                {
                    if (other) _otherState = "stopped";
                    else _state = "stopped";
                    _listeners = _listeners.Where(l => l.Port != port).ToArray();
                }
            }
            string state = other ? _otherState : _state;
            object gateway = state == "running"
                ? new
                {
                    state,
                    port,
                    ownership = new
                    {
                        sandboxId = "iso:fixture",
                        agentUserSid = Sid,
                        listeners = new[] { new { port, processId,
                            processStartTimeUtc = s_startTime, sequenceNumber = other ? (ulong)88 : 77 } }
                    }
                }
                : new { state };
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
        _runtime = new IsolatedGatewayRuntime(new Resolver(_package, FixturePackage(OtherFamily)), client,
            () => new WindowsTcpListenerSnapshotResult(_listeners, _snapshotComplete, true),
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
    public async Task LaterInspectionFailureDoesNotRollBackAnEarlierSuccessfulStart()
    {
        _state = "stopped";
        _listeners = [];
        _listenOnStart = true;
        await _runtime.EnsureRunningAsync(_record, default);
        _snapshotComplete = false;

        await Assert.ThrowsAsync<NativeGatewayListenerException>(() => _runtime.EnsureRunningAsync(_record, default));

        Assert.DoesNotContain("gateway-service stop --json", _calls);
        _snapshotComplete = true;
        await _runtime.StopAsync(default);
        Assert.Contains("gateway-service stop --json", _calls);
    }

    [Fact]
    public async Task RollbackStopFailurePreservesOriginalFailureAndStopOwnership()
    {
        _state = "stopped";
        _listeners = [];
        _stopFailure = new IOException("stop failed");

        await Assert.ThrowsAsync<NativeGatewayListenerException>(() => _runtime.EnsureRunningAsync(_record, default));

        _stopFailure = null;
        await _runtime.StopAsync(default);
        Assert.Equal(2, _calls.Count(call => call == "gateway-service stop --json"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectingAnotherPackageDoesNotTransferStartOwnershipOrReplaceActiveProof(bool startOther)
    {
        _state = "stopped";
        _listeners = startOther ? [] : [new(IPAddress.Loopback, Port + 1, 4567, "other", null)];
        _otherState = startOther ? "stopped" : "running";
        _listenOnStart = true;
        var other = _record with { Id = "other", NativePackageFamilyName = OtherFamily, Url = $"ws://127.0.0.1:{Port + 1}" };
        await _runtime.EnsureRunningAsync(_record, default);
        await _runtime.EnsureRunningAsync(other, default);
        await _runtime.InspectAsync(other, default);

        Assert.Equal(GatewayEndpointProvenanceKind.ExpectedManagedGateway, _runtime.Inspect(_record).Kind);
        await _runtime.StopAsync(default);

        Assert.Contains((Family, "gateway-service stop --json"), _packageCalls);
        Assert.Equal(startOther, _packageCalls.Contains((OtherFamily, "gateway-service stop --json")));
    }

    [Fact]
    public async Task ExplicitRestartRestartsPreExistingServiceWithoutTakingDetachOwnership()
    {
        _listenOnStart = true;
        await _runtime.EnsureRunningAsync(_record, default);
        await _runtime.RestartAsync(_record, default);

        Assert.Equal(1, _calls.Count(call => call == "gateway-service stop --json"));
        Assert.Equal(1, _calls.Count(call => call == "gateway-service start --json"));
        Assert.Equal(GatewayEndpointProvenanceKind.ExpectedManagedGateway, _runtime.Inspect(_record).Kind);
        await _runtime.StopAsync(default);
        Assert.Equal(1, _calls.Count(call => call == "gateway-service stop --json"));
    }

    [Fact]
    public async Task ExplicitRestartRejectsAnUnrelatedListener()
    {
        _listeners = [new(IPAddress.Loopback, Port, 4567, "other", null)];
        await Assert.ThrowsAsync<NativeGatewayListenerException>(() => _runtime.RestartAsync(_record, default));
        Assert.DoesNotContain("gateway-service stop --json", _calls);
        Assert.DoesNotContain("gateway-service start --json", _calls);
    }

    [Fact]
    public async Task RestartWithoutConfirmedStopFailsAndRetainsOwnedStart()
    {
        _state = "stopped";
        _listeners = [];
        _listenOnStart = true;
        await _runtime.EnsureRunningAsync(_record, default);
        _stopKeepsRunning = true;

        await Assert.ThrowsAsync<NativeGatewayContractException>(() => _runtime.RestartAsync(_record, default));

        _stopKeepsRunning = false;
        await _runtime.StopAsync(default);
        Assert.Equal(2, _calls.Count(call => call == "gateway-service stop --json"));
        Assert.Equal(1, _calls.Count(call => call == "gateway-service start --json"));
    }

    [Fact]
    public async Task PassiveInspectionFailureDeniesHandoffWithoutThrowingOrStoppingService()
    {
        await _runtime.EnsureRunningAsync(_record, default);
        _statusFailure = new IOException("package removed");

        var result = await _runtime.InspectAsync(_record, default);

        Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener, result.Kind);
        Assert.Equal(GatewayEndpointProvenanceFailureReason.InspectionUnavailable, result.FailureReason);
        Assert.Contains("inspection failed", result.Detail);
        Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener, _runtime.Inspect(_record).Kind);
        Assert.DoesNotContain("gateway-service stop --json", _calls);
    }

    [Fact]
    public async Task PassiveInspectionDeadlineIncludesUnresponsivePackageAndReleasesGate()
    {
        var pending = new TaskCompletionSource<NativeGatewayCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new NativeGatewayPackageClient((_, _, _) => pending.Task);
        await using var runtime = new IsolatedGatewayRuntime(new Resolver(_package), client,
            () => new WindowsTcpListenerSnapshotResult(_listeners, true, true),
            () => new Dictionary<int, ulong> { [ProcessId] = 77 }, _ => true,
            inspectionTimeout: TimeSpan.FromMilliseconds(50));

        var result = await runtime.InspectAsync(_record, default).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener, result.Kind);
        Assert.Contains("timed out", result.Detail);
        await runtime.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        pending.TrySetCanceled();
    }

    [Fact]
    public async Task PassiveInspectionHasABoundedBusyResultDuringConcurrentStartup()
    {
        var pending = new TaskCompletionSource<NativeGatewayCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new NativeGatewayPackageClient((_, _, token) =>
        {
            entered.TrySetResult();
            return pending.Task.WaitAsync(token);
        });
        await using var runtime = new IsolatedGatewayRuntime(new Resolver(_package), client,
            () => new WindowsTcpListenerSnapshotResult(_listeners, true, true),
            () => new Dictionary<int, ulong> { [ProcessId] = 77 }, _ => true,
            inspectionTimeout: TimeSpan.FromMilliseconds(50));
        using var cancellation = new CancellationTokenSource();
        var start = runtime.EnsureRunningAsync(_record, cancellation.Token);
        await entered.Task;
        try
        {
            var result = await runtime.InspectAsync(_record, default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener, result.Kind);
            Assert.Equal(GatewayEndpointProvenanceFailureReason.InspectionUnavailable, result.FailureReason);
            Assert.Contains("lifecycle is busy", result.Detail);
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        }
    }

    [Fact]
    public async Task RouterDeniesUnestablishedRecordsAndRoutesExplicitRestartAndBothStops()
    {
        var legacy = new RecordingRuntime();
        var unused = new NativeGatewayPackageClient((_, _, _) =>
            throw new InvalidOperationException("Isolated restart does not probe the legacy contract."));
        await using var router = new NativeGatewayRuntimeRouter(legacy, _runtime, new Resolver(_package), unused);
        var unestablished = _record with { NativeRuntimeContract = null };
        Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener, router.Inspect(unestablished).Kind);
        Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener,
            (await router.InspectAsync(unestablished, default)).Kind);
        _listenOnStart = true;
        await router.RestartAsync(_record, default);
        Assert.Contains("gateway-service stop --json", _calls);
        Assert.Contains("gateway-service start --json", _calls);
        await router.StopAsync(default);
        Assert.Equal(1, legacy.Stops);
        Assert.Equal(1, _calls.Count(call => call == "gateway-service stop --json"));
    }

    [Fact]
    public async Task PassiveInspectionPropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _runtime.InspectAsync(_record, cancellation.Token));
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

    private sealed class RecordingRuntime : INativeGatewayRuntime
    {
        public int Stops { get; private set; }
        public Task StopAsync(CancellationToken cancellationToken) { Stops++; return Task.CompletedTask; }
        public Task EnsureRunningAsync(GatewayRecord record, CancellationToken cancellationToken) => throw new NotSupportedException();
        public GatewayEndpointProvenance Inspect(GatewayRecord record) => throw new NotSupportedException();
        public Task<GatewayEndpointProvenance> InspectAsync(GatewayRecord record, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static NativeGatewayPackage FixturePackage(string family = Family)
    {
        string aliases = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", family);
        return new NativeGatewayPackage(family, "2026.9.5.5",
            Path.Combine(aliases, "openclaw.exe"), Path.Combine(aliases, "clawctl.exe"));
    }

    private sealed class Resolver(params NativeGatewayPackage[] packages) : INativeGatewayPackageResolver
    {
        public Task<NativeGatewayPackage> ResolveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(packages[0]);
        public Task<NativeGatewayPackage> ResolveAsync(string family, CancellationToken cancellationToken) =>
            Task.FromResult(packages.Single(package => package.PackageFamilyName == family));
    }
}
