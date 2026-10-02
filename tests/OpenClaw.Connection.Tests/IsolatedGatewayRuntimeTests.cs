using System.Net;
using System.Text.Json;
using OpenClaw.Connection.NativeGateway;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

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
    private readonly ManualTimeProvider _clock = new();
    private string _startState = "running";
    private TimeSpan _startDuration;
    private Action? _beforeStatus;
    private bool _failStart;
    private Func<CancellationToken, Task>? _duringStart;
    private Func<CancellationToken, Task>? _duringStatus;
    private int _reportedPort = Port;

    public IsolatedGatewayRuntimeTests()
    {
        var client = new NativeGatewayPackageClient(async (package, args, ct) =>
        {
            _calls.Add(string.Join(" ", args));
            _packageCalls.Add((package.PackageFamilyName, string.Join(" ", args)));
            bool other = package.PackageFamilyName == OtherFamily;
            int port = other ? Port + 1 : _reportedPort;
            int processId = other ? 4567 : ProcessId;
            if (args.SequenceEqual(["gateway-service", "status", "--json"]) && _statusFailure is not null)
                throw _statusFailure;
            if (args.SequenceEqual(["gateway-service", "status", "--json"]))
            {
                if (_duringStatus is not null) await _duringStatus(ct);
                _beforeStatus?.Invoke();
            }
            if (args.SequenceEqual(["gateway-service", "start", "--json"]))
            {
                if (_duringStart is not null) await _duringStart(ct);
                _clock.Advance(_startDuration);
                if (_failStart)
                    return new NativeGatewayCommandResult(1,
                        """{"ok":false,"schemaVersion":1,"command":"gateway-service start","integration":{"kind":"isolated-session","version":1},"error":{"code":"cli_error","message":"The gateway process started but is not listening yet."}}""");
                if (other) _otherState = "running";
                else _state = _startState;
                if (_listenOnStart)
                    _listeners = [.. _listeners.Where(l => l.Port != port),
                        new(IPAddress.Loopback, port, processId, "fixture", null)];
            }
            if (args.SequenceEqual(["gateway-service", "stop", "--json"]))
            {
                if (_stopFailure is not null)
                    throw _stopFailure;
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
            return new NativeGatewayCommandResult(0,
                JsonSerializer.Serialize(new
                {
                    ok = true,
                    schemaVersion = 1,
                    command = string.Join(" ", args.Take(2)),
                    integration = new { kind = "isolated-session", version = 1 },
                    gateway
                }));
        });
        _runtime = new IsolatedGatewayRuntime(new Resolver(_package, FixturePackage(OtherFamily)), client,
            () => new WindowsTcpListenerSnapshotResult(_listeners, _snapshotComplete, true),
            () => new Dictionary<int, ulong> { [ProcessId] = _hostSequence, [4567] = 88 },
            _ => true, timeProvider: _clock);
    }

    [Fact]
    public async Task SlowSuccessfulStartAcknowledgementStillRequiresFreshRunningStatus()
    {
        _state = "stopped";
        _listeners = [];
        _listenOnStart = true;
        _startDuration = TimeSpan.FromSeconds(136);
        await _runtime.EnsureRunningAsync(_record, default);
        Assert.Equal(2, _calls.Count(call => call == "gateway-service status --json"));
        Assert.DoesNotContain("gateway-service stop --json", _calls);
        Assert.Equal(GatewayEndpointProvenanceKind.ExpectedManagedGateway, _runtime.Inspect(_record).Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateSuccessfulAcknowledgementGetsBoundedConfirmationWithoutRestarting(bool expire)
    {
        _state = "stopped";
        _listeners = [];
        _listenOnStart = true;
        _duringStart = _ =>
        {
            _clock.Advance(TimeSpan.FromSeconds(179));
            _duringStatus = ct => expire
                ? Task.Delay(Timeout.Infinite, ct)
                : Task.Delay(TimeSpan.FromSeconds(8), _clock, ct);
            return Task.CompletedTask;
        };
        var start = _runtime.EnsureRunningAsync(_record, default);
        _clock.Advance(expire ? IsolatedGatewayRuntime.StartupConfirmationTimeout : TimeSpan.FromSeconds(8));
        if (expire)
        {
            await Assert.ThrowsAsync<NativeGatewayStartupTimeoutException>(() => start.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, _calls.Count(call => call == "gateway-service stop --json"));
        }
        else
        {
            await start.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain("gateway-service stop --json", _calls);
            Assert.Equal(GatewayEndpointProvenanceKind.ExpectedManagedGateway, _runtime.Inspect(_record).Kind);
        }
        Assert.Equal(1, _calls.Count(call => call == "gateway-service start --json"));
        _duringStart = _duringStatus = null;
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(25, false)]
    public async Task LateAcknowledgementConfirmationRemainsWithinOuterAuthorizationBudget(int initialStatusSeconds, bool allowed)
    {
        _state = "stopped";
        _listeners = [];
        _listenOnStart = true;
        var statuses = 0;
        _duringStatus = ct =>
        {
            statuses++;
            _clock.Advance(TimeSpan.FromSeconds(statuses switch
            {
                1 => initialStatusSeconds,
                2 => 8,
                _ => 5
            }));
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        _startDuration = TimeSpan.FromSeconds(179);
        using var outer = new CancellationTokenSource(NativeGatewayEndpointSecurity.CredentialHandoffTimeout, _clock);
        var authorization = NativeGatewayEndpointSecurity.AuthorizeAsync(_runtime, _record, outer.Token);
        if (allowed)
        {
            Assert.True((await authorization.WaitAsync(TimeSpan.FromSeconds(5))).Allowed);
            Assert.Equal(3, statuses);
            Assert.False(outer.IsCancellationRequested);
            Assert.DoesNotContain("gateway-service stop --json", _calls);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authorization.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(outer.IsCancellationRequested);
            Assert.Equal(1, _calls.Count(call => call == "gateway-service stop --json"));
        }
        Assert.Equal(1, _calls.Count(call => call == "gateway-service start --json"));
        _duringStatus = null;
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("sequence")]
    [InlineData("port")]
    [InlineData("pending")]
    [InlineData("incomplete")]
    public async Task ListenerTransitionGetsOneFreshAttestationAndStillRequiresFullOwnership(string outcome)
    {
        _state = "stopped";
        _listeners = [];
        _startState = "starting";
        var start = _runtime.EnsureRunningAsync(_record, default);
        var confirmations = 0;
        _beforeStatus = () =>
        {
            confirmations++;
            _listeners = [new(IPAddress.Loopback, Port, ProcessId, "fixture", null)];
            if (confirmations < 2) return;
            _state = outcome == "pending" ? "starting" : "running";
            if (outcome == "sequence") _hostSequence = 99;
            if (outcome == "incomplete") _snapshotComplete = false;
        };
        // The fixture captures the response port before _beforeStatus. Set it
        // between observations so the confirmation reports the changed port.
        if (outcome == "port") _reportedPort = Port + 1;
        _clock.Advance(TimeSpan.FromSeconds(2));
        if (outcome == "valid")
        {
            await start.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain("gateway-service stop --json", _calls);
            Assert.Equal(GatewayEndpointProvenanceKind.ExpectedManagedGateway, _runtime.Inspect(_record).Kind);
        }
        else
        {
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => start.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, _calls.Count(call => call == "gateway-service stop --json"));
        }
        Assert.Equal(2, confirmations);
        Assert.Equal(1, _calls.Count(call => call == "gateway-service start --json"));
        _beforeStatus = null;
    }

    [Fact]
    public async Task StartupTimeoutKeepsSafeDiagnosticThroughCredentialAuthorization()
    {
        _state = "stopped";
        _listeners = [];
        _duringStart = ct => Task.Delay(Timeout.Infinite, ct);
        var authorization = NativeGatewayEndpointSecurity.AuthorizeAsync(_runtime, _record, default);
        _clock.Advance(IsolatedGatewayRuntime.StartupTimeout);
        var result = await authorization.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Allowed);
        Assert.Equal(GatewayErrorKind.Network, result.FailureKind);
        Assert.Contains("startup or listener confirmation timed out", result.Detail);
        Assert.DoesNotContain("fixture-token", result.Detail);
    }

    [Fact]
    public async Task PackageTimeoutKeepsTypedStartupDiagnosticWithoutRawPackageText()
    {
        _state = "stopped";
        _listeners = [];
        _duringStart = _ => throw new TimeoutException("sensitive package fixture output");
        var error = await Assert.ThrowsAsync<NativeGatewayStartupTimeoutException>(() =>
            _runtime.EnsureRunningAsync(_record, default));
        Assert.DoesNotContain("sensitive", error.Message);
        Assert.Equal(1, _calls.Count(call => call == "gateway-service stop --json"));
    }

    [Fact]
    public async Task ConditionalAcknowledgedPendingContractPollsRepeatedlyWithoutRestarting()
    {
        _state = "stopped";
        _listeners = [];
        _startState = "starting";
        using var pollScheduled = new SemaphoreSlim(0);
        _clock.TimerScheduled += due =>
        {
            if (due == TimeSpan.FromSeconds(2)) pollScheduled.Release();
        };
        var start = _runtime.EnsureRunningAsync(_record, default);
        for (var poll = 0; poll < 68; poll++)
        {
            Assert.True(await pollScheduled.WaitAsync(TimeSpan.FromSeconds(5)));
            if (poll == 67)
                _beforeStatus = () =>
                {
                    _state = "running";
                    _listeners = [new(IPAddress.Loopback, Port, ProcessId, "fixture", null)];
                };
            _clock.Advance(TimeSpan.FromSeconds(2));
        }
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(70, _calls.Count(call => call == "gateway-service status --json"));
        Assert.Equal(1, _calls.Count(call => call == "gateway-service start --json"));
        Assert.DoesNotContain("gateway-service stop --json", _calls);
        _beforeStatus = null;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationDuringPackageCommandRollsBackAndReleasesGate(bool duringStatus, bool deadline)
    {
        _state = "stopped";
        _listeners = [];
        _listenOnStart = true;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Block(CancellationToken ct)
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
        _duringStart = duringStatus
            ? _ => { _duringStatus = Block; return Task.CompletedTask; }
            : Block;
        using var cancellation = new CancellationTokenSource();
        var start = _runtime.EnsureRunningAsync(_record, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (deadline) _clock.Advance(IsolatedGatewayRuntime.StartupTimeout);
        else cancellation.Cancel();
        if (deadline)
            await Assert.ThrowsAsync<NativeGatewayStartupTimeoutException>(() => start.WaitAsync(TimeSpan.FromSeconds(5)));
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, _calls.Count(call => call == "gateway-service stop --json"));
        _duringStart = _duringStatus = null;
        await _runtime.EnsureRunningAsync(_record, default);
        Assert.Equal(GatewayEndpointProvenanceKind.ExpectedManagedGateway, _runtime.Inspect(_record).Kind);
    }

    [Theory]
    [InlineData("starting")]
    [InlineData("unhealthy")]
    public async Task AcknowledgedOwnedStartWaitsPastNinetySecondsForFreshListenerProof(string pending)
    {
        _state = "stopped";
        _listeners = [];
        _startState = pending;
        _startDuration = TimeSpan.FromSeconds(90);
        var start = _runtime.EnsureRunningAsync(_record, default);
        Assert.False(start.IsCompleted);
        Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener, _runtime.Inspect(_record).Kind);

        _beforeStatus = () =>
        {
            _state = "running";
            _listeners = [new(IPAddress.Loopback, Port, ProcessId, "fixture", null)];
        };
        _clock.Advance(TimeSpan.FromSeconds(46));
        await start.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(136).Ticks, _clock.GetTimestamp());
        Assert.Equal(1, _calls.Count(call => call == "gateway-service start --json"));
        Assert.DoesNotContain("gateway-service stop --json", _calls);
        Assert.Equal(GatewayEndpointProvenanceKind.ExpectedManagedGateway, _runtime.Inspect(_record).Kind);
        _beforeStatus = null;
    }

    [Fact]
    public async Task PendingStartDeadlineIncludesStartCommandAndRollsBackExactlyOnce()
    {
        _state = "stopped";
        _listeners = [];
        _startState = "starting";
        _startDuration = TimeSpan.FromSeconds(90);
        var start = _runtime.EnsureRunningAsync(_record, default);
        _clock.Advance(TimeSpan.FromSeconds(90));

        await Assert.ThrowsAsync<NativeGatewayStartupTimeoutException>(() => start.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, _calls.Count(call => call == "gateway-service stop --json"));
        Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener, _runtime.Inspect(_record).Kind);
    }

    [Fact]
    public async Task CancellingPendingOwnedStartRollsBackAndReleasesGate()
    {
        _state = "stopped";
        _listeners = [];
        _startState = "starting";
        using var cancellation = new CancellationTokenSource();
        var start = _runtime.EnsureRunningAsync(_record, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(1, _calls.Count(call => call == "gateway-service stop --json"));

        _startState = "running";
        _listenOnStart = true;
        await _runtime.EnsureRunningAsync(_record, default);
        Assert.Equal(GatewayEndpointProvenanceKind.ExpectedManagedGateway, _runtime.Inspect(_record).Kind);
    }

    [Theory]
    [InlineData("stopped")]
    [InlineData("not-started")]
    [InlineData("unknown")]
    public async Task OwnedStartTerminalStateDoesNotRetryStart(string terminal)
    {
        _state = "stopped";
        _listeners = [];
        _startState = terminal;
        await Assert.ThrowsAsync<NativeGatewayContractException>(() => _runtime.EnsureRunningAsync(_record, default));
        Assert.Equal(1, _calls.Count(call => call == "gateway-service start --json"));
        Assert.Equal(1, _calls.Count(call => call == "gateway-service stop --json"));
    }

    [Theory]
    [InlineData("starting")]
    [InlineData("unhealthy")]
    [InlineData("unknown")]
    public async Task PreexistingUnreadyServiceIsNeverAdoptedOrStopped(string state)
    {
        _state = state;
        _listeners = [];
        await Assert.ThrowsAsync<NativeGatewayContractException>(() => _runtime.EnsureRunningAsync(_record, default));
        Assert.DoesNotContain("gateway-service start --json", _calls);
        Assert.DoesNotContain("gateway-service stop --json", _calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingStartRejectsUnattributedListenerOrReusedProcessSequence(bool claimsRunning)
    {
        _state = "stopped";
        _listeners = [];
        _startState = "starting";
        var start = _runtime.EnsureRunningAsync(_record, default);
        _beforeStatus = () =>
        {
            _state = claimsRunning ? "running" : "starting";
            _listeners = [new(IPAddress.Loopback, Port, ProcessId, "fixture", null)];
            _hostSequence = 99;
        };
        _clock.Advance(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<NativeGatewayListenerException>(() => start.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, _calls.Count(call => call == "gateway-service stop --json"));
        Assert.Equal(GatewayEndpointProvenanceKind.UnknownListener, _runtime.Inspect(_record).Kind);
        _beforeStatus = null;
    }

    [Fact]
    public async Task InstalledPackageAmbiguousStartingFailureIsNotTreatedAsAcknowledgement()
    {
        _state = "stopped";
        _listeners = [];
        _failStart = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _runtime.EnsureRunningAsync(_record, default));
        Assert.Equal(1, _calls.Count(call => call == "gateway-service status --json"));
        Assert.Equal(1, _calls.Count(call => call == "gateway-service stop --json"));
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
