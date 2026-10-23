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

public sealed class PairOperatorStep : SetupStep
{
    public override string Id => "pair-operator";
    public override string DisplayName => "Pair operator connection";
    public override RetryPolicy Retry => new(MaxAttempts: 3, InitialDelay: TimeSpan.FromSeconds(3));

    // Approval RPC/CLI returns after the gateway persisted the decision. One short
    // retry covers a reconnect that races the gateway's pairing-store refresh.
    internal static readonly TimeSpan PostApprovalReconnectRetryDelay = TimeSpan.FromSeconds(1);

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var gatewayUrl = ctx.GatewayUrl!;
        var token = SetupPairingCredentialPolicy.ResolveInitialPairingToken(ctx);

        if (string.IsNullOrEmpty(token))
            return StepResult.Terminal("No credential available for operator pairing");

        // Register gateway in registry (only once — reuse across retries)
        var registry = ctx.LoadSetupRegistry();

        string identityPath;
        if (!string.IsNullOrEmpty(ctx.GatewayRecordId))
        {
            var existing = registry.GetById(ctx.GatewayRecordId);
            if (existing == null)
                return StepResult.Fail($"Gateway record {ctx.GatewayRecordId} not found");
            identityPath = registry.GetIdentityDirectory(existing.Id);
            ctx.Logger.Info($"Reusing existing gateway record: id={existing.Id}");
        }
        else
        {
            var record = new GatewayRecord
            {
                Id = Guid.NewGuid().ToString("N")[..16],
                Url = gatewayUrl,
                FriendlyName = ctx.Config.Tailscale.Enabled
                    ? $"Tailscale ({ctx.DistroName})"
                    : $"Local ({ctx.DistroName})",
                SharedGatewayToken = ctx.SharedGatewayToken,
                BootstrapToken = ctx.BootstrapToken,
                IsLocal = true,
                SetupManagedDistroName = ctx.DistroName,
                LastConnected = DateTime.UtcNow
            };

            record = registry.AddOrUpdate(record);
            registry.SetActive(record.Id);
            ctx.SaveSetupRegistry(registry);
            ctx.GatewayRecordId = record.Id;
            identityPath = registry.GetIdentityDirectory(record.Id);
            ctx.Logger.Info($"Gateway record created: id={record.Id}");
        }

        // Initialize device identity
        Directory.CreateDirectory(identityPath);
        var identity = new DeviceIdentity(identityPath);
        try
        {
            identity.Initialize();
        }
        catch (DeviceIdentityLoadException ex)
        {
            return SetupIdentityFailure.Terminal(ctx, "operator pairing", ex);
        }
        ctx.Logger.Info($"Device identity initialized: {identity.DeviceId[..16]}...");
        ctx.OperatorDeviceId = identity.DeviceId;

        var reachability = await WindowsGatewayReachability.VerifyAsync(ctx, "operator", ct);
        if (!reachability.IsSuccess)
            return reachability;
        // The CLI baseline below sends the gateway token outside the handshake-gated
        // WebSocket, so verify the listener owner before it runs.
        var provenanceCheck = await EnsurePairingEndpointTrustedAsync(ctx, ct);
        if (provenanceCheck is not null)
            return provenanceCheck;

        // Connect operator WebSocket — handle pairing-required flow
        var wsLogger = new SetupOpenClawLogger(ctx.Logger);
        OpenClawGatewayClient? client = null;
        var requestBaseline = await ApprovalRequestHelper.CaptureSetupBaselineOnceAsync(
            ctx,
            new CliPairingRequests(ctx),
            ApprovalRequestKind.Device,
            ct);
        ctx.CurrentDeviceApprovalBaseline = requestBaseline;

        OpenClawGatewayClient CreateClient()
        {
            var created = new OpenClawGatewayClient(gatewayUrl, token, logger: wsLogger, identityPath: identityPath);
            ApplyReconnectAuthorization(created, ctx);
            created.UseV2Signature = true; // Local gateway uses v2 signature format
            return created;
        }

        try
        {
            // Phase 1: Initial connect (may get PAIRING_REQUIRED)
            client = CreateClient();
            var phase1Result = await WaitForConnectionOrPairing(client, ctx, TimeSpan.FromSeconds(15), ct);

            if (phase1Result == ConnectionOutcome.Connected)
            {
                ctx.Logger.Info("Operator connected directly (no pairing needed)");
                return StepResult.Ok("Operator connected and paired");
            }

            if (phase1Result == ConnectionOutcome.PairingRequired)
            {
                if (!ctx.Config.AutoApprovePairing)
                    return StepResult.Fail("Pairing required but auto-approve is disabled");

                ctx.Logger.Info("Pairing required — auto-approving via CLI");
                var requestId = client.PairingRequiredRequestId;
                await client.DisconnectAsync();
                client.Dispose();
                client = null;

                // Auto-approve the pending pairing request
                var approveResult = await AutoApprovePairing(ctx, new CliPairingRequests(ctx), requestId, ct);
                if (!approveResult.IsSuccess)
                    return approveResult;

                // Phase 2: Reconnect — the device should now be approved
                client = CreateClient();
                var phase2Result = await WaitForConnectionOrPairing(client, ctx, TimeSpan.FromSeconds(20), ct);
                if (phase2Result == ConnectionOutcome.PairingRequired)
                {
                    ctx.Logger.Info("Operator still pending right after approval; retrying reconnect once");
                    await client.DisconnectAsync();
                    client.Dispose();
                    client = null;
                    await Task.Delay(PostApprovalReconnectRetryDelay, ct);
                    client = CreateClient();
                    phase2Result = await WaitForConnectionOrPairing(client, ctx, TimeSpan.FromSeconds(20), ct);
                }

                if (phase2Result == ConnectionOutcome.Connected)
                {
                    ctx.Logger.Info("Operator paired successfully after approval");
                    // Disconnect before finalization
                    await client.DisconnectAsync();
                    client.Dispose();
                    client = null;

                    // Phase 3: Skip operator finalization here — it must happen AFTER node pairing.
                    // The node pairing changes the device's "current metadata" to node/node-host,
                    // so operator finalization (as cli/cli) must come last to match what the tray sends.
                    ctx.Logger.Info("Operator paired — finalization deferred to after node pairing");
                    return StepResult.Ok("Operator paired (finalization deferred)");
                }

                return ConnectionFailureResult(ctx, "Reconnection after approval failed", phase2Result);
            }

            return ConnectionFailureResult(ctx, "Operator connection failed", phase1Result);
        }
        catch (DeviceIdentityLoadException ex)
        {
            return SetupIdentityFailure.Terminal(ctx, "operator pairing", ex);
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"Operator pairing failed: {ex.Message}", ex);
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

    internal static async Task<StepResult?> EnsurePairingEndpointTrustedAsync(
        SetupContext ctx,
        CancellationToken cancellationToken,
        int noListenerRetryCount = 0,
        TimeSpan? noListenerRetryDelay = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(noListenerRetryCount);
        var retryDelay = noListenerRetryDelay ?? TimeSpan.FromSeconds(1);
        ArgumentOutOfRangeException.ThrowIfLessThan(retryDelay, TimeSpan.Zero);

        var record = new GatewayRecord
        {
            Id = ctx.GatewayRecordId ?? "setup-managed-gateway",
            Url = ctx.GatewayUrl ?? ctx.Config.EffectiveGatewayUrl,
            IsLocal = true,
            SetupManagedDistroName = ctx.DistroName,
        };
        var probe = ctx.EndpointProvenanceProbe ??
            new ManagedLocalGatewayPortProvenanceService(
                new SetupOpenClawLogger(ctx.Logger)).InspectAsync;
        var provenance =
            await GatewayWizardRestartRecoveryPolicy.WaitForExpectedManagedGatewayAsync(
                cancellationToken => probe(record, cancellationToken),
                noListenerRetryCount,
                retryDelay,
                cancellationToken).ConfigureAwait(false);

        return provenance.Kind switch
        {
            GatewayEndpointProvenanceKind.ExpectedManagedGateway or
            GatewayEndpointProvenanceKind.NotApplicable => null,
            GatewayEndpointProvenanceKind.NoListener =>
                StepResult.Fail("The managed WSL gateway is not listening; no pairing credential was sent."),
            _ => StepResult.Terminal(
                provenance.Detail ??
                "The managed gateway address is owned by an unverified process; no pairing credential was sent."),
        };
    }

    internal static void ApplyReconnectAuthorization(
        WebSocketClientBase client,
        SetupContext ctx,
        int provenanceRetryCount = 0,
        TimeSpan? provenanceRetryDelay = null)
    {
        async Task<ReconnectAuthorizationResult> AuthorizeCredentialHandoffAsync(
            CancellationToken cancellationToken)
        {
            var failure = await EnsurePairingEndpointTrustedAsync(
                ctx,
                cancellationToken,
                provenanceRetryCount,
                provenanceRetryDelay).ConfigureAwait(false);
            if (failure is not null)
                ctx.PairingEndpointTrustFailure = failure;
            return failure is null
                ? ReconnectAuthorizationResult.AllowedResult
                : new ReconnectAuthorizationResult(
                    false,
                    GatewayErrorKind.LocalPortConflict,
                    failure.Message);
        }

        client.ReconnectAuthorizationAsync = AuthorizeCredentialHandoffAsync;
        switch (client)
        {
            case OpenClawGatewayClient gatewayClient:
                gatewayClient.HandshakeAuthorizationAsync =
                    AuthorizeCredentialHandoffAsync;
                break;
            case WindowsNodeClient nodeClient:
                nodeClient.HandshakeAuthorizationAsync =
                    AuthorizeCredentialHandoffAsync;
                break;
        }
    }

    internal static async Task<StepResult> AutoApprovePairing(
        SetupContext ctx,
        ISetupPairingRequests requests,
        string? requestId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            var requestBaseline = ctx.CurrentDeviceApprovalBaseline;
            if (requestBaseline is null || !requestBaseline.Success)
            {
                if (requestBaseline?.PluginNotFound == true)
                    return StepResult.Terminal(ApprovalRequestHelper.PluginNotFoundMessage);

                return StepResult.Fail(
                    requestBaseline?.Error ??
                    "The setup socket did not provide a pairing request ID, and no pre-connect approval baseline is available.");
            }

            var pending = await requests.ListAsync(ApprovalRequestKind.Device, TimeSpan.FromSeconds(30), ct);

            ctx.Logger.Info($"Device pending list: {(pending.Success ? "ok" : pending.FailureDetail)}");

            if (!pending.Success)
            {
                if (ApprovalRequestHelper.IsPluginNotFoundError(pending.Output))
                    return StepResult.Terminal(ApprovalRequestHelper.PluginNotFoundMessage);
                return StepResult.Fail($"Could not list pending pairing requests ({pending.FailureDetail}): {pending.Output}");
            }

            var parsed = ApprovalRequestHelper.TrySelectPendingRequestForDevice(
                pending.Output,
                ctx.OperatorDeviceId,
                requestBaseline.RequestIds,
                matchNodeId: false);
            if (!parsed.Success)
            {
                ctx.Logger.Warn($"Could not select pairing request: {parsed.Error}");
                return StepResult.Fail(parsed.Error ?? "Could not find a safe pending pairing request to approve");
            }

            requestId = parsed.RequestId;
        }

        if (!ApprovalRequestHelper.IsSafeRequestId(requestId))
        {
            ctx.Logger.Warn("Refusing to approve pairing request with unsafe request ID");
            return StepResult.Fail("Pairing request ID contained unsafe characters");
        }

        ctx.Logger.Info($"Approving pairing request: {requestId}");

        var approve = await requests.ApproveAsync(ApprovalRequestKind.Device, requestId!, TimeSpan.FromSeconds(30), ct);

        ctx.Logger.Info($"Approve result: {(approve.Success ? "ok" : approve.FailureDetail)}");

        if (!approve.Success)
        {
            if (ApprovalRequestHelper.IsPluginNotFoundError(approve.Output))
                return StepResult.Terminal(ApprovalRequestHelper.PluginNotFoundMessage);
            return StepResult.Fail($"Device approval failed ({approve.FailureDetail}): {approve.Output}");
        }

        return StepResult.Ok($"Approved request {requestId}");
    }

    internal enum ConnectionOutcome { Connected, PairingRequired, CompatibilityFailure, Error, Timeout }

    internal static StepResult ConnectionFailureResult(
        SetupContext ctx,
        string prefix,
        ConnectionOutcome outcome)
    {
        if (outcome == ConnectionOutcome.Error && ctx.PairingEndpointTrustFailure is { } trustFailure)
            return trustFailure;

        if (outcome == ConnectionOutcome.CompatibilityFailure &&
            ctx.GatewayCompatibilityFailure is { } compatibilityFailure)
        {
            return StepResult.Terminal(compatibilityFailure.Message, compatibilityFailure);
        }

        return StepResult.Fail($"{prefix}: {outcome}");
    }

    internal static ConnectionOutcome? ClassifySetupConnectionStatus(
        ConnectionStatus status,
        bool isPairingRequired,
        int? lastRemoteCloseStatusCode,
        bool retryGatewayStartupDisconnects) =>
        status switch
        {
            ConnectionStatus.Connected => ConnectionOutcome.Connected,
            ConnectionStatus.Error => ConnectionOutcome.Error,
            ConnectionStatus.Disconnected when isPairingRequired =>
                ConnectionOutcome.PairingRequired,
            ConnectionStatus.Disconnected when
                retryGatewayStartupDisconnects &&
                GatewayWizardRestartRecoveryPolicy.IsRetryableGatewayStartupDisconnect(
                    lastRemoteCloseStatusCode) => null,
            ConnectionStatus.Disconnected => ConnectionOutcome.Error,
            _ => null,
        };

    internal static async Task<ConnectionOutcome> WaitForConnectionOrPairing(
        OpenClawGatewayClient client,
        SetupContext ctx,
        TimeSpan timeout,
        CancellationToken ct,
        bool retryGatewayStartupDisconnects = false,
        bool allowInstalledVersionDiscovery = false)
    {
        var tcs = new TaskCompletionSource<ConnectionOutcome>();
        ctx.ObservedGatewaySelf = null;
        ctx.PairingEndpointTrustFailure = null;
        ctx.GatewayCompatibilityFailure = null;

        void OnStatusChanged(object? sender, ConnectionStatus status)
        {
            ctx.Logger.Debug($"Operator connection status: {status}");
            if (status == ConnectionStatus.Connected)
            {
                var compatibilityFailure = GatewayInstallPolicy.ValidateHandshake(
                    ctx.Config,
                    ctx.ObservedGatewaySelf,
                    allowInstalledVersionDiscovery);
                if (compatibilityFailure is null)
                {
                    tcs.TrySetResult(ConnectionOutcome.Connected);
                }
                else
                {
                    ctx.GatewayCompatibilityFailure = compatibilityFailure;
                    tcs.TrySetResult(ConnectionOutcome.CompatibilityFailure);
                }
                return;
            }

            var outcome = ClassifySetupConnectionStatus(
                status,
                client.IsPairingRequired,
                client.LastRemoteCloseStatusCode,
                retryGatewayStartupDisconnects);
            if (outcome is not null)
            {
                tcs.TrySetResult(outcome.Value);
            }
            else if (status == ConnectionStatus.Disconnected)
            {
                ctx.Logger.Debug(
                    "Gateway is still starting after restart; waiting for the authenticated reconnect.");
            }
        }

        client.StatusChanged += OnStatusChanged;
        EventHandler<DeviceTokenReceivedEventArgs> onDeviceToken = (_, _) => ctx.Logger.Info("Device token received from gateway");
        client.DeviceTokenReceived += onDeviceToken;
        EventHandler<GatewaySelfInfo> onGatewaySelf = (_, gatewaySelf) => ctx.ObservedGatewaySelf = gatewaySelf;
        client.GatewaySelfUpdated += onGatewaySelf;

        try
        {
            await client.ConnectAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ConnectionOutcome.Timeout;
        }
        catch (Exception ex)
        {
            ctx.Logger.Warn($"Operator connection failed: {ex.Message}");
            return ConnectionOutcome.Error;
        }
        finally
        {
            client.StatusChanged -= OnStatusChanged;
            client.DeviceTokenReceived -= onDeviceToken;
            client.GatewaySelfUpdated -= onGatewaySelf;
        }
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        var registry = ctx.LoadSetupRegistry();

        // Find all local gateway records to remove (mirrors old uninstall step 6a)
        var localRecords = registry.GetAll()
            .Where(r => IsSetupManagedLocalRecord(r, ctx))
            .ToList();

        if (localRecords.Count > 0)
        {
            foreach (var record in localRecords)
            {
                // Remove identity directory
                var identityDir = registry.GetIdentityDirectory(record.Id);
                if (Directory.Exists(identityDir))
                {
                    Directory.Delete(identityDir, recursive: true);
                    ctx.Logger.Info($"[Uninstall] Deleted identity directory: {identityDir}");
                }
                registry.Remove(record.Id);
            }
            ctx.SaveSetupRegistry(registry);
            ctx.Logger.Info($"[Uninstall] Removed {localRecords.Count} local gateway record(s)");
        }
        else
        {
            ctx.Logger.Info("[Uninstall] No local gateway records found");
        }

        // Null operator device token (mirrors old uninstall step 7)
        // Check if external gateways remain — if so, preserve root device tokens
        var hasExternalGateways = registry.GetAll().Any(r =>
            !r.IsLocal && !(r.SshTunnel is null && LocalGatewayUrlClassifier.IsLocalGatewayUrl(r.Url)));

        if (hasExternalGateways)
        {
            ctx.Logger.Info("[Uninstall] Preserving root device tokens — external gateway records remain");
        }
        else
        {
            var operatorCleared = DeviceIdentity.TryClearDeviceTokenForRole(ctx.DataDir, "operator");
            ctx.Logger.Info(operatorCleared
                ? "[Uninstall] Cleared operator device token"
                : "[Uninstall] Operator device token already absent");
        }

        // Best-effort revoke operator token via gateway HTTP endpoint (mirrors old step 4)
        await TryRevokeOperatorTokenAsync(ctx, ct);
    }

    internal static bool IsSetupManagedLocalRecord(GatewayRecord record, SetupContext ctx)
    {
        if (!record.IsLocal || record.SshTunnel != null)
            return false;

        if (string.Equals(record.SetupManagedDistroName, ctx.DistroName, StringComparison.Ordinal))
            return true;

        return string.IsNullOrWhiteSpace(record.SetupManagedDistroName)
            && string.Equals(record.Url, ctx.GatewayUrl, StringComparison.OrdinalIgnoreCase)
            && string.Equals(record.FriendlyName, $"Local ({ctx.DistroName})", StringComparison.Ordinal);
    }

    private static async Task TryRevokeOperatorTokenAsync(SetupContext ctx, CancellationToken ct)
    {
        try
        {
            // Read settings.json for legacy token if available
            var settingsPath = Path.Combine(ctx.DataDir, "settings.json");
            if (!File.Exists(settingsPath)) return;

            var settingsJson = await File.ReadAllTextAsync(settingsPath, ct);
            using var doc = JsonDocument.Parse(settingsJson);

            string? token = null;
            if (doc.RootElement.TryGetProperty("Token", out var tokenProp))
                token = tokenProp.GetString();

            if (string.IsNullOrWhiteSpace(token)) return;

            var gatewayUrl = ctx.GatewayUrl ?? "ws://127.0.0.1:18789";
            var httpBase = gatewayUrl
                .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)
                .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
                .TrimEnd('/');

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var response = await http.PostAsync($"{httpBase}/api/v1/operator/disconnect", content: null, cts.Token);
            ctx.Logger.Info($"[Uninstall] Revoke operator token: HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            ctx.Logger.Info($"[Uninstall] Best-effort token revoke failed ({ex.GetType().Name}); gateway may be down");
        }
    }
}
