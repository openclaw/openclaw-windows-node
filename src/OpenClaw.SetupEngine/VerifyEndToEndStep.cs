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
        // Verify gateway is still healthy
        var distro = ctx.DistroName!;
        var status = await ctx.Commands.RunInWslAsync(
            distro, $"{ctx.WslPathPrefix} && openclaw gateway status --json", TimeSpan.FromSeconds(15), ct: ct);

        if (status.ExitCode != 0 || !status.Stdout.Contains("running", StringComparison.OrdinalIgnoreCase))
            return StepResult.Fail("Gateway is not running");

        // Verify registry state
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

        if (tokenRead.Status != DeviceTokenReadStatus.Resolved)
        {
            ctx.Logger.Warn("No stored device token found. Tray app may need to re-pair.");
        }
        else
        {
            ctx.Logger.Info("Device token present. Performing final operator handshake.");

            // CRITICAL: The operator finalization must happen AFTER node pairing.
            // Node pairing changes the device's "current metadata" to node-host/node.
            // The tray connects as operator (cli/cli), so we must re-establish operator
            // as the device's last-seen metadata. This prevents "metadata-upgrade" errors.
            var wsLogger = new SetupOpenClawLogger(ctx.Logger);
            var finalResult = await FinalizeOperatorForTray(ctx, ctx.GatewayUrl!, identityDirectory, wsLogger, ct);
            if (!finalResult.IsSuccess)
                return finalResult;
        }

        // Write setup-state.json so tray knows the distro name for WSL keepalive
        await WriteSetupStateAsync(ctx, ct);

        // Write settings.json with EnableNodeMode + capability toggles from config
        WriteSettingsJson(ctx);

        // Drain any remaining pending approvals (device or node) so tray starts clean
        var drainResult = await DrainPendingApprovalsAsync(ctx, ct);
        if (!drainResult.IsSuccess)
            return drainResult;

        ClearPersistedBootstrapCredentials(ctx);

        return StepResult.Ok("Gateway running; operator finalized; settings written for tray.");
    }

    internal static async Task<StepResult> DrainPendingDeviceApprovalsAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        var token = ctx.SharedGatewayToken ?? ctx.BootstrapToken;
        if (string.IsNullOrWhiteSpace(token))
            return StepResult.Fail("No gateway token available to drain pending device approvals");

        var pathPrefix = ctx.WslPathPrefix;
        var env = new Dictionary<string, string> { ["OPENCLAW_GATEWAY_TOKEN"] = token };
        const int maxDrainIterations = 10;

        for (var i = 0; i < maxDrainIterations; i++)
        {
            var preview = await ctx.Commands.RunInWslAsync(
                distro,
                $"""{pathPrefix} && openclaw devices approve --latest --json""",
                TimeSpan.FromSeconds(15), env, ct);

            if (preview.Stdout.Contains("No pending", StringComparison.OrdinalIgnoreCase) ||
                preview.Stderr.Contains("No pending", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            var parsed = ApprovalRequestHelper.TryReadSelectedRequestId(preview.Stdout.Trim());
            if (parsed.Success)
            {
                ctx.Logger.Info($"Draining pending device approval: {parsed.RequestId}");
                var approvalEnv = ApprovalRequestHelper.AddRequestIdEnvironment(env, parsed.RequestId!);
                var approve = await ctx.Commands.RunInWslAsync(
                    distro,
                    $"""{pathPrefix} && {ApprovalRequestHelper.ApprovalCommand(ApprovalRequestKind.Device)}""",
                    TimeSpan.FromSeconds(15), approvalEnv, ct);

                if (approve.ExitCode != 0)
                    return StepResult.Fail($"Device approval drain failed for {parsed.RequestId} (exit {approve.ExitCode}): {approve.Stdout.Trim()} {approve.Stderr.Trim()}".Trim());

                if (i == maxDrainIterations - 1)
                    return StepResult.Fail("Device approval drain reached its iteration limit; pending approvals may remain");

                continue;
            }

            if (preview.ExitCode == 0)
            {
                var approved = ApprovalRequestHelper.TryReadApprovedRequestId(preview.Stdout.Trim());
                if (approved.Success)
                {
                    ctx.Logger.Info($"Drained pending device approval via latest command: {approved.RequestId}");
                    if (i == maxDrainIterations - 1)
                        return StepResult.Fail("Device approval drain reached its iteration limit; pending approvals may remain");

                    continue;
                }
            }

            return StepResult.Fail($"Could not select pending device approval for drain (exit {preview.ExitCode}): {parsed.Error ?? preview.Stderr.Trim()}");
        }

        return StepResult.Ok("Pending device approvals drained");
    }

    private static async Task<StepResult> DrainPendingApprovalsAsync(SetupContext ctx, CancellationToken ct)
    {
        var deviceDrainResult = await DrainPendingDeviceApprovalsAsync(ctx, ct);
        if (!deviceDrainResult.IsSuccess)
            return deviceDrainResult;

        var distro = ctx.DistroName!;
        var token = ctx.SharedGatewayToken ?? ctx.BootstrapToken;
        if (string.IsNullOrWhiteSpace(token))
            return StepResult.Fail("No gateway token available to drain pending approvals");

        var pathPrefix = ctx.WslPathPrefix;
        var env = new Dictionary<string, string> { ["OPENCLAW_GATEWAY_TOKEN"] = token };
        const int maxDrainIterations = 10;

        for (var i = 0; i < maxDrainIterations; i++)
        {
            var nodeList = await ctx.Commands.RunInWslAsync(
                distro,
                $"""{pathPrefix} && openclaw nodes list --json""",
                TimeSpan.FromSeconds(15), env, ct);

            var parsed = ApprovalRequestHelper.TryReadPendingRequestIds(nodeList.Stdout.Trim());
            if (!parsed.Success)
            {
                if (nodeList.ExitCode != 0)
                    return StepResult.Fail($"Could not list pending node approvals (exit {nodeList.ExitCode}): {nodeList.Stdout.Trim()} {nodeList.Stderr.Trim()}".Trim());

                return StepResult.Fail($"Could not parse pending node approvals: {parsed.Error}");
            }

            if (parsed.RequestIds.Count == 0)
                break;

            foreach (var requestId in parsed.RequestIds)
            {
                ctx.Logger.Info($"Draining pending node approval: {requestId}");
                var approvalEnv = ApprovalRequestHelper.AddRequestIdEnvironment(env, requestId);
                var approve = await ctx.Commands.RunInWslAsync(
                    distro,
                    $"""{pathPrefix} && {ApprovalRequestHelper.ApprovalCommand(ApprovalRequestKind.Node)}""",
                    TimeSpan.FromSeconds(15), approvalEnv, ct);

                if (approve.ExitCode != 0)
                    return StepResult.Fail($"Node approval drain failed for {requestId} (exit {approve.ExitCode}): {approve.Stdout.Trim()} {approve.Stderr.Trim()}".Trim());
            }

            if (i == maxDrainIterations - 1)
                return StepResult.Fail("Node approval drain reached its iteration limit; pending approvals may remain");
        }

        return StepResult.Ok("Pending approvals drained");
    }

    internal static void WriteSettingsJson(SetupContext ctx)
    {
        var settingsPath = Path.Combine(ctx.DataDir, "settings.json");
        ctx.Config.Settings.ApplyCapabilities(ctx.Config.Capabilities);
        ctx.Config.Settings.MergeIntoSettingsFile(settingsPath);
        ctx.Logger.Info($"Wrote settings.json: EnableNodeMode={ctx.Config.Settings.EnableNodeMode}");
    }

    private static void ClearPersistedBootstrapCredentials(SetupContext ctx)
    {
        if (string.IsNullOrWhiteSpace(ctx.GatewayRecordId))
            return;

        var registry = new GatewayRegistry(ctx.DataDir, logger: new SetupOpenClawLogger(ctx.Logger));
        registry.Load();
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
        registry.Save();
        ctx.Logger.Info("Cleared persisted bootstrap gateway credential after device pairing");
    }

    /// <summary>
    /// Final operator connect using device token — establishes operator/cli/cli as the
    /// device's "current metadata" so the tray can connect without metadata-upgrade.
    /// </summary>
    private static async Task<StepResult> FinalizeOperatorForTray(
        SetupContext ctx, string gatewayUrl, string identityPath, IOpenClawLogger wsLogger, CancellationToken ct)
    {
        var identity = new DeviceIdentity(identityPath);
        try
        {
            identity.Initialize();
        }
        catch (DeviceIdentityLoadException ex)
        {
            return SetupIdentityFailure.Terminal(ctx, "operator finalization", ex);
        }
        var deviceToken = identity.DeviceToken;

        if (string.IsNullOrEmpty(deviceToken))
            return StepResult.Fail("No device token available for operator finalization");

        // Wait for grace period to expire so this connect is treated as a real metadata change
        ctx.Logger.Info("Waiting for grace period before final operator handshake...");
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        var client = new OpenClawGatewayClient(gatewayUrl, deviceToken, logger: wsLogger, identityPath: identityPath);
        PairOperatorStep.ApplyReconnectAuthorization(client, ctx);
        client.UseV2Signature = true;

        try
        {
            var result = await PairOperatorStep.WaitForConnectionOrPairing(client, ctx, TimeSpan.FromSeconds(15), ct);

            if (result == PairOperatorStep.ConnectionOutcome.Connected)
            {
                ctx.Logger.Info("Final operator handshake succeeded — tray will connect seamlessly");
                return StepResult.Ok("Operator finalized");
            }

            if (result == PairOperatorStep.ConnectionOutcome.PairingRequired)
            {
                ctx.Logger.Info("Metadata-upgrade detected — auto-approving for tray");
                await client.DisconnectAsync();
                client.Dispose();
                client = null;

                var approveResult = await PairOperatorStep.AutoApprovePairing(ctx, ct);
                if (!approveResult.IsSuccess)
                    return StepResult.Fail($"Operator finalization approval failed: {approveResult.Message}");

                await Task.Delay(2000, ct);

                // After approval, the gateway rotates the device token. The old one is invalid.
                // Clear the stale DeviceToken from the identity file so the client doesn't
                // try to use it (OpenClawGatewayClient prefers stored DeviceToken over constructor token).
                ctx.Logger.Info("Clearing stale operator device token from identity file");
                DeviceIdentity.TryClearDeviceToken(identityPath);

                // Reconnect with the SHARED GATEWAY TOKEN to get a fresh device token.
                ctx.Logger.Info("Reconnecting with shared token to get fresh device token after approval");
                var provenanceCheck = await PairOperatorStep.EnsurePairingEndpointTrustedAsync(ctx, ct);
                if (provenanceCheck is not null)
                    return provenanceCheck;
                client = new OpenClawGatewayClient(gatewayUrl, ctx.SharedGatewayToken!, logger: wsLogger, identityPath: identityPath);
                PairOperatorStep.ApplyReconnectAuthorization(client, ctx);
                client.UseV2Signature = true;
                var confirmResult = await PairOperatorStep.WaitForConnectionOrPairing(client, ctx, TimeSpan.FromSeconds(15), ct);

                if (confirmResult == PairOperatorStep.ConnectionOutcome.Connected)
                {
                    ctx.Logger.Info("Operator finalization approved — fresh device token stored, tray will connect seamlessly");
                    return StepResult.Ok("Operator finalized after approval");
                }

                return PairOperatorStep.ConnectionFailureResult(
                    ctx,
                    "Operator finalization failed after approval",
                    confirmResult);
            }

            return PairOperatorStep.ConnectionFailureResult(ctx, "Operator finalization failed", result);
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
