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

    // --- Bug 1 residual fix (CLI v2026.5.3-1 / commit 2eae30e) regression tests ---

    [Fact]
    public async Task WslGatewayCliPendingDeviceApprover_DoesNotPassUrlOverride_AvoidingEnsureExplicitGatewayAuthGuard()
    {
        // The upstream CLI v2026.5.3-1 (commit 2eae30e) `ensureExplicitGatewayAuth`
        // (src/gateway/call.ts) rejects `--url` overrides with the error
        // "gateway url override requires explicit credentials" unless explicit auth is supplied
        // in the precise shape it expects. Drop `--url` so the CLI resolves the loopback URL
        // from the in-distro `openclaw.json` (gateway.mode=local) instead.
        //
        // Bug 1 part 3: with the two-stage approve, BOTH stages must omit `--url`.
        var runner = new RecordingWslRunner(
            new WslCommandResult(0, "{\"selected\":{\"requestId\":\"abc-123\"},\"approveCommand\":\"openclaw devices approve abc-123 --json\"}", string.Empty),
            new WslCommandResult(0, "{\"requestId\":\"abc-123\",\"device\":{}}", string.Empty));
        var approver = new WslGatewayCliPendingDeviceApprover(runner, "/opt/openclaw/bin/openclaw");
        var state = new LocalGatewaySetupState { GatewayUrl = "ws://127.0.0.1:18789", DistroName = "OpenClawGateway" };

        var result = await approver.ApproveLatestAsync(state);

        Assert.True(result.Success);
        Assert.Equal(2, runner.RunInDistroCommands.Count);
        foreach (var cmd in runner.RunInDistroCommands)
        {
            Assert.Equal("OpenClawGateway", runner.LastDistroName);
            var script = string.Join(" ", cmd);
            Assert.DoesNotContain("--url", script);
            Assert.DoesNotContain("ws://127.0.0.1:18789", script);
            Assert.Contains("devices", script);
            Assert.Contains("approve", script);
            Assert.Contains("--json", script);
            Assert.Contains("--token", script);
            // Token value is dereferenced inside the shell so it never appears on argv.
            Assert.Contains("$(cat /var/lib/openclaw/gateway-token)", script);
        }
    }

    [Fact]
    public async Task WslGatewayCliPendingDeviceApprover_NonZeroExit_SurfacesStructuredFailureCode()
    {
        // Pin the failure surface for the v2026.5.3-1 stderr that originally regressed Bug 1.
        var stderr = "[openclaw] Failed to start CLI: Error: gateway url override requires explicit credentials\n"
                     + "Fix: pass --token *** --password *** gatewayToken in tools).\n"
                     + "    at ensureExplicitGatewayAuth (.../call-BCpe65RR.js:148:8)";
        var runner = new RecordingWslRunner(new WslCommandResult(1, string.Empty, stderr));
        var approver = new WslGatewayCliPendingDeviceApprover(runner, "/opt/openclaw/bin/openclaw");

        var result = await approver.ApproveLatestAsync(new LocalGatewaySetupState
        {
            GatewayUrl = "ws://127.0.0.1:18789",
            DistroName = "OpenClawGateway",
        });

        Assert.False(result.Success);
        Assert.Equal("operator_pending_approval_failed", result.ErrorCode);
        Assert.Equal("Local gateway pending pairing approval CLI failed (preview stage).", result.ErrorMessage);
    }

    // --- Bug 1 part 3 (two-stage approve, CLI v2026.5.3-1) regression tests ---

    [Fact]
    public async Task WslGatewayCliPendingDeviceApprover_TwoStage_PreviewThenCommit_Succeeds()
    {
        // Stage 1 returns the v2026.5.3-1 preview shape with selected.requestId; stage 2
        // performs the actual approve and returns the gateway's mutation result.
        var previewJson = "{\"selected\":{\"requestId\":\"57ccdbad-24a7-4750-8e5d-e92c5c497da0\","
                          + "\"deviceId\":\"c5979c9c\"},\"approvalState\":{\"kind\":\"new-pairing\","
                          + "\"requested\":{},\"approved\":null},"
                          + "\"approveCommand\":\"openclaw devices approve 57ccdbad-24a7-4750-8e5d-e92c5c497da0 --json\","
                          + "\"requiresAuthFlags\":{\"token\":false,\"password\":false}}";
        var commitJson = "{\"requestId\":\"57ccdbad-24a7-4750-8e5d-e92c5c497da0\",\"device\":{\"deviceId\":\"c5979c9c\"}}";
        var runner = new RecordingWslRunner(
            new WslCommandResult(0, previewJson, string.Empty),
            new WslCommandResult(0, commitJson, string.Empty));
        var approver = new WslGatewayCliPendingDeviceApprover(runner, "/opt/openclaw/bin/openclaw");

        var result = await approver.ApproveLatestAsync(new LocalGatewaySetupState
        {
            GatewayUrl = "ws://127.0.0.1:18789",
            DistroName = "OpenClawGateway",
        });

        Assert.True(result.Success);
        Assert.Equal(2, runner.RunInDistroCommands.Count);

        // Stage 1: preview (--latest, no requestId argv).
        var stage1 = string.Join(" ", runner.RunInDistroCommands[0]);
        Assert.Contains("--latest", stage1);
        Assert.DoesNotContain("57ccdbad-24a7-4750-8e5d-e92c5c497da0", stage1);

        // Stage 2: commit (explicit requestId, no --latest).
        var stage2 = string.Join(" ", runner.RunInDistroCommands[1]);
        Assert.DoesNotContain("--latest", stage2);
        Assert.Contains("'57ccdbad-24a7-4750-8e5d-e92c5c497da0'", stage2);
        Assert.Contains("--json", stage2);
        Assert.Contains("--token", stage2);
        Assert.DoesNotContain("--url", stage2);
    }

    [Fact]
    public async Task WslGatewayCliPendingDeviceApprover_TwoStage_PreviewEmpty_NoPendingEntries()
    {
        // Stage 1 returns empty stdout (CLI prints "No pending device pairing requests" to
        // stderr and exits — we observed exit-0 in the wild on v2026.5.3-1). Engine must
        // see a distinct error code so it does not treat it as success and does not
        // infinite-loop retrying the WS connect.
        var runner = new RecordingWslRunner(new WslCommandResult(0, string.Empty, "No pending device pairing requests to approve"));
        var approver = new WslGatewayCliPendingDeviceApprover(runner, "/opt/openclaw/bin/openclaw");

        var result = await approver.ApproveLatestAsync(new LocalGatewaySetupState
        {
            GatewayUrl = "ws://127.0.0.1:18789",
            DistroName = "OpenClawGateway",
        });

        Assert.False(result.Success);
        Assert.Equal("no_pending_entries", result.ErrorCode);
        Assert.Single(runner.RunInDistroCommands); // stage 2 must NOT have run.
    }

    [Fact]
    public async Task WslGatewayCliPendingDeviceApprover_TwoStage_CommitFails_SurfacesStructuredFailure()
    {
        var previewJson = "{\"selected\":{\"requestId\":\"abc-123\"},"
                          + "\"approveCommand\":\"openclaw devices approve abc-123 --json\"}";
        var runner = new RecordingWslRunner(
            new WslCommandResult(0, previewJson, string.Empty),
            new WslCommandResult(1, string.Empty, "unknown requestId"));
        var approver = new WslGatewayCliPendingDeviceApprover(runner, "/opt/openclaw/bin/openclaw");

        var result = await approver.ApproveLatestAsync(new LocalGatewaySetupState
        {
            GatewayUrl = "ws://127.0.0.1:18789",
            DistroName = "OpenClawGateway",
        });

        Assert.False(result.Success);
        Assert.Equal("operator_pending_approval_failed", result.ErrorCode);
        Assert.Equal("unknown requestId", result.ErrorMessage);
        Assert.Equal(2, runner.RunInDistroCommands.Count);
    }

    [Fact]
    public async Task WslGatewayCliPendingDeviceApprover_TwoStage_PreviewReturnsUnsafeRequestId_DoesNotRunCommit()
    {
        // Defense-in-depth: if the CLI ever returns a requestId containing shell
        // metacharacters, refuse to interpolate it into a `bash -lc` script.
        var previewJson = "{\"selected\":{\"requestId\":\"abc; rm -rf /\"}}";
        var runner = new RecordingWslRunner(new WslCommandResult(0, previewJson, string.Empty));
        var approver = new WslGatewayCliPendingDeviceApprover(runner, "/opt/openclaw/bin/openclaw");

        var result = await approver.ApproveLatestAsync(new LocalGatewaySetupState
        {
            GatewayUrl = "ws://127.0.0.1:18789",
            DistroName = "OpenClawGateway",
        });

        Assert.False(result.Success);
        Assert.Equal("operator_pending_approval_failed", result.ErrorCode);
        Assert.Single(runner.RunInDistroCommands);
    }

    [Fact]
    public void ParsePreviewJson_V20265_Shape_ReturnsRequestId()
    {
        var json = "{\"selected\":{\"requestId\":\"57ccdbad-24a7-4750-8e5d-e92c5c497da0\"},"
                   + "\"approveCommand\":\"openclaw devices approve 57ccdbad-24a7-4750-8e5d-e92c5c497da0 --json\"}";

        var result = WslGatewayCliPendingDeviceApprover.ParsePreviewJson(json);

        Assert.True(result.Success);
        Assert.Equal("57ccdbad-24a7-4750-8e5d-e92c5c497da0", result.RequestId);
    }

    [Fact]
    public void ParsePreviewJson_Empty_ReturnsNoPendingEntries()
    {
        var result = WslGatewayCliPendingDeviceApprover.ParsePreviewJson(string.Empty);

        Assert.False(result.Success);
        Assert.Equal("no_pending_entries", result.ErrorCode);
    }

    [Fact]
    public void ParsePreviewJson_OkFalse_ReturnsApprovalFailure()
    {
        var result = WslGatewayCliPendingDeviceApprover.ParsePreviewJson("{\"ok\":false,\"error\":\"boom\"}");

        Assert.False(result.Success);
        Assert.Equal("operator_pending_approval_failed", result.ErrorCode);
        Assert.Equal("boom", result.ErrorMessage);
    }

    private sealed class RecordingWslRunner : IWslCommandRunner
    {
        private readonly Queue<WslCommandResult> _results;
        private readonly WslCommandResult _fallback;
        public string? LastDistroName { get; private set; }
        public IReadOnlyList<string>? LastCommand { get; private set; }
        public List<IReadOnlyList<string>> RunInDistroCommands { get; } = new();

        public RecordingWslRunner(params WslCommandResult[] results)
        {
            _results = new Queue<WslCommandResult>(results);
            _fallback = results.Length > 0 ? results[^1] : new WslCommandResult(0, string.Empty, string.Empty);
        }

        public Task<WslCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(new WslCommandResult(0, string.Empty, string.Empty));

        public Task<IReadOnlyList<WslDistroInfo>> ListDistrosAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WslDistroInfo>>(Array.Empty<WslDistroInfo>());

        public Task<WslCommandResult> TerminateDistroAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(new WslCommandResult(0, string.Empty, string.Empty));

        public Task<WslCommandResult> UnregisterDistroAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(new WslCommandResult(0, string.Empty, string.Empty));

        public Task<WslCommandResult> RunInDistroAsync(string name, IReadOnlyList<string> command, CancellationToken cancellationToken = default)
        {
            LastDistroName = name;
            LastCommand = command;
            RunInDistroCommands.Add(command);
            var next = _results.Count > 0 ? _results.Dequeue() : _fallback;
            return Task.FromResult(next);
        }
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
