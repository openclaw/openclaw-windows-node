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

public sealed class VerifyEndToEndStep : SetupStep
{
    public override string Id => "verify-e2e";
    public override string DisplayName => "Verify end-to-end connectivity";
    public override RetryPolicy Retry => new(MaxAttempts: 2, InitialDelay: TimeSpan.FromSeconds(3));

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        // Gateway health is proven by the operator connect below; no separate status CLI call.
        var registry = new GatewayRegistry(ctx.DataDir, logger: new SetupOpenClawLogger(ctx.Logger));
        registry.Load();
        var record = registry.GetById(ctx.GatewayRecordId!);
        if (record == null)
            return StepResult.Fail("Gateway record missing from registry");

        var identityDirectory = registry.GetIdentityDirectory(record.Id);
        var tokenRead = DeviceIdentity.ReadStoredDeviceToken(
            identityDirectory,
            new SetupOpenClawLogger(ctx.Logger));
        if (tokenRead.Status is DeviceTokenReadStatus.Unreadable or DeviceTokenReadStatus.Corrupt)
        {
            var identityPath = Path.Combine(identityDirectory, "device-key-ed25519.json");
            Exception cause = tokenRead.Status == DeviceTokenReadStatus.Unreadable
                ? new IOException(tokenRead.Detail ?? "Identity file could not be read.")
                : new InvalidDataException(tokenRead.Detail ?? "Identity file is invalid.");
            return SetupIdentityFailure.Terminal(
                ctx,
                "end-to-end verification",
                new DeviceIdentityLoadException(identityPath, cause));
        }

        SetupOperatorPairingSession? session = null;
        try
        {
            if (tokenRead.Status != DeviceTokenReadStatus.Resolved)
            {
                ctx.Logger.Warn("No stored device token found. Tray app may need to re-pair.");
                (session, var sessionFailure) = await SetupOperatorPairingSession.OpenAsync(ctx, identityDirectory, ct);
                if (sessionFailure is not null)
                    return sessionFailure;
            }
            else
            {
                ctx.Logger.Info("Device token present. Performing final operator handshake.");

                // CRITICAL: The operator finalization must happen AFTER node pairing.
                // Node pairing changes the device's "current metadata" to node-host/node.
                // The tray connects as operator (cli/cli), so we must re-establish operator
                // as the device's last-seen metadata. This prevents "metadata-upgrade" errors.
                // The finalize connection is kept open as the pairing session for the drain below.
                var wsLogger = new SetupOpenClawLogger(ctx.Logger);
                (var finalResult, session) = await FinalizeOperatorForTray(ctx, ctx.GatewayUrl!, identityDirectory, wsLogger, ct);
                if (!finalResult.IsSuccess)
                    return finalResult;
            }

            // Write setup-state.json so tray knows the distro name for WSL keepalive
            await WriteSetupStateAsync(ctx, ct);

            // Write settings.json with EnableNodeMode + capability toggles from config
            WriteSettingsJson(ctx);

            // Drain any remaining pending approvals (device or node) so tray starts clean
            var drainResult = await DrainPendingApprovalsAsync(ctx, session!, ct);
            if (!drainResult.IsSuccess)
                return drainResult;
        }
        finally
        {
            if (session is not null)
                await session.DisposeAsync();
        }

        ClearPersistedBootstrapCredentials(ctx);

        return StepResult.Ok("Gateway running; operator finalized; settings written for tray.");
    }

    internal static Task<StepResult> DrainPendingDeviceApprovalsAsync(
        SetupContext ctx,
        ISetupPairingRequests requests,
        CancellationToken ct)
        => DrainPendingRequestsForSetupDeviceAsync(
            ctx,
            requests,
            ApprovalRequestKind.Device,
            matchNodeId: false,
            ctx.SetupDeviceApprovalBaseline,
            ct);

    private static async Task<StepResult> DrainPendingRequestsForSetupDeviceAsync(
        SetupContext ctx,
        ISetupPairingRequests requests,
        ApprovalRequestKind kind,
        bool matchNodeId,
        PendingRequestBaseline? requestBaseline,
        CancellationToken ct)
    {
        const int maxDrainIterations = 10;
        var label = kind == ApprovalRequestKind.Node ? "Node" : "Device";
        if (requestBaseline is null || !requestBaseline.Success)
        {
            ctx.Logger.Warn(
                $"Skipping pending {label.ToLowerInvariant()} approval drain because setup did not capture a pre-connect request baseline");
            return StepResult.Ok($"Pending {label.ToLowerInvariant()} approval drain skipped");
        }

        for (var i = 0; i < maxDrainIterations; i++)
        {
            var pending = await requests.ListAsync(kind, TimeSpan.FromSeconds(15), ct);
            if (!pending.Success)
                return StepResult.Fail($"Could not list pending {label.ToLowerInvariant()} approvals ({pending.FailureDetail}): {pending.Output}");

            var parsed = ApprovalRequestHelper.TrySelectPendingRequestForDevice(
                pending.Output,
                ctx.OperatorDeviceId,
                requestBaseline.RequestIds,
                matchNodeId);
            if (!parsed.Success)
            {
                if (ApprovalRequestHelper.IsNothingToDrain(parsed) ||
                    ApprovalRequestHelper.IsExplicitNoPendingMessage(pending.Output))
                {
                    break;
                }

                return StepResult.Fail($"Could not select pending {label.ToLowerInvariant()} approval for drain: {parsed.Error}");
            }

            ctx.Logger.Info($"Draining pending {label.ToLowerInvariant()} approval: {parsed.RequestId}");
            var approve = await requests.ApproveAsync(kind, parsed.RequestId!, TimeSpan.FromSeconds(15), ct);
            if (!approve.Success)
                return StepResult.Fail($"{label} approval drain failed for {parsed.RequestId} ({approve.FailureDetail}): {approve.Output}");

            if (i == maxDrainIterations - 1)
                return StepResult.Fail($"{label} approval drain reached its iteration limit; pending approvals may remain");
        }

        return StepResult.Ok(kind == ApprovalRequestKind.Node
            ? "Pending node approvals drained"
            : "Pending device approvals drained");
    }

    internal static async Task<StepResult> DrainPendingApprovalsAsync(
        SetupContext ctx,
        ISetupPairingRequests requests,
        CancellationToken ct)
    {
        var deviceDrainResult = await DrainPendingDeviceApprovalsAsync(ctx, requests, ct);
        if (!deviceDrainResult.IsSuccess)
            return deviceDrainResult;

        var nodeDrainResult = await DrainPendingRequestsForSetupDeviceAsync(
            ctx,
            requests,
            ApprovalRequestKind.Node,
            matchNodeId: true,
            ctx.SetupNodeApprovalBaseline,
            ct);
        if (!nodeDrainResult.IsSuccess)
            return nodeDrainResult;

        return StepResult.Ok("Pending approvals drained");
    }

    internal static void WriteSettingsJson(SetupContext ctx)
    {
        var settingsPath = Path.Combine(ctx.DataDir, "settings.json");
        ctx.Config.Settings.ApplyCapabilities(ctx.Config.Capabilities);
        if (ctx.PersistTraySettings is { } persist) persist(ctx.Config.Settings);
        else ctx.Config.Settings.MergeIntoSettingsFile(settingsPath);
        ctx.Logger.Info($"Wrote settings.json: EnableNodeMode={ctx.Config.Settings.EnableNodeMode}");
    }

    private static void ClearPersistedBootstrapCredentials(SetupContext ctx)
    {
        if (string.IsNullOrWhiteSpace(ctx.GatewayRecordId))
            return;

        var registry = ctx.LoadSetupRegistry();
        var record = registry.GetById(ctx.GatewayRecordId);
        if (record is null)
            return;

        if (string.IsNullOrWhiteSpace(record.BootstrapToken))
        {
            return;
        }

        registry.AddOrUpdate(record with
        {
            BootstrapToken = null
        });
        ctx.SaveSetupRegistry(registry);
        ctx.Logger.Info("Cleared persisted bootstrap gateway credential after device pairing");
    }

    /// <summary>
    /// Final operator connect using device token — establishes operator/cli/cli as the
    /// device's "current metadata" so the tray can connect without metadata-upgrade.
    /// On success the connected client is returned as the setup pairing session; the caller owns it.
    /// </summary>
    private static async Task<(StepResult Result, SetupOperatorPairingSession? Session)> FinalizeOperatorForTray(
        SetupContext ctx, string gatewayUrl, string identityPath, IOpenClawLogger wsLogger, CancellationToken ct)
    {
        var identity = new DeviceIdentity(identityPath);
        try
        {
            identity.Initialize();
        }
        catch (DeviceIdentityLoadException ex)
        {
            return (SetupIdentityFailure.Terminal(ctx, "operator finalization", ex), null);
        }
        var deviceToken = identity.DeviceToken;

        if (string.IsNullOrEmpty(deviceToken))
            return (StepResult.Fail("No device token available for operator finalization"), null);

        ctx.CurrentDeviceApprovalBaseline = await CaptureFinalizationApprovalBaselineAsync(ctx, ct);

        OpenClawGatewayClient CreateClient(string credential)
        {
            var created = new OpenClawGatewayClient(gatewayUrl, credential, logger: wsLogger, identityPath: identityPath);
            PairOperatorStep.ApplyReconnectAuthorization(created, ctx);
            created.UseV2Signature = true;
            return created;
        }

        var client = CreateClient(deviceToken);

        try
        {
            var result = await PairOperatorStep.WaitForConnectionOrPairing(client, ctx, TimeSpan.FromSeconds(15), ct);

            if (result == PairOperatorStep.ConnectionOutcome.Connected)
            {
                ctx.Logger.Info("Final operator handshake succeeded — tray will connect seamlessly");
                var session = SetupOperatorPairingSession.FromConnectedClient(client);
                client = null;
                return (StepResult.Ok("Operator finalized"), session);
            }

            if (result == PairOperatorStep.ConnectionOutcome.PairingRequired)
            {
                ctx.Logger.Info("Metadata-upgrade detected — auto-approving for tray");
                var requestId = client.PairingRequiredRequestId;
                ctx.OperatorDeviceId ??= identity.DeviceId;
                await client.DisconnectAsync();
                client.Dispose();
                client = null;

                var approveResult = await PairOperatorStep.AutoApprovePairing(ctx, new CliPairingRequests(ctx), requestId, ct);
                if (!approveResult.IsSuccess)
                    return (StepResult.Fail($"Operator finalization approval failed: {approveResult.Message}"), null);

                // After approval, the gateway rotates the device token. The old one is invalid.
                // Clear the stale DeviceToken from the identity file so the client doesn't
                // try to use it (OpenClawGatewayClient prefers stored DeviceToken over constructor token).
                ctx.Logger.Info("Clearing stale operator device token from identity file");
                DeviceIdentity.TryClearDeviceToken(identityPath);

                // Reconnect with the SHARED GATEWAY TOKEN to get a fresh device token.
                ctx.Logger.Info("Reconnecting with shared token to get fresh device token after approval");
                client = CreateClient(ctx.SharedGatewayToken!);
                var confirmResult = await PairOperatorStep.WaitForConnectionOrPairing(client, ctx, TimeSpan.FromSeconds(15), ct);
                if (confirmResult == PairOperatorStep.ConnectionOutcome.PairingRequired)
                {
                    ctx.Logger.Info("Operator still pending right after finalization approval; retrying reconnect once");
                    await client.DisconnectAsync();
                    client.Dispose();
                    client = null;
                    await Task.Delay(PairOperatorStep.PostApprovalReconnectRetryDelay, ct);
                    client = CreateClient(ctx.SharedGatewayToken!);
                    confirmResult = await PairOperatorStep.WaitForConnectionOrPairing(client, ctx, TimeSpan.FromSeconds(15), ct);
                }

                if (confirmResult == PairOperatorStep.ConnectionOutcome.Connected)
                {
                    ctx.Logger.Info("Operator finalization approved — fresh device token stored, tray will connect seamlessly");
                    var session = SetupOperatorPairingSession.FromConnectedClient(client);
                    client = null;
                    return (StepResult.Ok("Operator finalized after approval"), session);
                }

                return (PairOperatorStep.ConnectionFailureResult(
                    ctx,
                    "Operator finalization failed after approval",
                    confirmResult), null);
            }

            return (PairOperatorStep.ConnectionFailureResult(ctx, "Operator finalization failed", result), null);
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
    /// Fail closed for the finalization socket specifically (#1523): capture the pending
    /// request list immediately before the finalization socket opens, so the ID-less
    /// fallback in <see cref="PairOperatorStep.AutoApprovePairing"/> can only ever approve
    /// a request this finalization produced. The setup-wide baseline is deliberately NOT
    /// reused here: a request that appeared after initial pairing but before finalization
    /// is absent from it, and the fallback would treat that stale request as new.
    /// </summary>
    internal static Task<PendingRequestBaseline> CaptureFinalizationApprovalBaselineAsync(
        SetupContext ctx,
        CancellationToken ct)
        => ApprovalRequestHelper.CapturePendingRequestBaselineAsync(
            new CliPairingRequests(ctx),
            ApprovalRequestKind.Device,
            ct);

    private static async Task WriteSetupStateAsync(SetupContext ctx, CancellationToken ct)
    {
        var stateDir = ctx.LocalDataDir;
        Directory.CreateDirectory(stateDir);

        var statePath = Path.Combine(stateDir, "setup-state.json");
        // Phase and Status must be integers matching the tray's LocalGatewaySetupPhase/Status enums.
        // Phase.Complete = 13, Status.Complete = 7
        var state = new
        {
            SchemaVersion = 2,
            RunId = Guid.NewGuid().ToString("N"),
            InstallId = GetStableInstallId(ctx),
            Phase = 13,
            Status = 7,
            DistroName = ctx.DistroName,
            GatewayUrl = ctx.GatewayUrl,
            IsLocalOnly = !ctx.Config.Tailscale.Enabled,
            TailscaleEnabled = ctx.Config.Tailscale.Enabled,
            FailureCode = (string?)null,
            UserMessage = (string?)null,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Issues = Array.Empty<object>(),
            History = Array.Empty<object>()
        };

        var json = System.Text.Json.JsonSerializer.Serialize(state, SetupConfig.JsonWriteOptions);
        await AtomicFile.WriteAllTextAsync(statePath, json, ct);
        ctx.Logger.Info($"Wrote setup-state.json: DistroName={ctx.DistroName}");
    }

    private static string GetStableInstallId(SetupContext ctx)
        => !string.IsNullOrWhiteSpace(ctx.GatewayRecordId)
            ? $"gateway:{ctx.GatewayRecordId}"
            : $"distro:{ctx.DistroName}";
}
