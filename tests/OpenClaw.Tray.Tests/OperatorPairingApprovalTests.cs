using OpenClawTray.Services.LocalGatewaySetup;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Bug 1 (e2e drive 2026-05-04): the bootstrap-token connect handshake correctly delivers
/// the upstream token, but the gateway records it as a pending operator pairing request and
/// rejects the same connect with PairingRequired. On a local-loopback gateway, the user
/// driving the tray is also the operator, so the setup engine must auto-approve the pending
/// request via the gateway CLI before retrying. These tests pin the auto-approve + retry
/// behavior of <see cref="SettingsOperatorPairingService"/>.
/// </summary>
public class OperatorPairingApprovalTests
{
    [Fact]
    public async Task PairAsync_LocalLoopback_BootstrapToken_PairingRequired_ApprovesAndRetries()
    {
        var settings = new FakePairingSettings { BootstrapToken = "redacted-bootstrap-token" };
        var connector = new ScriptedConnector(
            new GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus.PairingRequired, "pairing required"),
            new GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus.Connected),
            // ConnectWithStoredDeviceTokenAsync after redeem
            new GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus.Connected));
        var approver = new RecordingApprover(new PendingDeviceApprovalResult(true));
        var service = new SettingsOperatorPairingService(settings, connector, approver);
        var state = new LocalGatewaySetupState { GatewayUrl = "ws://127.0.0.1:18789", DistroName = "OpenClawGateway" };

        var result = await service.PairAsync(state);

        Assert.True(result.Success);
        Assert.Equal(2, connector.ConnectCalls);
        Assert.Equal(1, connector.ConnectWithStoredDeviceTokenCalls);
        Assert.Equal(1, approver.ApproveCalls);
        Assert.Equal("ws://127.0.0.1:18789", approver.LastGatewayUrl);
        Assert.Equal("OpenClawGateway", approver.LastDistroName);
    }

    [Fact]
    public async Task PairAsync_LocalLoopback_BootstrapToken_PairingRequiredTwice_FailsWithoutLooping()
    {
        var settings = new FakePairingSettings { BootstrapToken = "redacted-bootstrap-token" };
        var connector = new ScriptedConnector(
            new GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus.PairingRequired, "pairing required"),
            new GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus.PairingRequired, "still pending"));
        var approver = new RecordingApprover(new PendingDeviceApprovalResult(true));
        var service = new SettingsOperatorPairingService(settings, connector, approver);

        var result = await service.PairAsync(new LocalGatewaySetupState { GatewayUrl = "ws://127.0.0.1:18789", DistroName = "OpenClawGateway" });

        Assert.False(result.Success);
        Assert.Equal("operator_pairing_required", result.ErrorCode);
        Assert.Equal(2, connector.ConnectCalls);
        Assert.Equal(1, approver.ApproveCalls);
    }

    [Fact]
    public async Task PairAsync_LocalLoopback_BootstrapToken_ApprovalFails_ReturnsApprovalError()
    {
        var settings = new FakePairingSettings { BootstrapToken = "redacted-bootstrap-token" };
        var connector = new ScriptedConnector(
            new GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus.PairingRequired, "pairing required"));
        var approver = new RecordingApprover(new PendingDeviceApprovalResult(false, "operator_pending_approval_failed", "no pending requests"));
        var service = new SettingsOperatorPairingService(settings, connector, approver);

        var result = await service.PairAsync(new LocalGatewaySetupState { GatewayUrl = "ws://127.0.0.1:18789", DistroName = "OpenClawGateway" });

        Assert.False(result.Success);
        Assert.Equal("operator_pending_approval_failed", result.ErrorCode);
        Assert.Equal(1, connector.ConnectCalls);
        Assert.Equal(1, approver.ApproveCalls);
    }

    [Fact]
    public async Task PairAsync_RemoteGateway_PairingRequired_DoesNotApprove()
    {
        var settings = new FakePairingSettings { BootstrapToken = "redacted-bootstrap-token" };
        var connector = new ScriptedConnector(
            new GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus.PairingRequired, "pairing required"));
        var approver = new RecordingApprover(new PendingDeviceApprovalResult(true));
        var service = new SettingsOperatorPairingService(settings, connector, approver);

        var result = await service.PairAsync(new LocalGatewaySetupState { GatewayUrl = "ws://gateway.example.com:18789", DistroName = "OpenClawGateway" });

        Assert.False(result.Success);
        Assert.Equal("operator_pairing_required", result.ErrorCode);
        Assert.Equal(0, approver.ApproveCalls);
    }

    [Fact]
    public async Task PairAsync_NonBootstrapToken_PairingRequired_DoesNotApprove()
    {
        // A previously-paired device whose deviceToken got revoked should NOT trigger an
        // auto-approval — that path indicates a deeper problem and re-approving here would
        // mask it.
        var settings = new FakePairingSettings { Token = "redacted-explicit-gateway-token" };
        var connector = new ScriptedConnector(
            new GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus.PairingRequired, "pairing required"));
        var approver = new RecordingApprover(new PendingDeviceApprovalResult(true));
        var service = new SettingsOperatorPairingService(settings, connector, approver);

        var result = await service.PairAsync(new LocalGatewaySetupState { GatewayUrl = "ws://127.0.0.1:18789", DistroName = "OpenClawGateway" });

        Assert.False(result.Success);
        Assert.Equal("operator_pairing_required", result.ErrorCode);
        Assert.Equal(0, approver.ApproveCalls);
    }

    [Fact]
    public async Task PairAsync_FirstConnectSucceeds_NoApprovalCall()
    {
        var settings = new FakePairingSettings { BootstrapToken = "redacted-bootstrap-token" };
        var connector = new ScriptedConnector(
            new GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus.Connected),
            new GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus.Connected));
        var approver = new RecordingApprover(new PendingDeviceApprovalResult(true));
        var service = new SettingsOperatorPairingService(settings, connector, approver);

        var result = await service.PairAsync(new LocalGatewaySetupState { GatewayUrl = "ws://127.0.0.1:18789", DistroName = "OpenClawGateway" });

        Assert.True(result.Success);
        Assert.Equal(0, approver.ApproveCalls);
    }

    [Fact]
    public void ParseApproveJson_OkResponse_ReturnsSuccess()
    {
        var result = WslGatewayCliPendingDeviceApprover.ParseApproveJson("{\"ok\":true,\"requestId\":\"abc\"}");

        Assert.True(result.Success);
    }

    [Fact]
    public void ParseApproveJson_OkFalse_ReturnsFailure()
    {
        var result = WslGatewayCliPendingDeviceApprover.ParseApproveJson("{\"ok\":false,\"error\":\"no pending requests\"}");

        Assert.False(result.Success);
        Assert.Equal("operator_pending_approval_failed", result.ErrorCode);
        Assert.Equal("no pending requests", result.ErrorMessage);
    }

    [Fact]
    public void ParseApproveJson_EmptyOutput_ReturnsSuccess()
    {
        var result = WslGatewayCliPendingDeviceApprover.ParseApproveJson(string.Empty);

        Assert.True(result.Success);
    }

    [Fact]
    public void ParseApproveJson_NonJsonOutput_ReturnsSuccess()
    {
        // Older CLI versions print plain text on success. Treat as success when exit was 0.
        var result = WslGatewayCliPendingDeviceApprover.ParseApproveJson("approved request abc");

        Assert.True(result.Success);
    }

    private sealed class FakePairingSettings : ILocalGatewaySetupSettings
    {
        public string GatewayUrl { get; set; } = "";
        public string Token { get; set; } = "";
        public string BootstrapToken { get; set; } = "";
        public bool UseSshTunnel { get; set; } = true;
        public bool EnableNodeMode { get; set; }
        public void Save() { }
    }

    private sealed class ScriptedConnector : IGatewayOperatorConnector
    {
        private readonly Queue<GatewayOperatorConnectionResult> _connectResults;
        public int ConnectCalls { get; private set; }
        public int ConnectWithStoredDeviceTokenCalls { get; private set; }

        public ScriptedConnector(params GatewayOperatorConnectionResult[] connectResults)
        {
            _connectResults = new Queue<GatewayOperatorConnectionResult>(connectResults);
        }

        public Task<GatewayOperatorConnectionResult> ConnectAsync(string gatewayUrl, string token, bool tokenIsBootstrapToken = false, CancellationToken cancellationToken = default)
        {
            ConnectCalls++;
            return Task.FromResult(_connectResults.Count > 0
                ? _connectResults.Dequeue()
                : new GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus.Failed, "no scripted result"));
        }

        public Task<GatewayOperatorConnectionResult> ConnectWithStoredDeviceTokenAsync(string gatewayUrl, CancellationToken cancellationToken = default)
        {
            ConnectWithStoredDeviceTokenCalls++;
            return Task.FromResult(_connectResults.Count > 0
                ? _connectResults.Dequeue()
                : new GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus.Connected));
        }
    }

    private sealed class RecordingApprover : IPendingDeviceApprover
    {
        private readonly PendingDeviceApprovalResult _result;
        public int ApproveCalls { get; private set; }
        public string? LastGatewayUrl { get; private set; }
        public string? LastDistroName { get; private set; }

        public RecordingApprover(PendingDeviceApprovalResult result) => _result = result;

        public Task<PendingDeviceApprovalResult> ApproveLatestAsync(LocalGatewaySetupState state, CancellationToken cancellationToken = default)
        {
            ApproveCalls++;
            LastGatewayUrl = state.GatewayUrl;
            LastDistroName = state.DistroName;
            return Task.FromResult(_result);
        }
    }
}
