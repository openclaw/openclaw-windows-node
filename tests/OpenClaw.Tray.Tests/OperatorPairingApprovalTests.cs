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
        // Bug 1 part 5: the gateway token is read via a separate `cat` call (cmd[0])
        // so it never lands as `$(...)` shell substitution in the approve script body.
        var runner = new RecordingWslRunner(
            new WslCommandResult(0, "test-token-abcdef\n", string.Empty),
            new WslCommandResult(0, "{\"selected\":{\"requestId\":\"abc-123\"},\"approveCommand\":\"openclaw devices approve abc-123 --json\"}", string.Empty),
            new WslCommandResult(0, "{\"requestId\":\"abc-123\",\"device\":{}}", string.Empty));
        var approver = new WslGatewayCliPendingDeviceApprover(runner, "/opt/openclaw/bin/openclaw");
        var state = new LocalGatewaySetupState { GatewayUrl = "ws://127.0.0.1:18789", DistroName = "OpenClawGateway" };

        var result = await approver.ApproveLatestAsync(state);

        Assert.True(result.Success);
        Assert.Equal(3, runner.RunInDistroCommands.Count);
        // cmd[0] = token-read (separate cat invocation, NOT the approve script).
        var tokenReadCmd = string.Join(" ", runner.RunInDistroCommands[0]);
        Assert.Contains("cat /var/lib/openclaw/gateway-token", tokenReadCmd);
        Assert.DoesNotContain("devices", tokenReadCmd);
        Assert.DoesNotContain("approve", tokenReadCmd);
        // cmd[1..] = approve scripts. They must satisfy --url=null and (NEW) must not
        // contain ANY embedded `$(...)` shell substitution or `"` characters — both of
        // which can be mangled by .NET ArgumentList / wsl.exe argv encoding (Bug 1 part 5).
        for (var i = 1; i < runner.RunInDistroCommands.Count; i++)
        {
            var script = string.Join(" ", runner.RunInDistroCommands[i]);
            Assert.Equal("OpenClawGateway", runner.LastDistroName);
            Assert.DoesNotContain("--url", script);
            Assert.DoesNotContain("ws://127.0.0.1:18789", script);
            Assert.Contains("devices", script);
            Assert.Contains("approve", script);
            Assert.Contains("--json", script);
            Assert.Contains("--token", script);
            // Bug 1 part 5: NO embedded shell substitution and NO double-quotes anywhere
            // in the script body. Previous embedded `"$(cat ...)"` is gone.
            Assert.DoesNotContain("$(", script);
            Assert.DoesNotContain("\"", script);
            // Token is interpolated as a single-quoted literal.
            Assert.Contains("'test-token-abcdef'", script);
        }
    }

    [Fact]
    public async Task WslGatewayCliPendingDeviceApprover_NonZeroExit_SurfacesStructuredFailureCode()
    {
        // Pin the failure surface for the v2026.5.3-1 stderr that originally regressed Bug 1.
        var stderr = "[openclaw] Failed to start CLI: Error: gateway url override requires explicit credentials\n"
                     + "Fix: pass --token *** --password *** gatewayToken in tools).\n"
                     + "    at ensureExplicitGatewayAuth (.../call-BCpe65RR.js:148:8)";
        var runner = new RecordingWslRunner(
            new WslCommandResult(0, "tok\n", string.Empty),
            new WslCommandResult(1, string.Empty, stderr));
        var approver = new WslGatewayCliPendingDeviceApprover(runner, "/opt/openclaw/bin/openclaw", TimeSpan.Zero);

        var result = await approver.ApproveLatestAsync(new LocalGatewaySetupState
        {
            GatewayUrl = "ws://127.0.0.1:18789",
            DistroName = "OpenClawGateway",
        });

        Assert.False(result.Success);
        Assert.Equal("operator_pending_approval_failed", result.ErrorCode);
        // Bug 1 part 4: stage-1 stderr is now surfaced for diagnosability, AND stage 1 is
        // retried once on first failure. With the same stderr returned on both attempts,
        // we surface the prefix + attempt-1 stderr only (attempt-2 is suppressed when equal).
        Assert.NotNull(result.ErrorMessage);
        Assert.StartsWith("Local gateway pending pairing approval CLI failed (preview stage).", result.ErrorMessage);
        Assert.Contains("stage1.attempt1.stderr=", result.ErrorMessage!);
        Assert.Contains("ensureExplicitGatewayAuth", result.ErrorMessage!);
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
            new WslCommandResult(0, "tok\n", string.Empty),
            new WslCommandResult(0, previewJson, string.Empty),
            new WslCommandResult(0, commitJson, string.Empty));
        var approver = new WslGatewayCliPendingDeviceApprover(runner, "/opt/openclaw/bin/openclaw");

        var result = await approver.ApproveLatestAsync(new LocalGatewaySetupState
        {
            GatewayUrl = "ws://127.0.0.1:18789",
            DistroName = "OpenClawGateway",
        });

        Assert.True(result.Success);
        Assert.Equal(3, runner.RunInDistroCommands.Count);

        // cmd[1] = Stage 1 preview (--latest, no requestId argv).
        var stage1 = string.Join(" ", runner.RunInDistroCommands[1]);
        Assert.Contains("--latest", stage1);
        Assert.DoesNotContain("57ccdbad-24a7-4750-8e5d-e92c5c497da0", stage1);

        // cmd[2] = Stage 2 commit (explicit requestId, no --latest).
        var stage2 = string.Join(" ", runner.RunInDistroCommands[2]);
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
        var runner = new RecordingWslRunner(
            new WslCommandResult(0, "tok\n", string.Empty),
            new WslCommandResult(0, string.Empty, "No pending device pairing requests to approve"));
        var approver = new WslGatewayCliPendingDeviceApprover(runner, "/opt/openclaw/bin/openclaw");

        var result = await approver.ApproveLatestAsync(new LocalGatewaySetupState
        {
            GatewayUrl = "ws://127.0.0.1:18789",
            DistroName = "OpenClawGateway",
        });

        Assert.False(result.Success);
        Assert.Equal("no_pending_entries", result.ErrorCode);
        // 2 calls: token-read + stage-1. Stage 2 must NOT have run.
        Assert.Equal(2, runner.RunInDistroCommands.Count);
    }

    [Fact]
    public async Task WslGatewayCliPendingDeviceApprover_TwoStage_CommitFails_SurfacesStructuredFailure()
    {
        var previewJson = "{\"selected\":{\"requestId\":\"abc-123\"},"
                          + "\"approveCommand\":\"openclaw devices approve abc-123 --json\"}";
        var runner = new RecordingWslRunner(
            new WslCommandResult(0, "tok\n", string.Empty),
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
        // Backwards-compat: when stage-2 has stderr-only (no stdout), surface bare stderr.
        Assert.Equal("unknown requestId", result.ErrorMessage);
        Assert.Equal(3, runner.RunInDistroCommands.Count);
    }

    [Fact]
    public async Task WslGatewayCliPendingDeviceApprover_TwoStage_PreviewReturnsUnsafeRequestId_DoesNotRunCommit()
    {
        // Defense-in-depth: if the CLI ever returns a requestId containing shell
        // metacharacters, refuse to interpolate it into a `bash -lc` script.
        var previewJson = "{\"selected\":{\"requestId\":\"abc; rm -rf /\"}}";
        var runner = new RecordingWslRunner(
            new WslCommandResult(0, "tok\n", string.Empty),
            new WslCommandResult(0, previewJson, string.Empty));
        var approver = new WslGatewayCliPendingDeviceApprover(runner, "/opt/openclaw/bin/openclaw");

        var result = await approver.ApproveLatestAsync(new LocalGatewaySetupState
        {
            GatewayUrl = "ws://127.0.0.1:18789",
            DistroName = "OpenClawGateway",
        });

        Assert.False(result.Success);
        Assert.Equal("operator_pending_approval_failed", result.ErrorCode);
        // 2 calls: token-read + stage-1 preview. Stage 2 must not run.
        Assert.Equal(2, runner.RunInDistroCommands.Count);
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

    // --- Bug 1 part 4 (first-call race retry + stderr surfacing) regression tests ---

    [Fact]
    public async Task WslGatewayCliPendingDeviceApprover_TwoStage_Stage1FailsThenSucceeds_OverallSuccess()
    {
        // Bug 1 part 4 race: the engine's first --token call into the in-distro CLI
        // triggers an internal Linux-operator auto-bootstrap that exits the CLI process
        // non-zero. A second invocation made shortly after succeeds because the internal
        // operator is now pre-paired. Approver retries stage 1 once on first failure.
        var previewJson = "{\"selected\":{\"requestId\":\"81ff1b4c-ff71-4432-99c2-54b6b214982d\"}}";
        var commitJson = "{\"requestId\":\"81ff1b4c-ff71-4432-99c2-54b6b214982d\",\"device\":{}}";
        var runner = new RecordingWslRunner(
            new WslCommandResult(0, "tok\n", string.Empty),
            new WslCommandResult(1, string.Empty, "auto-bootstrap pairing in progress"),
            new WslCommandResult(0, previewJson, string.Empty),
            new WslCommandResult(0, commitJson, string.Empty));
        var approver = new WslGatewayCliPendingDeviceApprover(runner, "/opt/openclaw/bin/openclaw", TimeSpan.Zero);

        var result = await approver.ApproveLatestAsync(new LocalGatewaySetupState
        {
            GatewayUrl = "ws://127.0.0.1:18789",
            DistroName = "OpenClawGateway",
        });

        Assert.True(result.Success);
        // 4 calls: token-read + stage-1 attempt 1 + stage-1 attempt 2 + stage-2.
        Assert.Equal(4, runner.RunInDistroCommands.Count);
        // Both stage-1 attempts (cmd[1], cmd[2]) must use --latest; stage 2 contains the requestId.
        Assert.Contains("--latest", string.Join(" ", runner.RunInDistroCommands[1]));
        Assert.Contains("--latest", string.Join(" ", runner.RunInDistroCommands[2]));
        Assert.Contains("'81ff1b4c-ff71-4432-99c2-54b6b214982d'", string.Join(" ", runner.RunInDistroCommands[3]));
    }

    [Fact]
    public async Task WslGatewayCliPendingDeviceApprover_TwoStage_Stage1FailsTwice_SurfacesBothStderrs()
    {
        var firstStderr = "first attempt: bootstrap pairing in progress";
        var secondStderr = "second attempt: gateway returned 500";
        var runner = new RecordingWslRunner(
            new WslCommandResult(0, "tok\n", string.Empty),
            new WslCommandResult(1, string.Empty, firstStderr),
            new WslCommandResult(2, string.Empty, secondStderr));
        var approver = new WslGatewayCliPendingDeviceApprover(runner, "/opt/openclaw/bin/openclaw", TimeSpan.Zero);

        var result = await approver.ApproveLatestAsync(new LocalGatewaySetupState
        {
            GatewayUrl = "ws://127.0.0.1:18789",
            DistroName = "OpenClawGateway",
        });

        Assert.False(result.Success);
        Assert.Equal("operator_pending_approval_failed", result.ErrorCode);
        Assert.NotNull(result.ErrorMessage);
        Assert.StartsWith("Local gateway pending pairing approval CLI failed (preview stage).", result.ErrorMessage);
        Assert.Contains("stage1.attempt1.stderr=" + firstStderr, result.ErrorMessage!);
        Assert.Contains("stage1.attempt2.stderr=" + secondStderr, result.ErrorMessage!);
        Assert.Contains("stage1.attempt1.exit=1", result.ErrorMessage!);
        Assert.Contains("stage1.attempt2.exit=2", result.ErrorMessage!);
        // 3 calls: token-read + 2 stage-1 attempts. Stage 2 must NOT have run.
        Assert.Equal(3, runner.RunInDistroCommands.Count);
    }

    [Fact]
    public void TruncateStderr_RespectsCap_AndAppendsTruncationMarker()
    {
        var huge = new string('x', WslGatewayCliPendingDeviceApprover.MaxStderrSurfaceLength + 200);

        var truncated = WslGatewayCliPendingDeviceApprover.TruncateStderr(huge);

        Assert.NotNull(truncated);
        Assert.True(truncated!.Length <= WslGatewayCliPendingDeviceApprover.MaxStderrSurfaceLength + "…[truncated]".Length);
        Assert.EndsWith("…[truncated]", truncated);

        var small = WslGatewayCliPendingDeviceApprover.TruncateStderr("short");
        Assert.Equal("short", small);

        Assert.Null(WslGatewayCliPendingDeviceApprover.TruncateStderr(null));
        Assert.Null(WslGatewayCliPendingDeviceApprover.TruncateStderr("   \r\n  "));
    }

    // --- Bug 1 part 5 (token-read in C# + stdout surfacing) regression tests ---

    [Fact]
    public async Task WslGatewayCliPendingDeviceApprover_PreviewScript_HasNoEmbeddedShellSubstitutionOrDoubleQuotes()
    {
        // The leading hypothesis for Bostick-11 Round-3's "exit non-zero + EMPTY stderr"
        // failure is that .NET ArgumentList / wsl.exe argv encoding mangles the embedded
        // `"$(cat /var/lib/openclaw/gateway-token)"` substitution. Bug 1 part 5 reads the
        // token in C# and interpolates it as a single-quoted shell literal so the script
        // body contains NO `$(...)` substitution and NO `"` characters at all.
        var runner = new RecordingWslRunner(
            new WslCommandResult(0, "TOKEN-VALUE-XYZ\n", string.Empty),
            new WslCommandResult(0, "{\"selected\":{\"requestId\":\"abc-123\"}}", string.Empty),
            new WslCommandResult(0, "{\"requestId\":\"abc-123\"}", string.Empty));
        var approver = new WslGatewayCliPendingDeviceApprover(runner, "/opt/openclaw/bin/openclaw");

        await approver.ApproveLatestAsync(new LocalGatewaySetupState
        {
            GatewayUrl = "ws://127.0.0.1:18789",
            DistroName = "OpenClawGateway",
        });

        Assert.Equal(3, runner.RunInDistroCommands.Count);
        for (var i = 1; i < runner.RunInDistroCommands.Count; i++)
        {
            var script = string.Join(" ", runner.RunInDistroCommands[i]);
            Assert.DoesNotContain("$(", script);
            Assert.DoesNotContain("\"", script);
            Assert.DoesNotContain("cat /var/lib/openclaw/gateway-token", script);
            Assert.Contains("'TOKEN-VALUE-XYZ'", script);
        }
    }

    [Fact]
    public async Task WslGatewayCliPendingDeviceApprover_TokenReadFails_SurfacesStructuredFailure_NoApproveScriptRuns()
    {
        var runner = new RecordingWslRunner(
            new WslCommandResult(1, string.Empty, "cat: /var/lib/openclaw/gateway-token: No such file or directory"));
        var approver = new WslGatewayCliPendingDeviceApprover(runner, "/opt/openclaw/bin/openclaw", TimeSpan.Zero);

        var result = await approver.ApproveLatestAsync(new LocalGatewaySetupState
        {
            GatewayUrl = "ws://127.0.0.1:18789",
            DistroName = "OpenClawGateway",
        });

        Assert.False(result.Success);
        Assert.Equal("operator_pending_approval_failed", result.ErrorCode);
        Assert.Contains("token-read stage", result.ErrorMessage!);
        Assert.Contains("No such file", result.ErrorMessage!);
        Assert.Single(runner.RunInDistroCommands);
    }

    [Fact]
    public async Task WslGatewayCliPendingDeviceApprover_TokenReadEmpty_SurfacesStructuredFailure()
    {
        var runner = new RecordingWslRunner(
            new WslCommandResult(0, "   \n", string.Empty));
        var approver = new WslGatewayCliPendingDeviceApprover(runner, "/opt/openclaw/bin/openclaw", TimeSpan.Zero);

        var result = await approver.ApproveLatestAsync(new LocalGatewaySetupState
        {
            GatewayUrl = "ws://127.0.0.1:18789",
            DistroName = "OpenClawGateway",
        });

        Assert.False(result.Success);
        Assert.Equal("operator_pending_approval_failed", result.ErrorCode);
        Assert.Contains("token file empty", result.ErrorMessage!);
        Assert.Single(runner.RunInDistroCommands);
    }

    [Fact]
    public async Task WslGatewayCliPendingDeviceApprover_TokenWithUnsafeCharacters_RejectedBeforeApprove()
    {
        // A token containing a single quote would break out of the single-quoted shell
        // literal. Reject before constructing any script. Same for newlines / control chars.
        var unsafeTokens = new[] { "tok'evil", "tok\nnewline", "tok\rcr", "tok\0null", "tok\x01ctl" };
        foreach (var bad in unsafeTokens)
        {
            var runner = new RecordingWslRunner(new WslCommandResult(0, bad, string.Empty));
            var approver = new WslGatewayCliPendingDeviceApprover(runner, "/opt/openclaw/bin/openclaw", TimeSpan.Zero);

            var result = await approver.ApproveLatestAsync(new LocalGatewaySetupState
            {
                GatewayUrl = "ws://127.0.0.1:18789",
                DistroName = "OpenClawGateway",
            });

            Assert.False(result.Success);
            Assert.Equal("operator_pending_approval_failed", result.ErrorCode);
            Assert.Contains("unsafe characters", result.ErrorMessage!);
            // No approve script should have run.
            Assert.Single(runner.RunInDistroCommands);
        }
    }

    [Fact]
    public async Task WslGatewayCliPendingDeviceApprover_Stage1FailureWithStdoutOnly_SurfacesStdout()
    {
        // Bug 1 part 5: stdout is now surfaced too (some CLI failure paths write the
        // structured error to stdout in `--json` mode, with empty stderr — exactly what
        // Bostick-11 Round-3 observed).
        var runner = new RecordingWslRunner(
            new WslCommandResult(0, "tok\n", string.Empty),
            new WslCommandResult(1, "{\"ok\":false,\"error\":\"json-mode error on stdout\"}", string.Empty),
            new WslCommandResult(2, "{\"ok\":false,\"error\":\"second attempt stdout error\"}", string.Empty));
        var approver = new WslGatewayCliPendingDeviceApprover(runner, "/opt/openclaw/bin/openclaw", TimeSpan.Zero);

        var result = await approver.ApproveLatestAsync(new LocalGatewaySetupState
        {
            GatewayUrl = "ws://127.0.0.1:18789",
            DistroName = "OpenClawGateway",
        });

        Assert.False(result.Success);
        Assert.Equal("operator_pending_approval_failed", result.ErrorCode);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("stage1.attempt1.stdout=", result.ErrorMessage!);
        Assert.Contains("json-mode error on stdout", result.ErrorMessage!);
        Assert.Contains("stage1.attempt2.stdout=", result.ErrorMessage!);
        Assert.Contains("second attempt stdout error", result.ErrorMessage!);
        Assert.Contains("stage1.attempt1.exit=1", result.ErrorMessage!);
        Assert.Contains("stage1.attempt2.exit=2", result.ErrorMessage!);
    }

    [Fact]
    public async Task WslGatewayCliPendingDeviceApprover_Stage2FailureWithStdoutOnly_SurfacesStdout()
    {
        var previewJson = "{\"selected\":{\"requestId\":\"abc-123\"}}";
        var runner = new RecordingWslRunner(
            new WslCommandResult(0, "tok\n", string.Empty),
            new WslCommandResult(0, previewJson, string.Empty),
            new WslCommandResult(1, "{\"ok\":false,\"error\":\"stage2 stdout-only error\"}", string.Empty));
        var approver = new WslGatewayCliPendingDeviceApprover(runner, "/opt/openclaw/bin/openclaw", TimeSpan.Zero);

        var result = await approver.ApproveLatestAsync(new LocalGatewaySetupState
        {
            GatewayUrl = "ws://127.0.0.1:18789",
            DistroName = "OpenClawGateway",
        });

        Assert.False(result.Success);
        Assert.Equal("operator_pending_approval_failed", result.ErrorCode);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("commit stage", result.ErrorMessage!);
        Assert.Contains("stage2.exit=1", result.ErrorMessage!);
        Assert.Contains("stage2.stdout=", result.ErrorMessage!);
        Assert.Contains("stage2 stdout-only error", result.ErrorMessage!);
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
