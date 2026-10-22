using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using OpenClaw.Shared;

namespace OpenClaw.Connection;

internal delegate Task<ReconnectAuthorizationResult> ValidationHandshakeAuthorization(
    GatewayRecord record, GatewayCredential credential, ISshTunnelManager? tunnel,
    SshTunnelConfig? config, long? generation, CancellationToken cancellationToken);

/// <summary>One-shot operator validation, isolated from the active manager, saved identity and SSH listener.</summary>
internal sealed class GatewayConnectionValidator(
    ICredentialResolver resolver,
    Func<ISshTunnelManager> tunnelFactory,
    ValidationHandshakeAuthorization authorize,
    IOpenClawLogger logger,
    Func<OpenClawGatewayClient, CancellationToken, Task<SetupCodeResult>>? validateHandshake = null)
{
    public async Task<SetupCodeResult> ValidateAsync(
        GatewayRecord candidate, GatewayValidationIdentity identity,
        IReadOnlySet<int> excludedPorts, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(35));
        var ct = deadline.Token;
        ISshTunnelManager? tunnel = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            GatewayCredential? ResolveCredential() => identity.OperatorCredential is { } ephemeral
                ? new GatewayCredential(ephemeral.Token, false, CredentialResolver.SourceDeviceToken)
                : resolver.ResolveOperator(candidate, identity.DirectoryPath);
            if (ResolveCredential() is null)
                return new(SetupCodeOutcome.ConnectionFailed, "Enter a setup code or shared token for this gateway.");
            SshTunnelConfig? config = null;
            long? generation = null;
            var url = candidate.Url;
            if (candidate.SshTunnel is { } ssh)
            {
                config = ssh with { LocalPort = AllocatePort(excludedPorts), IncludeBrowserProxyForward = false };
                tunnel = tunnelFactory();
                var started = await tunnel.StartOwnedAsync(config, ct).ConfigureAwait(false);
                generation = started.OwnershipGeneration;
                // Do not trust an arbitrary URL or a different config returned by a tunnel implementation.
                if (started.Config != config || !GatewayRecordEditing.AreEquivalentLoopbackEndpoints(
                        started.Url, $"ws://127.0.0.1:{config.LocalPort}"))
                    return new(SetupCodeOutcome.ConnectionFailed, "The isolated SSH listener did not match the requested endpoint.");
                url = started.Url;
            }
            // Signature, scope and revoked-token recovery may each advance once, never reconnect without a bound.
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var credential = ResolveCredential();
                if (credential is null)
                    return new(SetupCodeOutcome.ConnectionFailed, "Enter a setup code or shared token for this gateway.");
                var record = candidate with { Url = url, RequiresV2Signature = candidate.RequiresV2Signature || identity.UseV2Signature };
                var permission = await authorize(record, credential, tunnel, config, generation, ct).ConfigureAwait(false);
                if (!permission.Allowed)
                    return new(SetupCodeOutcome.ConnectionFailed, permission.Detail);

                var boundedScopes = identity.UseBoundedBootstrapScopes;
                using var client = CreateClient(
                    url, credential, identity.DirectoryPath, record, logger,
                    token => authorize(record, credential, tunnel, config, generation, token),
                    identity.OperatorCredential, boundedScopes);
                var v2Signature = client.UseV2Signature;
                var receivedTokens = new ConcurrentQueue<DeviceTokenReceivedEventArgs>();
                client.DeviceTokenReceived += (_, token) => receivedTokens.Enqueue(token);
                void CaptureAuthenticatedTokens()
                {
                    foreach (var token in receivedTokens)
                        identity.CaptureToken(token);
                }
                // Capture before graceful disconnect: teardown failure must not lose a consumed
                // bootstrap token's authenticated replacement.
                client.HandshakeSucceeded += (_, _) => CaptureAuthenticatedTokens();
                SetupCodeResult result;
                try
                {
                    result = await (validateHandshake ?? RunHandshakeAsync)(client, ct).ConfigureAwait(false);
                }
                finally
                {
                    identity.UseBoundedBootstrapScopes |= client.UsesBoundedBootstrapScopes;
                    identity.UseV2Signature |= client.UseV2Signature;
                }
                if (result.Outcome == SetupCodeOutcome.Success)
                {
                    ct.ThrowIfCancellationRequested();
                    CaptureAuthenticatedTokens();
                    return result;
                }
                if (result.ErrorKind == GatewayErrorKind.DeviceTokenMismatch &&
                    credential.Source == CredentialResolver.SourceDeviceToken &&
                    identity.OperatorCredential is null && !identity.OperatorTokenRecoveryAttempted &&
                    GatewayCredentialRecoveryPolicy.IsTransportSafe(candidate))
                {
                    var fallback = !string.IsNullOrWhiteSpace(candidate.SharedGatewayToken)
                        ? new GatewayCredential(candidate.SharedGatewayToken, false, CredentialResolver.SourceSharedGatewayToken)
                        : !string.IsNullOrWhiteSpace(candidate.BootstrapToken)
                            ? new GatewayCredential(candidate.BootstrapToken, true, CredentialResolver.SourceBootstrapToken)
                            : null;
                    if (fallback is not null)
                    {
                        var recovery = await authorize(record, fallback, tunnel, config, generation, ct).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        if (!recovery.Allowed)
                            return result with { ErrorMessage = recovery.Detail };
                        if (identity.RejectStoredOperatorToken(credential.Token))
                            continue;
                    }
                }
                if ((boundedScopes || !client.UsesBoundedBootstrapScopes) &&
                    (v2Signature || !client.UseV2Signature))
                    return result;
            }
            return new(SetupCodeOutcome.ConnectionFailed, "Gateway authentication compatibility checks were exhausted.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Warn($"Native gateway validation failed: {ex.GetType().Name}");
            return new(SetupCodeOutcome.ConnectionFailed,
                ex is OperationCanceledException ? "Gateway connection check timed out." : "Gateway connection check failed. Review the address, credentials and SSH settings.");
        }
        finally
        {
            if (tunnel is not null)
            {
                try { await tunnel.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                finally { tunnel.Dispose(); }
            }
        }
    }

    internal static OpenClawGatewayClient CreateClient(
        string url, GatewayCredential credential, string identityPath, GatewayRecord record,
        IOpenClawLogger logger, Func<CancellationToken, Task<ReconnectAuthorizationResult>> authorize,
        DeviceTokenReceivedEventArgs? ephemeralOperatorCredential = null,
        bool useBoundedBootstrapScopes = false)
    {
        var client = new OpenClawGatewayClient(
            url, credential.Token, logger,
            tokenIsBootstrapToken: credential.IsBootstrapToken,
            bootstrapPairAsNode: false,
            identityPath: identityPath,
            ignoreStoredDeviceToken: credential.Source != CredentialResolver.SourceDeviceToken,
            persistHandshakeDeviceTokens: false,
            ephemeralOperatorCredential: ephemeralOperatorCredential,
            useBoundedBootstrapScopes: useBoundedBootstrapScopes)
        {
            UseV2Signature = record.IsLocal || record.RequiresV2Signature || credential.IsBootstrapToken
        };
        client.ReconnectAuthorizationAsync = _ => Task.FromResult(new ReconnectAuthorizationResult(
            false, GatewayErrorKind.Auth, "Gateway validation is one-shot."));
        client.HandshakeAuthorizationAsync = authorize;
        return client;
    }

    internal static async Task<SetupCodeResult> RunHandshakeAsync(
        OpenClawGatewayClient client, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<SetupCodeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handshake(object? sender, EventArgs e) =>
            completion.TrySetResult(new(SetupCodeOutcome.Success));
        void AuthenticationFailed(object? sender, string message) =>
            completion.TrySetResult(AuthenticationFailure(message));
        void StatusChanged(object? sender, ConnectionStatus status)
        {
            if (status is ConnectionStatus.Error or ConnectionStatus.Disconnected)
                completion.TrySetResult(new(SetupCodeOutcome.ConnectionFailed, "Gateway connection check failed."));
        }

        client.HandshakeSucceeded += Handshake;
        client.AuthenticationFailed += AuthenticationFailed;
        client.StatusChanged += StatusChanged;
        using var cancellation = cancellationToken.Register(client.Dispose);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            client.HandshakeSucceeded -= Handshake;
            client.AuthenticationFailed -= AuthenticationFailed;
            client.StatusChanged -= StatusChanged;
            if (!cancellationToken.IsCancellationRequested)
                await client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    internal static SetupCodeResult AuthenticationFailure(string message)
    {
        var kind = GatewayErrorClassifier.ClassifyWithCode(message);
        return new(SetupCodeOutcome.ConnectionFailed,
            kind == GatewayErrorKind.DeviceTokenMismatch
                ? "The saved device token was rejected. Supply a current shared token or setup code for this Gateway and check again."
                : "Gateway authentication failed or device approval is required.",
            ErrorKind: kind);
    }

    private static int AllocatePort(IReadOnlySet<int> excludedPorts)
    {
        for (var i = 0; i < 16; i++)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (!excludedPorts.Contains(port))
                return port;
        }
        throw new InvalidOperationException("No isolated SSH validation port is available.");
    }
}
