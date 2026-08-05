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
        var provenanceCheck = await PairOperatorStep.EnsurePairingEndpointTrustedAsync(ctx, ct);
        if (provenanceCheck is not null)
            return provenanceCheck;

        var drainResult = await VerifyEndToEndStep.DrainPendingDeviceApprovalsAsync(ctx, ct);
        if (!drainResult.IsSuccess)
            return drainResult;
        provenanceCheck = await PairOperatorStep.EnsurePairingEndpointTrustedAsync(ctx, ct);
        if (provenanceCheck is not null)
            return provenanceCheck;

        var wsLogger = new SetupOpenClawLogger(ctx.Logger);
        WindowsNodeClient? client = null;

        try
        {
            // Phase 1: Connect (may get PAIRING_REQUIRED)
            client = new WindowsNodeClient(gatewayUrl, token, identityPath, logger: wsLogger);
            PairOperatorStep.ApplyReconnectAuthorization(client, ctx);
            client.UseV2Signature = true;

            // Register capabilities BEFORE connect — gateway stores them from hello message
            RegisterCapabilitiesFromConfig(client, ctx);

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

                ctx.Logger.Info("Node pairing required — auto-approving via CLI");
                await client.DisconnectAsync();
                client.Dispose();
                client = null;

                var approveResult = await AutoApproveNodePairing(ctx, outcome.RequestId, ct);
                if (!approveResult.IsSuccess)
                    return approveResult;

                await Task.Delay(2000, ct);

                // Phase 2: Reconnect after approval
                provenanceCheck = await PairOperatorStep.EnsurePairingEndpointTrustedAsync(ctx, ct);
                if (provenanceCheck is not null)
                    return provenanceCheck;
                client = new WindowsNodeClient(gatewayUrl, token, identityPath, logger: wsLogger);
                PairOperatorStep.ApplyReconnectAuthorization(client, ctx);
                client.UseV2Signature = true;
                RegisterCapabilitiesFromConfig(client, ctx);

                outcome = await WaitForNodeConnection(client, ctx, TimeSpan.FromSeconds(20), ct);
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

                return StepResult.Fail($"Node reconnection after approval failed: {outcome.Outcome}");
            }

            return StepResult.Fail($"Node connection failed: {outcome.Outcome}");
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
        }
    }

    /// <summary>
    /// After node pairing, finalize by connecting with the node device token to avoid
    /// metadata-upgrade when the tray reconnects.
    /// </summary>
    private static async Task<StepResult> FinalizeNodeWithDeviceToken(
        SetupContext ctx, string gatewayUrl, string identityPath, IOpenClawLogger wsLogger, CancellationToken ct)
    {
        ctx.Logger.Info("Finalizing node: reconnect with node device token");

        var identity = new DeviceIdentity(identityPath);
        try
        {
            identity.Initialize();
        }
        catch (DeviceIdentityLoadException ex)
        {
            return SetupIdentityFailure.Terminal(ctx, "node finalization", ex);
        }
        var nodeToken = identity.NodeDeviceToken;

        if (string.IsNullOrEmpty(nodeToken))
        {
            ctx.Logger.Warn("No node device token stored after pairing — skipping node finalization");
            return StepResult.Ok("Node paired (no finalization needed)");
        }

        // Wait for grace period (same as operator finalization)
        ctx.Logger.Info("Waiting for gateway grace period before node finalization...");
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        var finalClient = new WindowsNodeClient(gatewayUrl, nodeToken, identityPath, logger: wsLogger);
        PairOperatorStep.ApplyReconnectAuthorization(finalClient, ctx);
        finalClient.UseV2Signature = true;

        try
        {
            var result = await WaitForNodeConnection(finalClient, ctx, TimeSpan.FromSeconds(15), ct);

            if (result.Outcome == NodeConnectionOutcome.Connected)
            {
                ctx.Logger.Info("Node finalization connected — tray will connect seamlessly");
                return StepResult.Ok("Node paired and finalized for tray");
            }

            if (result.Outcome == NodeConnectionOutcome.PairingRequired)
            {
                ctx.Logger.Info("Node metadata-upgrade detected — auto-approving");
                await finalClient.DisconnectAsync();
                finalClient.Dispose();
                finalClient = null;

                var approveResult = await AutoApproveNodePairing(ctx, result.RequestId, ct);
                if (!approveResult.IsSuccess)
                    return StepResult.Fail($"Node finalization approval failed: {approveResult.Message}");

                await Task.Delay(2000, ct);

                finalClient = new WindowsNodeClient(gatewayUrl, nodeToken, identityPath, logger: wsLogger);
                PairOperatorStep.ApplyReconnectAuthorization(finalClient, ctx);
                finalClient.UseV2Signature = true;
                var finalResult = await WaitForNodeConnection(finalClient, ctx, TimeSpan.FromSeconds(15), ct);

                if (finalResult.Outcome == NodeConnectionOutcome.Connected)
                {
                    ctx.Logger.Info("Node finalization approved — tray will connect seamlessly");
                    return StepResult.Ok("Node paired and finalized for tray");
                }

                return StepResult.Fail($"Node finalization failed after approval: {finalResult.Outcome}");
            }

            return StepResult.Fail($"Node finalization failed: {result.Outcome}");
        }
        finally
        {
            if (finalClient != null)
            {
                await finalClient.DisconnectAsync();
                finalClient.Dispose();
            }
        }
    }

    private enum NodeConnectionOutcome { Connected, PairingRequired, Error, Timeout }

    private sealed record NodeConnectionResult(NodeConnectionOutcome Outcome, string? RequestId = null);

    private static async Task<NodeConnectionResult> WaitForNodeConnection(
        WindowsNodeClient client, SetupContext ctx, TimeSpan timeout, CancellationToken ct)
    {
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

    internal static async Task<StepResult> AutoApproveNodePairing(SetupContext ctx, string? requestId, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        var token = ctx.SharedGatewayToken ?? ctx.BootstrapToken ?? throw new InvalidOperationException("No gateway token available for auto-approve");

        var env = new Dictionary<string, string> { ["OPENCLAW_GATEWAY_TOKEN"] = token };
        var approvalKind = ApprovalRequestKind.Device;

        if (string.IsNullOrWhiteSpace(requestId))
        {
            approvalKind = ApprovalRequestKind.Node;
            var pending = await ctx.Commands.RunInWslAsync(
                distro,
                $"""{ctx.WslPathPrefix} && openclaw nodes list --json""",
                TimeSpan.FromSeconds(30), env, ct);

            ctx.Logger.Info($"Node pending list: exit={pending.ExitCode}");

            if (pending.ExitCode != 0)
            {
                var pendingOutput = pending.Stdout.Trim();
                if (ApprovalRequestHelper.IsPluginNotFoundError(pendingOutput))
                    return StepResult.Terminal(ApprovalRequestHelper.PluginNotFoundMessage);
                return StepResult.Fail($"Could not list pending node pairing requests (exit {pending.ExitCode}): {pendingOutput}");
            }

            var parsed = ApprovalRequestHelper.TryReadSinglePendingRequestId(pending.Stdout.Trim());
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
        var approvalEnv = ApprovalRequestHelper.AddRequestIdEnvironment(env, requestId!);

        var approve = await ctx.Commands.RunInWslAsync(
            distro,
            $"""{ctx.WslPathPrefix} && {ApprovalRequestHelper.ApprovalCommand(approvalKind)}""",
            TimeSpan.FromSeconds(30), approvalEnv, ct);

        ctx.Logger.Info($"Node approve result: exit={approve.ExitCode}");

        return approve.ExitCode == 0
            ? StepResult.Ok($"Node approved: {requestId}")
            : ApprovalRequestHelper.IsPluginNotFoundError(approve.Stdout.Trim())
                ? StepResult.Terminal(ApprovalRequestHelper.PluginNotFoundMessage)
                : StepResult.Fail($"Node approval failed (exit {approve.ExitCode}): {approve.Stdout.Trim()}");
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
