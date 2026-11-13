using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

public sealed class PairNodeStep : SetupStep
{
    public override string Id => "pair-node";
    public override string DisplayName => "Pair node connection";
    public override RetryPolicy Retry => new(MaxAttempts: 3, InitialDelay: TimeSpan.FromSeconds(3));

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var gatewayUrl = ctx.GatewayUrl!;
        var token = SetupPairingCredentialPolicy.ResolveInitialPairingToken(ctx);

        if (string.IsNullOrEmpty(token))
            return StepResult.Terminal("No credential available for node pairing");

        var registry = new GatewayRegistry(ctx.DataDir, logger: new SetupOpenClawLogger(ctx.Logger));
        registry.Load();
        var record = registry.GetById(ctx.GatewayRecordId!);
        if (record == null)
            return StepResult.Fail("Gateway record not found in registry");

        var identityPath = registry.GetIdentityDirectory(record.Id);

        var reachability = await WindowsGatewayReachability.VerifyAsync(ctx, "node", ct);
        if (!reachability.IsSuccess)
            return reachability;

        var wsLogger = new SetupOpenClawLogger(ctx.Logger);
        WindowsNodeClient? client = null;
        SetupOperatorPairingSession? session = null;

        WindowsNodeClient CreateClient()
        {
            var created = new WindowsNodeClient(gatewayUrl, token, identityPath, logger: wsLogger);
            PairOperatorStep.ApplyReconnectAuthorization(created, ctx);
            created.UseV2Signature = true;
            // Register capabilities BEFORE connect — gateway stores them from hello message
            RegisterCapabilitiesFromConfig(created, ctx);
            return created;
        }

        try
        {
            // The operator session connects before the node socket, so verify-e2e's
            // operator connect stays the device's last connect.
            (session, var sessionFailure) = await SetupOperatorPairingSession.OpenAsync(ctx, identityPath, ct);
            if (sessionFailure is not null)
                return sessionFailure;

            var drainResult = await VerifyEndToEndStep.DrainPendingDeviceApprovalsAsync(ctx, session!, ct);
            if (!drainResult.IsSuccess)
                return drainResult;

            ctx.CurrentNodeApprovalBaseline = await ApprovalRequestHelper.CaptureSetupBaselineOnceAsync(
                ctx,
                session!,
                ApprovalRequestKind.Node,
                ct);

            // Phase 1: Connect (may get PAIRING_REQUIRED)
            client = CreateClient();

            var outcome = await WaitForNodeConnection(client, ctx, TimeSpan.FromSeconds(15), ct);

            if (outcome.Outcome == NodeConnectionOutcome.Connected)
            {
                ctx.NodeDeviceId = client.ShortDeviceId;
                ctx.Logger.Info($"Node connected directly: {ctx.NodeDeviceId}");
                return StepResult.Ok("Node connected and paired");
            }

            if (outcome.Outcome == NodeConnectionOutcome.PairingRequired)
            {
                if (!ctx.Config.AutoApprovePairing)
                    return StepResult.Fail("Node pairing required but auto-approve is disabled");

                ctx.Logger.Info("Node pairing required; auto-approving via operator session");
                await client.DisconnectAsync();
                client.Dispose();
                client = null;

                var approveResult = await AutoApproveNodePairing(ctx, session!, outcome.RequestId, ct);
                if (!approveResult.IsSuccess)
                    return approveResult;

                // Phase 2: Reconnect after approval
                client = CreateClient();
                outcome = await WaitForNodeConnection(client, ctx, TimeSpan.FromSeconds(20), ct);
                if (outcome.Outcome == NodeConnectionOutcome.PairingRequired)
                {
                    ctx.Logger.Info("Node still pending right after approval; retrying reconnect once");
                    await client.DisconnectAsync();
                    client.Dispose();
                    client = null;
                    await Task.Delay(PairOperatorStep.PostApprovalReconnectRetryDelay, ct);
                    client = CreateClient();
                    outcome = await WaitForNodeConnection(client, ctx, TimeSpan.FromSeconds(20), ct);
                }

                if (outcome.Outcome == NodeConnectionOutcome.Connected)
                {
                    ctx.NodeDeviceId = client.ShortDeviceId;
                    ctx.Logger.Info($"Node paired after approval: {ctx.NodeDeviceId}");
                    await client.DisconnectAsync();
                    client.Dispose();
                    client = null;

                    // Skip node finalization — the operator finalization in VerifyEndToEndStep
                    // will be the last connect, ensuring operator metadata is "current".
                    // Node finalization would rotate tokens and potentially invalidate the operator token.
                    ctx.Logger.Info("Node paired — skipping node finalization (operator finalization is last)");
                    return StepResult.Ok("Node paired successfully");
                }

                return NodeConnectionFailure(ctx, "Node reconnection after approval failed", outcome.Outcome);
            }

            return NodeConnectionFailure(ctx, "Node connection failed", outcome.Outcome);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Let a caller-driven cancel propagate so the pipeline reports Cancelled,
            // not a Failed step — the catch-all below would otherwise convert it back
            // into StepResult.Fail (same idiom as the other steps' cancel rethrow).
            throw;
        }
        catch (DeviceIdentityLoadException ex)
        {
            return SetupIdentityFailure.Terminal(ctx, "node pairing", ex);
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"Node pairing failed: {ex.Message}", ex);
        }
        finally
        {
            if (client != null)
            {
                await client.DisconnectAsync();
                client.Dispose();
            }

            if (session is not null)
                await session.DisposeAsync();
        }
    }

    private enum NodeConnectionOutcome { Connected, PairingRequired, Error, Timeout }

    private sealed record NodeConnectionResult(NodeConnectionOutcome Outcome, string? RequestId = null);

    private static StepResult NodeConnectionFailure(SetupContext ctx, string prefix, NodeConnectionOutcome outcome) =>
        outcome == NodeConnectionOutcome.Error && ctx.PairingEndpointTrustFailure is { } trustFailure
            ? trustFailure
            : StepResult.Fail($"{prefix}: {outcome}");

    private static async Task<NodeConnectionResult> WaitForNodeConnection(
        WindowsNodeClient client, SetupContext ctx, TimeSpan timeout, CancellationToken ct)
    {
        ctx.PairingEndpointTrustFailure = null;
        var tcs = new TaskCompletionSource<NodeConnectionResult>();
        string? pairingRequestId = null;

        void OnStatusChanged(object? sender, ConnectionStatus status)
        {
            ctx.Logger.Debug($"Node connection status: {status}");
            if (status == ConnectionStatus.Connected)
                tcs.TrySetResult(new NodeConnectionResult(NodeConnectionOutcome.Connected));
            else if (status == ConnectionStatus.Error)
                tcs.TrySetResult(new NodeConnectionResult(NodeConnectionOutcome.Error));
            else if (status == ConnectionStatus.Disconnected)
            {
                if (client.IsPendingApproval)
                    tcs.TrySetResult(new NodeConnectionResult(NodeConnectionOutcome.PairingRequired, pairingRequestId));
                else
                    tcs.TrySetResult(new NodeConnectionResult(NodeConnectionOutcome.Error));
            }
        }

        void OnPairingStatusChanged(object? sender, PairingStatusEventArgs args)
        {
            if (args.Status == PairingStatus.Pending && ApprovalRequestHelper.IsSafeRequestId(args.RequestId))
                pairingRequestId = args.RequestId;
        }

        client.StatusChanged += OnStatusChanged;
        client.PairingStatusChanged += OnPairingStatusChanged;

        try
        {
            await client.ConnectAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Only the internal CancelAfter(timeout) firing is a Timeout; a caller
            // (user aborting setup) cancelling `ct` must propagate so the pipeline
            // reports Cancelled, rather than being misreported as a node timeout.
            return new NodeConnectionResult(NodeConnectionOutcome.Timeout);
        }
        finally
        {
            client.StatusChanged -= OnStatusChanged;
            client.PairingStatusChanged -= OnPairingStatusChanged;
        }
    }

    internal static async Task<StepResult> AutoApproveNodePairing(
        SetupContext ctx,
        ISetupPairingRequests requests,
        string? requestId,
        CancellationToken ct)
    {
        var approvalKind = ApprovalRequestKind.Device;

        if (string.IsNullOrWhiteSpace(requestId))
        {
            var requestBaseline = ctx.CurrentNodeApprovalBaseline;
            if (requestBaseline is null || !requestBaseline.Success)
            {
                if (requestBaseline?.PluginNotFound == true)
                    return StepResult.Terminal(ApprovalRequestHelper.PluginNotFoundMessage);

                return StepResult.Fail(
                    requestBaseline?.Error ??
                    "The setup socket did not provide a node pairing request ID, and no pre-connect approval baseline is available.");
            }

            approvalKind = ApprovalRequestKind.Node;
            var pending = await requests.ListAsync(ApprovalRequestKind.Node, TimeSpan.FromSeconds(30), ct);

            ctx.Logger.Info($"Node pending list: {(pending.Success ? "ok" : pending.FailureDetail)}");

            if (!pending.Success)
            {
                if (ApprovalRequestHelper.IsPluginNotFoundError(pending.Output))
                    return StepResult.Terminal(ApprovalRequestHelper.PluginNotFoundMessage);
                return StepResult.Fail($"Could not list pending node pairing requests ({pending.FailureDetail}): {pending.Output}");
            }

            // Both setup sockets use the same per-gateway identity. NodeDeviceId is display-only.
            var parsed = ApprovalRequestHelper.TrySelectPendingRequestForDevice(
                pending.Output,
                ctx.OperatorDeviceId,
                requestBaseline.RequestIds,
                matchNodeId: true);
            if (!parsed.Success)
            {
                ctx.Logger.Warn($"Could not select node pairing request: {parsed.Error}");
                return StepResult.Fail(parsed.Error ?? "Could not find a safe pending node pairing request");
            }

            requestId = parsed.RequestId;
        }

        if (!ApprovalRequestHelper.IsSafeRequestId(requestId))
            return StepResult.Fail("Node pairing request ID contained unsafe characters");

        ctx.Logger.Info($"Approving node pairing request: {requestId}");

        var approve = await requests.ApproveAsync(approvalKind, requestId!, TimeSpan.FromSeconds(30), ct);

        ctx.Logger.Info($"Node approve result: {(approve.Success ? "ok" : approve.FailureDetail)}");

        return approve.Success
            ? StepResult.Ok($"Node approved: {requestId}")
            : ApprovalRequestHelper.IsPluginNotFoundError(approve.Output)
                ? StepResult.Terminal(ApprovalRequestHelper.PluginNotFoundMessage)
                : StepResult.Fail($"Node approval failed ({approve.FailureDetail}): {approve.Output}");
    }

    private static void RegisterCapabilitiesFromConfig(WindowsNodeClient client, SetupContext ctx)
    {
        var capabilities = ctx.Config.Capabilities.GetEnabledCapabilities();
        foreach (var (category, commands) in capabilities)
        {
            client.RegisterCapability(new StubNodeCapability(category, commands));
        }
        if (ctx.Config.Settings.NodeCameraEnabled && ctx.Config.Capabilities.Camera)
            client.SetPermission("camera.capture", true);
        if (ctx.Config.Settings.NodeScreenEnabled && ctx.Config.Capabilities.Screen)
            client.SetPermission("screen.record", true);

        ctx.Logger.Info($"Registered {capabilities.Count} capability categories with {capabilities.Sum(c => c.Commands.Length)} total commands");
    }

    public override Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        // Null node device token (mirrors old uninstall step 7 for node role)
        // Only clear if no external gateways remain (same logic as PairOperatorStep)
        var registry = new GatewayRegistry(ctx.DataDir, logger: new SetupOpenClawLogger(ctx.Logger));
        registry.Load();
        var hasExternalGateways = registry.GetAll().Any(r =>
            !r.IsLocal && !(r.SshTunnel is null && LocalGatewayUrlClassifier.IsLocalGatewayUrl(r.Url)));

        if (hasExternalGateways)
        {
            ctx.Logger.Info("[Uninstall] Preserving node device token — external gateway records remain");
        }
        else
        {
            var nodeCleared = DeviceIdentity.TryClearDeviceTokenForRole(ctx.DataDir, "node");
            ctx.Logger.Info(nodeCleared
                ? "[Uninstall] Cleared node device token"
                : "[Uninstall] Node device token already absent");
        }

        return Task.CompletedTask;
    }
}
