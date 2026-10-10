using System.Diagnostics;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

public enum NativeSetupRecoveryAction { OpenTerminal, RestartGateway, RestartAi, CancelSetup }

/// <summary>Owns only an operator connection. The staged native session retains its profile and runtime.</summary>
public sealed class NativeGatewaySetupConnection : IGatewayAiSetupTransport, IAsyncDisposable
{
    private readonly NativeGatewaySetupSession _owner;
    private readonly SetupGatewaySessionBinding _binding;
    private readonly Func<CancellationToken, Task> _authorize;
    private readonly GatewayAiSetupRoute _route;
    public OpenClawGatewayClient Client { get; }
    public long Generation => Client.ServerHandshakeGeneration;
    public bool IsConnected => Client.IsConnectedToGateway && Client.HasHandshakeSnapshot;
    public IReadOnlyCollection<string> Methods => Client.AdvertisedServerMethods;
    public IReadOnlyCollection<string> OperatorScopes => Client.GrantedOperatorScopes;

    public void RequireRestartAuthority(GatewayAiSetupRoute expected)
    {
        _owner.RequireCurrentProfile();
        _binding.RequirePersistedAuthority(_owner.Record, _owner.IdentityDirectory, expected);
    }

    private NativeGatewaySetupConnection(NativeGatewaySetupSession owner, OpenClawGatewayClient client,
        Func<CancellationToken, Task> authorize)
    {
        _owner = owner;
        Client = client;
        _authorize = authorize;
        _binding = new(owner.Record);
        _route = CaptureRoute();
    }

    private GatewayAiSetupRoute CaptureRoute()
    {
        _owner.RequireCurrentProfile();
        var route = _binding.GetRoute(_owner.Record, _owner.IdentityDirectory,
            Client.MainSessionKey, Client.AuthenticatedSigningDeviceId);
        SetupCompletionAuthority.RequirePersistedIdentity(_owner.IdentityDirectory, route.IdentityBinding);
        _owner.AdmitIdentity(route.IdentityBinding);
        return route;
    }

    public GatewayAiSetupRoute Route
    {
        get
        {
            var current = CaptureRoute();
            if (current != _route) throw new SetupNativeOwnershipException();
            return current;
        }
    }

    public static async Task<NativeGatewaySetupConnection> ConnectAsync(
        NativeGatewaySetupSession owner, CancellationToken ct = default)
    {
        await owner.PrepareConnectionAsync(ct);
        return await ConnectCoreAsync(owner, owner.AuthorizeAsync, allowPairing: true, ct);
    }

    internal static Task<NativeGatewaySetupConnection> ConnectForFinalizationAsync(
        NativeGatewaySetupSession owner, Func<CancellationToken, Task> authorize, CancellationToken ct) =>
        ConnectCoreAsync(owner, authorize, allowPairing: false, ct);

    private static async Task<NativeGatewaySetupConnection> ConnectCoreAsync(
        NativeGatewaySetupSession owner, Func<CancellationToken, Task> authorize, bool allowPairing, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, owner.LifetimeToken);
        var pairingAttempted = false;
        var recoveryAttempted = false;
        // Worst case: rejected device token -> retire it -> shared credential -> pairing approval -> connected.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            linked.Token.ThrowIfCancellationRequested();
            owner.RequireCurrentProfile();
            var storedDeviceToken = DeviceIdentity.TryReadStoredDeviceToken(owner.IdentityDirectory);
            var token = storedDeviceToken
                ?? owner.Record.SharedGatewayToken
                ?? throw new InvalidOperationException("No native Gateway credential found.");
            var client = new OpenClawGatewayClient(owner.Record.Url, token,
                logger: NullLogger.Instance, identityPath: owner.IdentityDirectory,
                requireExistingIdentity: !allowPairing) { UseV2Signature = true };
            client.HandshakeAuthorizationAsync = client.ReconnectAuthorizationAsync = async cancellation =>
            {
                await authorize(cancellation);
                return ReconnectAuthorizationResult.AllowedResult;
            };
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // The gateway reports a typed failure kind before the terminal error status,
            // so recovery keys off the authoritative code rather than error text.
            GatewayErrorKind? observedFailure = null;
            void ConnectionFailure(object? sender, GatewayErrorKind kind) => observedFailure ??= kind;
            void StatusChanged(object? sender, ConnectionStatus status)
            {
                if (status == ConnectionStatus.Connected) connected.TrySetResult();
                else if (status == ConnectionStatus.Error)
                    connected.TrySetException(new InvalidOperationException("Could not connect to the verified native Gateway."));
            }
            void PairingRequired(object? sender, string? requestId) =>
                connected.TrySetException(new NativePairingException(requestId));
            client.ConnectionFailure += ConnectionFailure;
            client.StatusChanged += StatusChanged;
            client.PairingRequired += PairingRequired;
            using var cancellation = linked.Token.Register(client.Dispose);
            try
            {
                await client.ConnectAsync().WaitAsync(linked.Token);
                await connected.Task.WaitAsync(TimeSpan.FromSeconds(20), linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                return new(owner, client, authorize);
            }
            catch (NativePairingException error) when (allowPairing && !pairingAttempted)
            {
                pairingAttempted = true;
                client.Dispose();
                await owner.ApproveWizardPairingAsync(error.RequestId, linked.Token);
            }
            catch (Exception error) when (
                allowPairing && !recoveryAttempted && storedDeviceToken is not null &&
                error is not OperationCanceledException &&
                observedFailure == GatewayErrorKind.DeviceTokenMismatch)
            {
                recoveryAttempted = true;
                client.Dispose();
                // Retire only this rejected operator credential; the shared setup token
                // then re-pairs the unchanged device identity on the next attempt.
                if (!await owner.RecoverRejectedOperatorTokenAsync(storedDeviceToken, linked.Token))
                    throw;
            }
            catch
            {
                client.Dispose();
                throw;
            }
            finally
            {
                client.ConnectionFailure -= ConnectionFailure;
                client.StatusChanged -= StatusChanged;
                client.PairingRequired -= PairingRequired;
            }
        }
        throw new InvalidOperationException("The Gateway did not accept this Companion after pairing.");
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters, int timeoutMs, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _owner.LifetimeToken);
        await _authorize(linked.Token);
        return await new GatewayAiSetupTransport(Client, () => Route).RequestAsync(method, parameters, timeoutMs, linked.Token);
    }

    public async Task<JsonElement> RequestMutationAsync(string method, object parameters, int timeoutMs,
        CancellationToken ct, Action? beforeDispatch = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _owner.LifetimeToken);
        await _authorize(linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        // After admission, even session cancellation must await the tracked RPC's
        // bounded result before the setup owner can release its mutation lifetime.
        return await new GatewayAiSetupTransport(Client, () => Route)
            .RequestMutationAsync(method, parameters, timeoutMs, linked.Token, beforeDispatch);
    }

    public async Task RestartAsync(CancellationToken ct)
    {
        var generation = Generation;
        _ = Route;
        await _owner.RestartAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _owner.LifetimeToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!IsConnected || Generation == generation)
            await Task.Delay(100, timeout.Token);
        _ = Route;
    }

    public async ValueTask DisposeAsync()
    {
        try { await Client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { Trace.TraceWarning("Native setup connection shutdown timed out."); }
        finally { Client.Dispose(); }
    }

    private sealed class NativePairingException(string? requestId)
        : InvalidOperationException(NativeGatewaySetupSession.GetPairingGuidance(requestId))
    {
        public string? RequestId { get; } = requestId;
    }
}
