using System;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Services;
using Xunit;

namespace OpenClaw.Tray.Tests;

public class ManagedLocalGatewayAutoRepairMonitorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GatewayRegistry _registry;
    private readonly ConnectionDiagnostics _diagnostics = new();
    private readonly FakeClock _clock = new();
    private GatewayConnectionSnapshot _snapshot = GatewayConnectionSnapshot.Idle;
    private bool _enabled = true;
    private int _repairCount;
    private int _resetBudgetCount;
    private ManagedLocalGatewayRepairOutcome _repairOutcome = ManagedLocalGatewayRepairOutcome.Repaired;

    public ManagedLocalGatewayAutoRepairMonitorTests()
    {
        _tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-autorepair-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(_tempDir);
        _registry = new GatewayRegistry(_tempDir);
    }

    public void Dispose()
    {
        // slopwatch-ignore: SW003 Test cleanup is best-effort and must not hide the test outcome.
        try { System.IO.Directory.Delete(_tempDir, true); } catch { }
    }

    private void SetActiveManagedLocal() =>
        SetActive(new GatewayRecord
        {
            Id = "gw-local",
            Url = "ws://localhost:18789",
            IsLocal = true,
            SetupManagedDistroName = "OpenClawGateway"
        });

    private void SetActive(GatewayRecord record)
    {
        _registry.AddOrUpdate(record);
        _registry.SetActive(record.Id);
    }

    private ManagedLocalGatewayAutoRepairMonitor CreateMonitor(
        TimeSpan? startupGrace = null,
        Func<string, bool>? isAutomaticRepairAllowed = null) =>
        new(
            () => _snapshot,
            _registry,
            _ => { _repairCount++; return Task.FromResult(new ManagedLocalGatewayRepairResult(_repairOutcome, "OpenClawGateway")); },
            _ => _resetBudgetCount++,
            () => _enabled,
            _diagnostics,
            NullLogger.Instance,
            _clock,
            unhealthyThreshold: TimeSpan.FromSeconds(10),
            cooldown: TimeSpan.FromSeconds(30),
            startupGrace: startupGrace ?? TimeSpan.Zero,
            delay: (_, _) => Task.CompletedTask,
            isAutomaticRepairAllowed: isAutomaticRepairAllowed);

    private static GatewayConnectionSnapshot Op(
        RoleConnectionState state,
        string? error = null,
        GatewayErrorKind? kind = null) =>
        new() { OperatorState = state, OperatorError = error, OperatorErrorKind = kind };

    [Fact]
    public async Task NotEligibleGateway_NeverRepairs()
    {
        SetActive(new GatewayRecord { Id = "gw-remote", Url = "wss://remote.example" });
        _snapshot = Op(RoleConnectionState.Error, "connection refused");
        await using var monitor = CreateMonitor();

        await monitor.EvaluateOnceAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await monitor.EvaluateOnceAsync(CancellationToken.None);

        Assert.Equal(0, _repairCount);
    }

    [Fact]
    public async Task KillSwitchDisabled_NeverRepairs()
    {
        SetActiveManagedLocal();
        _enabled = false;
        _snapshot = Op(RoleConnectionState.Error, "connection refused");
        await using var monitor = CreateMonitor();

        await monitor.EvaluateOnceAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await monitor.EvaluateOnceAsync(CancellationToken.None);

        Assert.Equal(0, _repairCount);
    }

    [Fact]
    public async Task OperatorConnected_NoRepair_ResetsBudget()
    {
        SetActiveManagedLocal();
        _snapshot = Op(RoleConnectionState.Connected);
        await using var monitor = CreateMonitor();

        await monitor.EvaluateOnceAsync(CancellationToken.None);

        Assert.Equal(0, _repairCount);
        Assert.True(_resetBudgetCount >= 1);
    }

    [Fact]
    public async Task SnapshotForDifferentGateway_IsIgnored_NoBudgetResetNoRepair()
    {
        SetActiveManagedLocal(); // active = gw-local
        // During a switch the snapshot can still describe a DIFFERENT gateway. A Connected snapshot for
        // gw-other must NOT reset gw-local's restart budget (which would let alternating gateways bypass
        // the bound), and the tick must not act on gw-local at all.
        _snapshot = new GatewayConnectionSnapshot
        {
            OperatorState = RoleConnectionState.Connected,
            GatewayId = "gw-other",
        };
        await using var monitor = CreateMonitor();

        await monitor.EvaluateOnceAsync(CancellationToken.None);

        Assert.Equal(0, _resetBudgetCount); // gw-other's health not attributed to gw-local
        Assert.Equal(0, _repairCount);
    }

    [Fact]
    public async Task OperatorIdle_DoesNotRepair_SoManualDisconnectStands()
    {
        SetActiveManagedLocal();
        _snapshot = Op(RoleConnectionState.Idle);
        await using var monitor = CreateMonitor();

        await monitor.EvaluateOnceAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await monitor.EvaluateOnceAsync(CancellationToken.None);

        Assert.Equal(0, _repairCount);
    }

    [Theory]
    [InlineData("unauthorized")]
    [InlineData("AUTH_DEVICE_TOKEN_MISMATCH")]
    [InlineData("device token mismatch")]
    [InlineData("rate limit exceeded")]
    [InlineData("insufficient scope operator.admin")]
    [InlineData("TLS handshake failed")]
    public async Task AuthOrNonTransportError_DoesNotRepair(string operatorError)
    {
        SetActiveManagedLocal();
        _snapshot = Op(RoleConnectionState.Error, operatorError);
        await using var monitor = CreateMonitor();

        await monitor.EvaluateOnceAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await monitor.EvaluateOnceAsync(CancellationToken.None);

        Assert.Equal(0, _repairCount);
    }

    [Fact]
    public async Task TypedTlsFailure_WithGenericTransportText_DoesNotRepair()
    {
        SetActiveManagedLocal();
        _snapshot = Op(RoleConnectionState.Error, "Transport error", GatewayErrorKind.Tls);
        await using var monitor = CreateMonitor();

        await monitor.EvaluateOnceAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await monitor.EvaluateOnceAsync(CancellationToken.None);

        Assert.Equal(0, _repairCount);
    }

    [Fact]
    public async Task TypedLocalPortConflict_PastThreshold_Repairs()
    {
        SetActiveManagedLocal();
        _snapshot = Op(
            RoleConnectionState.Error,
            "gateway token mismatch",
            GatewayErrorKind.LocalPortConflict);
        await using var monitor = CreateMonitor();

        await monitor.EvaluateOnceAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(11));
        await monitor.EvaluateOnceAsync(CancellationToken.None);

        Assert.Equal(1, _repairCount);
    }

    [Fact]
    public async Task UserDisconnectedIntent_NeverRepairs()
    {
        SetActiveManagedLocal();
        _snapshot = Op(RoleConnectionState.Error, "connection refused", GatewayErrorKind.Network);
        await using var monitor = CreateMonitor(
            isAutomaticRepairAllowed: _ => false);

        await monitor.EvaluateOnceAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await monitor.EvaluateOnceAsync(CancellationToken.None);

        Assert.Equal(0, _repairCount);
    }

    [Fact]
    public async Task PairingRequired_DoesNotRepair()
    {
        SetActiveManagedLocal();
        _snapshot = new GatewayConnectionSnapshot
        {
            OperatorState = RoleConnectionState.Error,
            OperatorPairingRequired = true
        };
        await using var monitor = CreateMonitor();

        await monitor.EvaluateOnceAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await monitor.EvaluateOnceAsync(CancellationToken.None);

        Assert.Equal(0, _repairCount);
    }

    [Fact]
    public async Task TransportUnreachable_PastThreshold_Repairs()
    {
        SetActiveManagedLocal();
        _snapshot = Op(RoleConnectionState.Error, "connection refused");
        await using var monitor = CreateMonitor();

        await monitor.EvaluateOnceAsync(CancellationToken.None); // starts unhealthy timer
        _clock.Advance(TimeSpan.FromSeconds(11));                // past 10s threshold
        await monitor.EvaluateOnceAsync(CancellationToken.None);

        Assert.Equal(1, _repairCount);
    }

    [Fact]
    public async Task TransportUnreachable_BelowThreshold_NoRepair()
    {
        SetActiveManagedLocal();
        _snapshot = Op(RoleConnectionState.Error, "connection refused");
        await using var monitor = CreateMonitor();

        await monitor.EvaluateOnceAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(9)); // under threshold
        await monitor.EvaluateOnceAsync(CancellationToken.None);

        Assert.Equal(0, _repairCount);
    }

    [Fact]
    public async Task WithinStartupGrace_DoesNotRepair_EvenWhenUnhealthy()
    {
        SetActiveManagedLocal();
        _snapshot = Op(RoleConnectionState.Connecting); // cold start: Connecting, no classified error
        await using var monitor = CreateMonitor(startupGrace: TimeSpan.FromSeconds(45));

        // Unhealthy well past the threshold, but still inside the startup grace window.
        await monitor.EvaluateOnceAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(20));
        await monitor.EvaluateOnceAsync(CancellationToken.None);
        Assert.Equal(0, _repairCount);

        // After the grace window elapses and the threshold accrues, repair fires.
        _clock.Advance(TimeSpan.FromSeconds(40)); // now past 45s grace
        await monitor.EvaluateOnceAsync(CancellationToken.None); // re-arms timer post-grace
        _clock.Advance(TimeSpan.FromSeconds(11));
        await monitor.EvaluateOnceAsync(CancellationToken.None);
        Assert.Equal(1, _repairCount);
    }

    [Fact]
    public async Task WithinCooldown_DoesNotRepairAgain()
    {
        SetActiveManagedLocal();
        _snapshot = Op(RoleConnectionState.Error, "connection refused");
        _repairOutcome = ManagedLocalGatewayRepairOutcome.ReconnectPendingVerification;
        await using var monitor = CreateMonitor();

        await monitor.EvaluateOnceAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(11));
        await monitor.EvaluateOnceAsync(CancellationToken.None); // repair #1
        _clock.Advance(TimeSpan.FromSeconds(20)); // within 30s cooldown
        await monitor.EvaluateOnceAsync(CancellationToken.None);

        Assert.Equal(1, _repairCount);
    }

    private sealed class FakeClock : IClock
    {
        public DateTime UtcNow { get; private set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
