using System;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Services;
using Xunit;

namespace OpenClaw.Tray.Tests;

public class ManagedLocalGatewayRepairCoordinatorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GatewayRegistry _registry;
    private readonly ConnectionDiagnostics _diagnostics = new();

    public ManagedLocalGatewayRepairCoordinatorTests()
    {
        _tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-repair-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(_tempDir);
        _registry = new GatewayRegistry(_tempDir);
    }

    public void Dispose()
    {
        // slopwatch-ignore: SW003 Test cleanup is best-effort and must not hide the test outcome.
        try { System.IO.Directory.Delete(_tempDir, true); } catch { }
    }

    private void SetActiveManagedLocal()
    {
        _registry.AddOrUpdate(new GatewayRecord
        {
            Id = "gw-local",
            Url = "ws://localhost:18789",
            IsLocal = true,
            SetupManagedDistroName = "OpenClawGateway"
        });
        _registry.SetActive("gw-local");
    }

    private void AddManagedLocal(string id, string distro, int port)
    {
        _registry.AddOrUpdate(new GatewayRecord
        {
            Id = id,
            Url = $"ws://localhost:{port}",
            IsLocal = true,
            SetupManagedDistroName = distro
        });
    }

    private ManagedLocalGatewayRepairCoordinator CreateCoordinator(
        IManagedLocalGatewayRestarter restarter,
        Func<bool> probeReachable,
        Func<string, CancellationToken, Task<bool>> reconnectIfCurrent,
        Func<bool> isOperatorConnected,
        Action? onReArm = null,
        int maxRestarts = 3,
        Func<IDisposable?>? tryAcquireLifecycleLease = null,
        Func<bool>? isRestartStillWarranted = null,
        Func<string, bool>? isAutomaticRepairAllowed = null,
        Func<GatewayRecord, CancellationToken, Task<ManagedLocalPortConflictRepairResult>>?
            repairPortConflictAsync = null,
        Func<bool>? isPortConflictCandidate = null)
        => new(
            _registry,
            restarter,
            (_, _) => Task.FromResult(probeReachable()),
            reconnectIfCurrent,
            isOperatorConnected,
            _ => { onReArm?.Invoke(); return Task.CompletedTask; },
            _diagnostics,
            NullLogger.Instance,
            maxRestartsPerGateway: maxRestarts,
            verifyTimeout: TimeSpan.FromMilliseconds(150),
            verifyPollInterval: TimeSpan.FromMilliseconds(10),
            tryAcquireLifecycleLease: tryAcquireLifecycleLease,
            isRestartStillWarranted: isRestartStillWarranted,
            isAutomaticRepairAllowed: isAutomaticRepairAllowed,
            repairPortConflictAsync: repairPortConflictAsync,
            isPortConflictCandidate: isPortConflictCandidate);

    [Fact]
    public async Task RemoteGateway_NotEligible_DoesNotRestart()
    {
        _registry.AddOrUpdate(new GatewayRecord { Id = "gw-remote", Url = "wss://remote.example" });
        _registry.SetActive("gw-remote");
        var restarter = new FakeRestarter();
        var coordinator = CreateCoordinator(restarter, () => false, (_, _) => Task.FromResult(true), () => true);

        var result = await coordinator.TryRepairActiveGatewayAsync();

        Assert.Equal(ManagedLocalGatewayRepairOutcome.NotEligible, result.Outcome);
        Assert.Equal(0, restarter.Calls);
    }

    [Fact]
    public async Task SshTunnelGateway_NotEligible_DoesNotRestart()
    {
        _registry.AddOrUpdate(new GatewayRecord
        {
            Id = "gw-ssh",
            Url = "ws://localhost:18789",
            IsLocal = true,
            SetupManagedDistroName = "OpenClawGateway",
            SshTunnel = new SshTunnelConfig("user", "host.example", 18789, 45678)
        });
        _registry.SetActive("gw-ssh");
        var restarter = new FakeRestarter();
        var coordinator = CreateCoordinator(restarter, () => false, (_, _) => Task.FromResult(true), () => true);

        var result = await coordinator.TryRepairActiveGatewayAsync();

        Assert.Equal(ManagedLocalGatewayRepairOutcome.NotEligible, result.Outcome);
        Assert.Equal(0, restarter.Calls);
    }

    [Fact]
    public async Task ProbeReachable_ReconnectsWithoutRestart()
    {
        SetActiveManagedLocal();
        var restarter = new FakeRestarter();
        var reconnects = 0;
        var coordinator = CreateCoordinator(
            restarter,
            probeReachable: () => true,
            (_, _) => { reconnects++; return Task.FromResult(true); },
            () => true);

        var result = await coordinator.TryRepairActiveGatewayAsync();

        Assert.Equal(ManagedLocalGatewayRepairOutcome.ReconnectedWithoutRestart, result.Outcome);
        Assert.Equal(0, restarter.Calls); // reachable -> no distro restart
        Assert.Equal(1, reconnects);
    }

    [Fact]
    public async Task ProbeReachableButNeverVerifies_EscalatesToBudgetedRestart()
    {
        SetActiveManagedLocal();
        var restarter = new FakeRestarter();
        var reconnects = 0;
        // Wedged-gateway case: the gateway answers TCP (reachable=true) so the reconnect "succeeds",
        // but the operator connection never verifies until a distro restart actually happens. The
        // coordinator must not loop forever on the TCP-positive reachable path — it must escalate to
        // exactly one budgeted restart, then verify and report Repaired.
        var coordinator = CreateCoordinator(
            restarter,
            probeReachable: () => true,
            (_, _) => { reconnects++; return Task.FromResult(true); },
            isOperatorConnected: () => restarter.Calls > 0);

        var result = await coordinator.TryRepairActiveGatewayAsync();

        Assert.Equal(ManagedLocalGatewayRepairOutcome.Repaired, result.Outcome);
        Assert.Equal(1, restarter.Calls);   // escalated to a single budgeted restart
        Assert.Equal(1, reconnects);         // reachable-path reconnect; restart recovery avoids tearing down a healthy client
    }

    [Fact]
    public async Task ProbeReachable_ReconnectReturnsFalse_AbortsWithoutRestart()
    {
        SetActiveManagedLocal();
        var restarter = new FakeRestarter();
        // Reachable, but the reconnect no-ops (returns false) because the active gateway changed
        // during the repair. The coordinator must NOT escalate to restarting a gateway we left.
        var coordinator = CreateCoordinator(
            restarter,
            probeReachable: () => true,
            (_, _) => Task.FromResult(false),
            isOperatorConnected: () => false);

        var result = await coordinator.TryRepairActiveGatewayAsync();

        Assert.Equal(ManagedLocalGatewayRepairOutcome.AbortedGatewayChanged, result.Outcome);
        Assert.Equal(0, restarter.Calls);
    }

    [Fact]
    public async Task GatewaySwitchDuringVerify_AbortsBeforeConsumingBudgetOrRestarting()
    {
        SetActiveManagedLocal();
        _registry.AddOrUpdate(new GatewayRecord { Id = "gw-other", Url = "wss://remote.example" });
        var restarter = new FakeRestarter();
        // Reachable + pinned reconnect returns true, but simulates the user switching gateways during
        // it. Verify then fails (active != pinned). The coordinator must re-check the active gateway and
        // ABORT before consuming budget or restarting the gateway the user just left.
        var coordinator = CreateCoordinator(
            restarter,
            probeReachable: () => true,
            reconnectIfCurrent: (_, _) => { _registry.SetActive("gw-other"); return Task.FromResult(true); },
            isOperatorConnected: () => true);

        var result = await coordinator.TryRepairActiveGatewayAsync();

        Assert.Equal(ManagedLocalGatewayRepairOutcome.AbortedGatewayChanged, result.Outcome);
        Assert.Equal(0, restarter.Calls);
    }

    [Fact]
    public async Task RecordRepointedToRemoteDuringVerify_AbortsBeforeRestart()
    {
        SetActiveManagedLocal();
        var restarter = new FakeRestarter();
        var mutated = false;
        // Reachable + reconnect "succeeds" but never verifies; during verify the user repoints the SAME
        // record to a remote host (same id, no longer a managed WSL gateway). The pre-restart guard must
        // re-validate the full record and abort — not restart a WSL distro for a now-remote gateway.
        var coordinator = CreateCoordinator(
            restarter,
            probeReachable: () => true,
            reconnectIfCurrent: (_, _) => Task.FromResult(true),
            isOperatorConnected: () =>
            {
                if (!mutated)
                {
                    mutated = true;
                    _registry.AddOrUpdate(new GatewayRecord { Id = "gw-local", Url = "wss://remote.example" });
                }
                return false;
            });

        var result = await coordinator.TryRepairActiveGatewayAsync();

        Assert.Equal(ManagedLocalGatewayRepairOutcome.AbortedGatewayChanged, result.Outcome);
        Assert.Equal(0, restarter.Calls);
    }

    [Fact]
    public async Task RecordRepointedToDifferentManagedPortDuringVerify_AbortsBeforeRestart()
    {
        SetActiveManagedLocal();
        var restarter = new FakeRestarter();
        var mutated = false;
        var coordinator = CreateCoordinator(
            restarter,
            probeReachable: () => true,
            reconnectIfCurrent: (_, _) => Task.FromResult(true),
            isOperatorConnected: () =>
            {
                if (!mutated)
                {
                    mutated = true;
                    _registry.AddOrUpdate(new GatewayRecord
                    {
                        Id = "gw-local",
                        Url = "ws://localhost:18800",
                        IsLocal = true,
                        SetupManagedDistroName = "OpenClawGateway",
                    });
                }
                return false;
            });

        var result = await coordinator.TryRepairActiveGatewayAsync();

        Assert.Equal(ManagedLocalGatewayRepairOutcome.AbortedGatewayChanged, result.Outcome);
        Assert.Equal(0, restarter.Calls);
    }

    [Fact]
    public async Task RepairDeferred_WhenManualOpHoldsLifecycleLease()
    {
        SetActiveManagedLocal();
        var restarter = new FakeRestarter();
        // A manual gateway lifecycle op holds the shared lease → the coordinator's non-blocking acquire
        // returns null, so it must defer the destructive restart rather than run it concurrently.
        var coordinator = CreateCoordinator(
            restarter,
            probeReachable: () => false,
            reconnectIfCurrent: (_, _) => Task.FromResult(true),
            isOperatorConnected: () => false,
            tryAcquireLifecycleLease: () => null);

        var result = await coordinator.TryRepairActiveGatewayAsync();

        Assert.Equal(ManagedLocalGatewayRepairOutcome.AbortedGatewayChanged, result.Outcome);
        Assert.Equal(0, restarter.Calls);
    }

    [Fact]
    public async Task RepairAborts_WhenFailureIsNoLongerTransport()
    {
        SetActiveManagedLocal();
        var restarter = new FakeRestarter();
        // The operator failure became a non-transport terminal error (e.g. auth) during verify — a
        // distro restart cannot fix it, so the coordinator must abort instead of burning the budget.
        var coordinator = CreateCoordinator(
            restarter,
            probeReachable: () => false,
            reconnectIfCurrent: (_, _) => Task.FromResult(true),
            isOperatorConnected: () => false,
            isRestartStillWarranted: () => false);

        var result = await coordinator.TryRepairActiveGatewayAsync();

        Assert.Equal(ManagedLocalGatewayRepairOutcome.AbortedNonTransportFailure, result.Outcome);
        Assert.Equal(0, restarter.Calls);
    }

    [Fact]
    public async Task UserDisconnectedIntent_AbortsBeforeProbeOrRestart()
    {
        SetActiveManagedLocal();
        var restarter = new FakeRestarter();
        var probes = 0;
        var coordinator = new ManagedLocalGatewayRepairCoordinator(
            _registry,
            restarter,
            (_, _) => { probes++; return Task.FromResult(false); },
            (_, _) => Task.FromResult(true),
            () => false,
            _ => Task.CompletedTask,
            _diagnostics,
            NullLogger.Instance,
            verifyTimeout: TimeSpan.FromMilliseconds(100),
            isAutomaticRepairAllowed: _ => false);

        var result = await coordinator.TryRepairActiveGatewayAsync();

        Assert.Equal(ManagedLocalGatewayRepairOutcome.AbortedUserIntent, result.Outcome);
        Assert.Equal(0, probes);
        Assert.Equal(0, restarter.Calls);
    }

    [Fact]
    public async Task UserStopsDuringRestart_PostRestartReconnectDoesNotRun()
    {
        SetActiveManagedLocal();
        var allowed = true;
        var reconnects = 0;
        var restarter = new FakeRestarter
        {
            Gate = () =>
            {
                allowed = false;
                return Task.CompletedTask;
            }
        };
        var coordinator = CreateCoordinator(
            restarter,
            probeReachable: () => false,
            reconnectIfCurrent: (_, _) => { reconnects++; return Task.FromResult(true); },
            isOperatorConnected: () => false,
            isAutomaticRepairAllowed: _ => allowed);

        var result = await coordinator.TryRepairActiveGatewayAsync();

        Assert.Equal(ManagedLocalGatewayRepairOutcome.AbortedUserIntent, result.Outcome);
        Assert.Equal(1, restarter.Calls);
        Assert.Equal(0, reconnects);
    }

    [Fact]
    public async Task ProvenNativePortConflict_IsRemovedBeforeWslRestart()
    {
        SetActiveManagedLocal();
        var restarter = new FakeRestarter();
        var portRepairCalls = 0;
        var coordinator = CreateCoordinator(
            restarter,
            probeReachable: () => true,
            reconnectIfCurrent: (_, _) => Task.FromResult(true),
            isOperatorConnected: () => true,
            repairPortConflictAsync: (_, _) =>
            {
                portRepairCalls++;
                return Task.FromResult(new ManagedLocalPortConflictRepairResult(
                    ManagedLocalPortConflictRepairOutcome.Repaired));
            },
            isPortConflictCandidate: () => true);

        var result = await coordinator.TryRepairActiveGatewayAsync();

        Assert.Equal(ManagedLocalGatewayRepairOutcome.Repaired, result.Outcome);
        Assert.Equal(1, portRepairCalls);
        Assert.Equal(1, restarter.Calls); // skips reachable probe path and rebinds WSL relay
    }

    [Fact]
    public async Task UnknownPortOwner_IsBlockedWithoutRestart()
    {
        SetActiveManagedLocal();
        var restarter = new FakeRestarter();
        var coordinator = CreateCoordinator(
            restarter,
            probeReachable: () => true,
            reconnectIfCurrent: (_, _) => Task.FromResult(true),
            isOperatorConnected: () => false,
            repairPortConflictAsync: (_, _) => Task.FromResult(
                new ManagedLocalPortConflictRepairResult(
                    ManagedLocalPortConflictRepairOutcome.BlockedUnknownOwner,
                    "unknown owner")),
            isPortConflictCandidate: () => true);

        var result = await coordinator.TryRepairActiveGatewayAsync();

        Assert.Equal(ManagedLocalGatewayRepairOutcome.PortConflictBlocked, result.Outcome);
        Assert.Equal(0, restarter.Calls);
    }

    [Fact]
    public async Task RestartBudget_IsPerGateway_AlternatingDoesNotResetOtherGateway()
    {
        // Two managed-local gateways: A exhausts its own restart budget; B has an independent budget;
        // returning to A does NOT grant a fresh budget (the bound is keyed per gateway id, so
        // alternating gateways cannot bypass it).
        AddManagedLocal("gw-a", "DistroA", 18801);
        AddManagedLocal("gw-b", "DistroB", 18802);
        var restarter = new FakeRestarter();
        // Unreachable + never verifies: each attempt restarts once then ends PendingVerification.
        var coordinator = CreateCoordinator(
            restarter, () => false, (_, _) => Task.FromResult(true), () => false, maxRestarts: 1);

        _registry.SetActive("gw-a");
        Assert.Equal(ManagedLocalGatewayRepairOutcome.ReconnectPendingVerification, (await coordinator.TryRepairActiveGatewayAsync()).Outcome);
        Assert.Equal(ManagedLocalGatewayRepairOutcome.AttemptsExhausted, (await coordinator.TryRepairActiveGatewayAsync()).Outcome);

        _registry.SetActive("gw-b");
        Assert.Equal(ManagedLocalGatewayRepairOutcome.ReconnectPendingVerification, (await coordinator.TryRepairActiveGatewayAsync()).Outcome);
        Assert.Equal(ManagedLocalGatewayRepairOutcome.AttemptsExhausted, (await coordinator.TryRepairActiveGatewayAsync()).Outcome);

        _registry.SetActive("gw-a");
        Assert.Equal(ManagedLocalGatewayRepairOutcome.AttemptsExhausted, (await coordinator.TryRepairActiveGatewayAsync()).Outcome);

        Assert.Equal(2, restarter.Calls); // exactly one restart for A and one for B; A never restarted twice
    }

    [Fact]
    public async Task ResetAttemptBudget_ForOneGateway_DoesNotResetAnother()
    {
        AddManagedLocal("gw-a", "DistroA", 18811);
        AddManagedLocal("gw-b", "DistroB", 18812);
        var restarter = new FakeRestarter();
        var coordinator = CreateCoordinator(
            restarter, () => false, (_, _) => Task.FromResult(true), () => false, maxRestarts: 1);

        _registry.SetActive("gw-a");
        await coordinator.TryRepairActiveGatewayAsync();                       // A restart #1
        Assert.Equal(ManagedLocalGatewayRepairOutcome.AttemptsExhausted, (await coordinator.TryRepairActiveGatewayAsync()).Outcome);

        _registry.SetActive("gw-b");
        await coordinator.TryRepairActiveGatewayAsync();                       // B restart #1
        Assert.Equal(ManagedLocalGatewayRepairOutcome.AttemptsExhausted, (await coordinator.TryRepairActiveGatewayAsync()).Outcome);

        // Reset ONLY gateway A's budget.
        coordinator.ResetAttemptBudget("gw-a");

        _registry.SetActive("gw-a");
        Assert.Equal(ManagedLocalGatewayRepairOutcome.ReconnectPendingVerification, (await coordinator.TryRepairActiveGatewayAsync()).Outcome); // A can restart again
        _registry.SetActive("gw-b");
        Assert.Equal(ManagedLocalGatewayRepairOutcome.AttemptsExhausted, (await coordinator.TryRepairActiveGatewayAsync()).Outcome); // B still exhausted
    }

    [Fact]
    public async Task ProbeUnreachable_RestartsRearmsReconnectsAndVerifies()
    {
        SetActiveManagedLocal();
        var restarter = new FakeRestarter();
        var reconnects = 0;
        var rearms = 0;
        var coordinator = CreateCoordinator(
            restarter,
            probeReachable: () => false,
            (_, _) => { reconnects++; return Task.FromResult(true); },
            () => restarter.Calls > 0,
            onReArm: () => rearms++);

        var result = await coordinator.TryRepairActiveGatewayAsync();

        Assert.Equal(ManagedLocalGatewayRepairOutcome.Repaired, result.Outcome);
        Assert.Equal("OpenClawGateway", result.DistroName);
        Assert.Equal(1, restarter.Calls);
        Assert.Equal(1, rearms);   // keepalive re-armed after restart
        Assert.Equal(0, reconnects); // operator recovered during restart; do not tear down the healthy client
    }

    [Fact]
    public async Task OperatorRecoversDuringRestart_RearmsKeepAliveAndReportsRepaired()
    {
        SetActiveManagedLocal();
        var transportFailure = true;
        var operatorConnected = false;
        var rearms = 0;
        var reconnects = 0;
        var restarter = new FakeRestarter
        {
            Gate = () =>
            {
                transportFailure = false;
                operatorConnected = true;
                return Task.CompletedTask;
            }
        };
        var coordinator = CreateCoordinator(
            restarter,
            probeReachable: () => false,
            (_, _) =>
            {
                reconnects++;
                return Task.FromResult(true);
            },
            isOperatorConnected: () => operatorConnected,
            onReArm: () => rearms++,
            isRestartStillWarranted: () => transportFailure);

        var result = await coordinator.TryRepairActiveGatewayAsync();

        Assert.Equal(ManagedLocalGatewayRepairOutcome.Repaired, result.Outcome);
        Assert.Equal(1, restarter.Calls);
        Assert.Equal(1, rearms);
        Assert.Equal(0, reconnects);
    }

    [Fact]
    public async Task RestartFails_DoesNotReconnect()
    {
        SetActiveManagedLocal();
        var restarter = new FakeRestarter { Result = new ManagedLocalGatewayRestartResult(false, "boom") };
        var reconnects = 0;
        var coordinator = CreateCoordinator(
            restarter, () => false, (_, _) => { reconnects++; return Task.FromResult(true); }, () => true);

        var result = await coordinator.TryRepairActiveGatewayAsync();

        Assert.Equal(ManagedLocalGatewayRepairOutcome.RestartFailed, result.Outcome);
        Assert.Equal(1, restarter.Calls);
        Assert.Equal(0, reconnects);
    }

    [Fact]
    public async Task RestartBudget_ExhaustsThenResetAllowsAgain()
    {
        SetActiveManagedLocal();
        var restarter = new FakeRestarter();
        // Unreachable + operator never connects: each attempt restarts and ends PendingVerification.
        var coordinator = CreateCoordinator(
            restarter, () => false, (_, _) => Task.FromResult(true), () => false, maxRestarts: 2);

        Assert.Equal(ManagedLocalGatewayRepairOutcome.ReconnectPendingVerification, (await coordinator.TryRepairActiveGatewayAsync()).Outcome);
        Assert.Equal(ManagedLocalGatewayRepairOutcome.ReconnectPendingVerification, (await coordinator.TryRepairActiveGatewayAsync()).Outcome);
        Assert.Equal(ManagedLocalGatewayRepairOutcome.AttemptsExhausted, (await coordinator.TryRepairActiveGatewayAsync()).Outcome);
        Assert.Equal(2, restarter.Calls); // exhausted attempt did not restart again

        coordinator.ResetAttemptBudget();

        Assert.Equal(ManagedLocalGatewayRepairOutcome.ReconnectPendingVerification, (await coordinator.TryRepairActiveGatewayAsync()).Outcome);
        Assert.Equal(3, restarter.Calls);
    }

    [Fact]
    public async Task CancelledDuringRestart_DoesNotReconnect()
    {
        SetActiveManagedLocal();
        using var cts = new CancellationTokenSource();
        var reconnects = 0;
        var restarter = new FakeRestarter { Gate = () => { cts.Cancel(); return Task.CompletedTask; } };
        var coordinator = CreateCoordinator(
            restarter, () => false, (_, _) => { reconnects++; return Task.FromResult(true); }, () => true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.TryRepairActiveGatewayAsync(cts.Token));

        Assert.Equal(1, restarter.Calls);
        Assert.Equal(0, reconnects);
    }

    [Fact]
    public async Task GatewaySwitchDuringRepair_PinnedReconnectDoesNotReportRepaired()
    {
        SetActiveManagedLocal();
        _registry.AddOrUpdate(new GatewayRecord { Id = "gw-other", Url = "wss://remote.example" });
        var restarter = new FakeRestarter();
        // The reconnect simulates a gateway switch: pinned reconnect returns false (active changed).
        var coordinator = CreateCoordinator(
            restarter,
            probeReachable: () => false,
            reconnectIfCurrent: (_, _) => { _registry.SetActive("gw-other"); return Task.FromResult(false); },
            isOperatorConnected: () => false);

        var result = await coordinator.TryRepairActiveGatewayAsync();

        // Restart targeted the ORIGINAL distro (captured before the switch), but the pinned reconnect
        // no-ops once the active gateway changed — so the repair aborts cleanly rather than reporting a
        // false success (or driving the switched-to gateway).
        Assert.Equal("OpenClawGateway", restarter.LastDistro);
        Assert.Equal(ManagedLocalGatewayRepairOutcome.AbortedGatewayChanged, result.Outcome);
    }

    private sealed class FakeRestarter : IManagedLocalGatewayRestarter
    {
        public int Calls;
        public string? LastDistro;
        public ManagedLocalGatewayRestartResult Result = new(true);
        public Func<Task>? Gate;

        public async Task<ManagedLocalGatewayRestartResult> RestartAsync(
            string distroName,
            CancellationToken cancellationToken,
            Func<bool>? canContinue = null)
        {
            if (canContinue is not null && !canContinue())
                return new ManagedLocalGatewayRestartResult(false, "Gateway changed before restart.");
            Calls++;
            LastDistro = distroName;
            if (Gate != null)
                await Gate();
            cancellationToken.ThrowIfCancellationRequested();
            return Result;
        }
    }
}
