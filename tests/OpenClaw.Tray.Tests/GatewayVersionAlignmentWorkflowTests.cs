using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class GatewayVersionAlignmentWorkflowTests
{
    private static readonly GatewayRecord LocalGateway = new()
    {
        Id = "gateway-a",
        Url = "ws://127.0.0.1:18789",
        IsLocal = true,
        SetupManagedDistroName = "OpenClawGateway",
    };

    [Fact]
    public async Task CheckAsync_ReportsRecoveryGateWithoutPromptingOrUpdating()
    {
        var operations = new FakeOperations
        {
            ProbeResult = Result(GatewayVersionAlignmentState.RecoveryAvailable, rollbackPointId: "point-1"),
        };
        var confirmations = 0;
        var toasts = new List<string>();
        var workflow = CreateWorkflow(
            operations,
            confirm: (_, _) =>
            {
                confirmations++;
                return Task.FromResult(true);
            },
            toast: (title, _) => toasts.Add(title));

        await workflow.CheckAsync(LocalGateway.Id);
        await workflow.CheckAsync(LocalGateway.Id);

        Assert.Equal(0, confirmations);
        Assert.Equal(0, operations.UpdateCount);
        Assert.Equal(["Local Gateway needs attention"], toasts);
    }

    [Fact]
    public async Task CheckAsync_ResumesVerifiedPendingUpdateWithoutPrompt()
    {
        var operations = new FakeOperations
        {
            ProbeResult = Result(GatewayVersionAlignmentState.Aligned),
            HasPendingUpdate = true,
            UpdateResult = Result(GatewayVersionAlignmentState.Updated),
        };
        var confirmations = 0;
        var workflow = CreateWorkflow(
            operations,
            confirm: (_, _) =>
            {
                confirmations++;
                return Task.FromResult(true);
            });

        await workflow.CheckAsync(LocalGateway.Id);

        Assert.Equal(0, confirmations);
        Assert.Equal(1, operations.UpdateCount);
    }

    [Fact]
    public async Task CheckAsync_RevalidatesGatewayIdentityAfterConfirmation()
    {
        GatewayRecord? active = LocalGateway;
        var operations = new FakeOperations
        {
            ProbeResult = Result(
                GatewayVersionAlignmentState.Mismatch,
                installedVersion: "2026.7.1"),
        };
        var toasts = new List<string>();
        var workflow = CreateWorkflow(
            operations,
            activeGateway: () => active,
            confirm: (_, _) =>
            {
                active = LocalGateway with { Id = "gateway-b" };
                return Task.FromResult(true);
            },
            toast: (title, _) => toasts.Add(title));

        await workflow.CheckAsync(LocalGateway.Id);

        Assert.Equal(0, operations.UpdateCount);
        Assert.Contains("Local Gateway update canceled", toasts);
    }

    [Fact]
    public async Task CheckAsync_QueuesExactlyOneFollowUpWhileProbeIsInFlight()
    {
        var firstProbe = new TaskCompletionSource<GatewayVersionAlignmentResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new FakeOperations
        {
            ProbeAsyncOverride = _ => firstProbe.Task,
        };
        var workflow = CreateWorkflow(operations);

        var running = workflow.CheckAsync(LocalGateway.Id);
        await operations.FirstProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await workflow.CheckAsync(LocalGateway.Id);
        await workflow.CheckAsync(LocalGateway.Id);
        operations.ProbeAsyncOverride = _ => Task.FromResult(Result(GatewayVersionAlignmentState.Aligned));
        firstProbe.SetResult(Result(GatewayVersionAlignmentState.Aligned));
        await running;

        await WaitUntilAsync(() => operations.ProbeCount == 2);
        Assert.Equal(2, operations.ProbeCount);
    }

    [Fact]
    public async Task SendRequestAsync_RejectsChangedGatewayAndForwardsStableIdentity()
    {
        var connection = new FakeConnection
        {
            Snapshot = ConnectedSnapshot(LocalGateway.Id),
            OperatorIdentityValue = new object(),
            OperatorConnected = true,
        };
        var workflow = CreateWorkflow(new FakeOperations(), connection: connection);

        await workflow.SendRequestAsync(
            LocalGateway.Id,
            "request-1",
            "gateway.update",
            new { version = "2026.7.9" },
            1000,
            CancellationToken.None);

        Assert.Equal(1, connection.SendCount);
        connection.Snapshot = ConnectedSnapshot("gateway-b");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.SendRequestAsync(
                LocalGateway.Id,
                "request-2",
                "gateway.update",
                null,
                1000,
                CancellationToken.None));

        Assert.Contains("identity changed", error.Message);
        Assert.Equal(1, connection.SendCount);
    }

    [Fact]
    public async Task SynchronizeAsync_ReconnectsAndRequiresPairedNodeWhenEnabled()
    {
        var connection = new FakeConnection
        {
            Snapshot = ConnectedSnapshot(LocalGateway.Id, nodeConnected: true),
        };
        var workflow = CreateWorkflow(
            new FakeOperations(),
            connection: connection,
            nodeModeEnabled: true);

        await workflow.SynchronizeAsync(LocalGateway.Id, CancellationToken.None);

        Assert.Equal(1, connection.ReconnectCount);
        Assert.Equal(1, connection.EnsureNodeCount);
    }

    private static GatewayVersionAlignmentWorkflow CreateWorkflow(
        FakeOperations operations,
        Func<GatewayRecord?>? activeGateway = null,
        FakeConnection? connection = null,
        bool nodeModeEnabled = false,
        Func<GatewayVersionAlignmentResult, GatewayUpdateProtectionMode, Task<bool>>? confirm = null,
        Action<string, string>? toast = null)
    {
        return new GatewayVersionAlignmentWorkflow(
            activeGateway ?? (() => LocalGateway),
            connection ?? new FakeConnection
            {
                Snapshot = ConnectedSnapshot(LocalGateway.Id),
                OperatorIdentityValue = new object(),
                OperatorConnected = true,
            },
            operations,
            () => nodeModeEnabled,
            confirm ?? ((_, _) => Task.FromResult(false)),
            toast ?? ((_, _) => { }));
    }

    private static GatewayVersionAlignmentResult Result(
        GatewayVersionAlignmentState state,
        string? installedVersion = "2026.7.9",
        string? rollbackPointId = null) =>
        new(
            state,
            "2026.7.9",
            InstalledVersion: installedVersion,
            RollbackPointId: rollbackPointId);

    private static GatewayConnectionSnapshot ConnectedSnapshot(
        string gatewayId,
        bool nodeConnected = false) =>
        new()
        {
            GatewayId = gatewayId,
            OperatorState = RoleConnectionState.Connected,
            NodeState = nodeConnected ? RoleConnectionState.Connected : RoleConnectionState.Disabled,
            NodePairingStatus = nodeConnected ? PairingStatus.Paired : PairingStatus.Unknown,
        };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class FakeOperations : IGatewayVersionAlignmentOperations
    {
        public string RequiredVersion => "2026.7.9";
        public GatewayVersionAlignmentResult ProbeResult { get; set; } = Result(GatewayVersionAlignmentState.Aligned);
        public GatewayVersionAlignmentResult UpdateResult { get; set; } = Result(GatewayVersionAlignmentState.Updated);
        public bool HasPendingUpdate { get; set; }
        public int ProbeCount { get; private set; }
        public int UpdateCount { get; private set; }
        public TaskCompletionSource FirstProbeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<GatewayHostAccessPlan, Task<GatewayVersionAlignmentResult>>? ProbeAsyncOverride { get; set; }

        public IReadOnlyList<GatewayRollbackPointInfo> ListRollbackPoints() => [];
        public bool HasUnreadableRollbackReceipt() => false;
        public bool HasVerifiedPendingUpdate() => HasPendingUpdate;
        public GatewayUpdateProtectionMode ResolveProtectionMode(string sourceVersion) =>
            GatewayUpdateProtectionMode.NativeBackup;

        public Task<GatewayVersionAlignmentResult> ProbeAsync(
            GatewayHostAccessPlan accessPlan,
            CancellationToken cancellationToken = default)
        {
            ProbeCount++;
            FirstProbeStarted.TrySetResult();
            return ProbeAsyncOverride?.Invoke(accessPlan) ?? Task.FromResult(ProbeResult);
        }

        public Task<GatewayVersionAlignmentResult> UpdateAsync(
            GatewayHostAccessPlan accessPlan,
            CancellationToken cancellationToken = default)
        {
            UpdateCount++;
            return Task.FromResult(UpdateResult);
        }

        public Task<int> CleanupRollbackPointsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<GatewayVersionAlignmentResult> RestoreAsync(
            GatewayHostAccessPlan accessPlan,
            string rollbackPointId,
            string confirmedRollbackPointId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result(GatewayVersionAlignmentState.Restored));

        public Task<GatewayVersionAlignmentResult> ResolveNativeRecoveryAsync(
            GatewayHostAccessPlan accessPlan,
            string rollbackPointId,
            string confirmedRollbackPointId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result(GatewayVersionAlignmentState.RecoveryResolved));

        public GatewayVersionAlignmentResult CancelRestore(
            GatewayHostAccessPlan accessPlan,
            string rollbackPointId,
            string confirmedRollbackPointId) =>
            Result(GatewayVersionAlignmentState.RestoreCancelled);
    }

    private sealed class FakeConnection : IGatewayAlignmentConnection
    {
        public event EventHandler<GatewayConnectionSnapshot>? StateChanged;
        public GatewayConnectionSnapshot Snapshot { get; set; } = GatewayConnectionSnapshot.Idle;
        public GatewayConnectionSnapshot CurrentSnapshot => Snapshot;
        public object? OperatorIdentityValue { get; set; }
        public object? OperatorIdentity => OperatorIdentityValue;
        public bool OperatorConnected { get; set; }
        public bool IsOperatorConnected => OperatorConnected;
        public int ReconnectCount { get; private set; }
        public int EnsureNodeCount { get; private set; }
        public int SendCount { get; private set; }

        public Task ReconnectAsync()
        {
            ReconnectCount++;
            StateChanged?.Invoke(this, Snapshot);
            return Task.CompletedTask;
        }

        public Task EnsureNodeConnectedAsync(CancellationToken cancellationToken)
        {
            EnsureNodeCount++;
            return Task.CompletedTask;
        }

        public Task<JsonElement> SendCorrelatedRequestAsync(
            string expectedGatewayId,
            object expectedOperatorIdentity,
            string requestId,
            string method,
            object? parameters,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            if (!ReferenceEquals(expectedOperatorIdentity, OperatorIdentityValue))
                throw new InvalidOperationException("Operator identity changed.");
            SendCount++;
            return Task.FromResult(JsonDocument.Parse("{}").RootElement.Clone());
        }
    }
}
