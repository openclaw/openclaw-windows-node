using OpenClawTray.Services.LocalGatewaySetup;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Bug 3 (Bostick-11 Round-5, 2026-05-05): Phase 14 (PairWindowsTrayNode) connect to a
/// fresh local-loopback gateway arrives as <c>reason=role-upgrade, isRepair=true</c>
/// (the device is already paired as <c>operator</c> from Phase 12 and is now asking for
/// the additional <c>node</c> role). The gateway parks the request on the pending list
/// and the connect attempt times out with <c>windows_node_pairing_failed</c>. There is
/// no auto-approve handler upstream for this path, so the engine must drive the same
/// <see cref="IPendingDeviceApprover"/> approval flow it now uses for Phase 12. These
/// tests pin the auto-approve + retry behavior of <see cref="SettingsWindowsTrayNodeProvisioner"/>.
/// </summary>
public class WindowsTrayNodePairingApprovalTests
{
    private const string LocalGatewayUrl = "ws://127.0.0.1:18789";
    private const string RemoteGatewayUrl = "ws://gateway.example.com:18789";

    [Fact]
    public async Task PairAsync_LocalLoopback_RoleUpgradePending_ApprovesAndRetries_Succeeds()
    {
        var settings = new FakeNodeSettings { Token = "redacted-device-token", BootstrapToken = "" };
        var connector = new ScriptedNodeConnector(
            // First connect: gateway parks role-upgrade pending entry, NodeService times out.
            new TimeoutException("Timed out waiting for the Windows tray node to pair with the gateway."),
            // Retry after approve: succeeds.
            null);
        var approver = new RecordingNodeApprover(new PendingDeviceApprovalResult(true));
        var service = new SettingsWindowsTrayNodeProvisioner(settings, connector, approver);
        var state = new LocalGatewaySetupState { GatewayUrl = LocalGatewayUrl, DistroName = "OpenClawGateway" };

        var result = await service.PairAsync(state);

        Assert.True(result.Success);
        Assert.Null(result.ErrorCode);
        Assert.Equal(2, connector.ConnectCalls);
        Assert.Equal(1, approver.ApproveCalls);
        Assert.Equal(0, approver.ApproveExplicitCalls);
        Assert.Equal(LocalGatewayUrl, approver.LastGatewayUrl);
        Assert.Equal("OpenClawGateway", approver.LastDistroName);
        Assert.True(settings.EnableNodeMode);
    }

    [Fact]
    public async Task PairAsync_LocalLoopback_RoleUpgradePending_UsesLatestApprovalPathNotExplicitRequestId()
    {
        var settings = new FakeNodeSettings { Token = "redacted-device-token", BootstrapToken = "" };
        var connector = new ScriptedNodeConnector(
            new TimeoutException("Timed out waiting for the Windows tray node to pair with the gateway."),
            null);
        var approver = new RecordingNodeApprover(new PendingDeviceApprovalResult(true));
        var service = new SettingsWindowsTrayNodeProvisioner(settings, connector, approver);

        var result = await service.PairAsync(new LocalGatewaySetupState { GatewayUrl = LocalGatewayUrl, DistroName = "OpenClawGateway" });

        Assert.True(result.Success);
        Assert.Equal(1, approver.ApproveCalls);
        Assert.Equal(0, approver.ApproveExplicitCalls);
        Assert.Null(approver.LastExplicitRequestId);
    }

    [Fact]
    public async Task PairAsync_LocalLoopback_RoleUpgradePending_ApproverFails_SurfacesStructuredFailure()
    {
        var settings = new FakeNodeSettings { Token = "redacted-device-token", BootstrapToken = "" };
        var connector = new ScriptedNodeConnector(
            new TimeoutException("Timed out waiting for the Windows tray node to pair with the gateway."));
        var approver = new RecordingNodeApprover(new PendingDeviceApprovalResult(
            false,
            "operator_pending_approval_failed",
            "Local gateway pending pairing approval CLI failed (commit stage). stage2.exit=1"));
        var service = new SettingsWindowsTrayNodeProvisioner(settings, connector, approver);

        var result = await service.PairAsync(new LocalGatewaySetupState { GatewayUrl = LocalGatewayUrl, DistroName = "OpenClawGateway" });

        Assert.False(result.Success);
        Assert.Equal("operator_pending_approval_failed", result.ErrorCode);
        Assert.Contains("stage2.exit=1", result.ErrorMessage);
        Assert.Equal(1, connector.ConnectCalls);
        Assert.Equal(1, approver.ApproveCalls);
    }

    [Fact]
    public async Task PairAsync_LocalLoopback_RoleUpgradePending_ApproverNoPendingEntries_SurfacesStructuredFailure()
    {
        var settings = new FakeNodeSettings { Token = "redacted-device-token", BootstrapToken = "" };
        var connector = new ScriptedNodeConnector(
            new TimeoutException("Timed out waiting for the Windows tray node to pair with the gateway."));
        var approver = new RecordingNodeApprover(new PendingDeviceApprovalResult(
            false,
            "no_pending_entries",
            "No pending device pairing requests to approve."));
        var service = new SettingsWindowsTrayNodeProvisioner(settings, connector, approver);

        var result = await service.PairAsync(new LocalGatewaySetupState { GatewayUrl = LocalGatewayUrl, DistroName = "OpenClawGateway" });

        Assert.False(result.Success);
        Assert.Equal("no_pending_entries", result.ErrorCode);
        Assert.Equal("No pending device pairing requests to approve.", result.ErrorMessage);
        Assert.Equal(1, connector.ConnectCalls);
        Assert.Equal(1, approver.ApproveCalls);
    }

    [Fact]
    public async Task PairAsync_LocalLoopback_RetryAfterApproveAlsoFails_SurfacesPairingFailed()
    {
        var settings = new FakeNodeSettings { Token = "redacted-device-token", BootstrapToken = "" };
        var connector = new ScriptedNodeConnector(
            new TimeoutException("first timeout"),
            new TimeoutException("still timing out after approve"));
        var approver = new RecordingNodeApprover(new PendingDeviceApprovalResult(true));
        var service = new SettingsWindowsTrayNodeProvisioner(settings, connector, approver);

        var result = await service.PairAsync(new LocalGatewaySetupState { GatewayUrl = LocalGatewayUrl, DistroName = "OpenClawGateway" });

        Assert.False(result.Success);
        Assert.Equal("windows_node_pairing_failed", result.ErrorCode);
        Assert.Equal("still timing out after approve", result.ErrorMessage);
        Assert.Equal(2, connector.ConnectCalls);
        Assert.Equal(1, approver.ApproveCalls);
    }

    [Fact]
    public async Task PairAsync_RemoteGateway_ConnectFails_DoesNotApprove()
    {
        var settings = new FakeNodeSettings { Token = "redacted-device-token", BootstrapToken = "" };
        var connector = new ScriptedNodeConnector(
            new TimeoutException("Timed out waiting for the Windows tray node to pair with the gateway."));
        var approver = new RecordingNodeApprover(new PendingDeviceApprovalResult(true));
        var service = new SettingsWindowsTrayNodeProvisioner(settings, connector, approver);

        var result = await service.PairAsync(new LocalGatewaySetupState { GatewayUrl = RemoteGatewayUrl, DistroName = "OpenClawGateway" });

        Assert.False(result.Success);
        Assert.Equal("windows_node_pairing_failed", result.ErrorCode);
        Assert.Equal(1, connector.ConnectCalls);
        Assert.Equal(0, approver.ApproveCalls);
    }

    [Fact]
    public async Task PairAsync_LocalLoopback_FirstConnectSucceeds_DoesNotApprove()
    {
        var settings = new FakeNodeSettings { Token = "redacted-device-token", BootstrapToken = "" };
        var connector = new ScriptedNodeConnector((Exception?)null);
        var approver = new RecordingNodeApprover(new PendingDeviceApprovalResult(true));
        var service = new SettingsWindowsTrayNodeProvisioner(settings, connector, approver);

        var result = await service.PairAsync(new LocalGatewaySetupState { GatewayUrl = LocalGatewayUrl, DistroName = "OpenClawGateway" });

        Assert.True(result.Success);
        Assert.Equal(1, connector.ConnectCalls);
        Assert.Equal(0, approver.ApproveCalls);
        Assert.True(settings.EnableNodeMode);
    }

    [Fact]
    public async Task PairAsync_LocalLoopback_NoApproverWired_PreservesLegacyFailureCode()
    {
        // Backstop: when no IPendingDeviceApprover is supplied (e.g. legacy callers), the
        // Phase-14 path must still surface windows_node_pairing_failed unchanged so existing
        // FailedRetryable consumers keep working.
        var settings = new FakeNodeSettings { Token = "redacted-device-token", BootstrapToken = "" };
        var connector = new ScriptedNodeConnector(
            new TimeoutException("Timed out waiting for the Windows tray node to pair with the gateway."));
        var service = new SettingsWindowsTrayNodeProvisioner(settings, connector, pendingApprover: null);

        var result = await service.PairAsync(new LocalGatewaySetupState { GatewayUrl = LocalGatewayUrl, DistroName = "OpenClawGateway" });

        Assert.False(result.Success);
        Assert.Equal("windows_node_pairing_failed", result.ErrorCode);
        Assert.Equal(1, connector.ConnectCalls);
    }

    [Fact]
    public async Task PairAsync_OperationCanceled_DoesNotApproveOrSwallow()
    {
        var settings = new FakeNodeSettings { Token = "redacted-device-token", BootstrapToken = "" };
        var connector = new ScriptedNodeConnector(new OperationCanceledException("cancelled by user"));
        var approver = new RecordingNodeApprover(new PendingDeviceApprovalResult(true));
        var service = new SettingsWindowsTrayNodeProvisioner(settings, connector, approver);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.PairAsync(new LocalGatewaySetupState { GatewayUrl = LocalGatewayUrl, DistroName = "OpenClawGateway" }));

        Assert.Equal(1, connector.ConnectCalls);
        Assert.Equal(0, approver.ApproveCalls);
    }

    private sealed class FakeNodeSettings : ILocalGatewaySetupSettings
    {
        public string GatewayUrl { get; set; } = "";
        public string Token { get; set; } = "";
        public string BootstrapToken { get; set; } = "";
        public bool UseSshTunnel { get; set; } = true;
        public bool EnableNodeMode { get; set; }
        public void Save() { }
    }

    private sealed class ScriptedNodeConnector : IWindowsNodeConnector
    {
        private readonly Queue<Exception?> _outcomes;
        public int ConnectCalls { get; private set; }

        public ScriptedNodeConnector(params Exception?[] outcomes)
        {
            _outcomes = new Queue<Exception?>(outcomes);
        }

        public Task ConnectAsync(string gatewayUrl, string token, string? bootstrapToken, CancellationToken cancellationToken = default)
        {
            ConnectCalls++;
            var ex = _outcomes.Count > 0 ? _outcomes.Dequeue() : null;
            return ex == null ? Task.CompletedTask : Task.FromException(ex);
        }
    }

    private sealed class RecordingNodeApprover : IPendingDeviceApprover
    {
        private readonly PendingDeviceApprovalResult _result;
        public int ApproveCalls { get; private set; }
        public int ApproveExplicitCalls { get; private set; }
        public string? LastGatewayUrl { get; private set; }
        public string? LastDistroName { get; private set; }
        public string? LastExplicitRequestId { get; private set; }

        public RecordingNodeApprover(PendingDeviceApprovalResult result) => _result = result;

        public Task<PendingDeviceApprovalResult> ApproveLatestAsync(LocalGatewaySetupState state, CancellationToken cancellationToken = default)
        {
            ApproveCalls++;
            LastGatewayUrl = state.GatewayUrl;
            LastDistroName = state.DistroName;
            return Task.FromResult(_result);
        }

        public Task<PendingDeviceApprovalResult> ApproveExplicitAsync(LocalGatewaySetupState state, string requestId, CancellationToken cancellationToken = default)
        {
            ApproveExplicitCalls++;
            LastGatewayUrl = state.GatewayUrl;
            LastDistroName = state.DistroName;
            LastExplicitRequestId = requestId;
            return Task.FromResult(_result);
        }
    }
}
