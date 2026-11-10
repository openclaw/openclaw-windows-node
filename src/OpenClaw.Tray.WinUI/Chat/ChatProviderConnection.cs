using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

internal enum ChatVerificationState { None, Verifying, Failed }

internal interface IChatVerificationRecovery
{
    ChatVerificationState VerificationState { get; }
    bool RetryVerification();
}

internal interface IChatConnectionBoundProvider
{
    bool IsBoundTo(IOperatorGatewayClient client);
}

internal sealed record ChatConnectionAuthority(string GatewayId, string EndpointBinding, string SigningDeviceId, string ProfileId)
{
    internal bool CanRetain(ChatConnectionAuthority? next) =>
        !string.IsNullOrWhiteSpace(GatewayId) && !string.IsNullOrWhiteSpace(EndpointBinding) &&
        !string.IsNullOrWhiteSpace(SigningDeviceId) && !string.IsNullOrWhiteSpace(ProfileId) && this == next;
}

/// <summary>
/// UI-dispatched provider lifetime. Disconnect suspends its port, not its loaded
/// conversation. A replacement route must independently confirm endpoint, signing
/// device, profile and session identities before it can reuse that conversation.
/// </summary>
internal sealed class ChatProviderConnection : IDisposable, IChatVerificationRecovery
{
    private const int DefaultReadinessTimeoutMs = 12_000;
    private readonly Action<Action> _post;
    private readonly Func<IChatGatewayBridge, OpenClawChatDataProvider> _createProvider;
    private RetainedChatGatewayBridge _bridge;
    private GatewayRegistry? _registry;
    private GatewayRecord? _scope;
    private OpenClawGatewayClient? _client;
    // Receive-loop invalidation precedes its queued UI notification.
    private volatile ChatConnectionAuthority? _authority;
    private CancellationTokenSource? _verification;
    private Action? _unsubscribe;
    private int _version;
    private bool _disposed;

    internal ChatProviderConnection(Action<Action>? post, Func<IChatGatewayBridge, OpenClawChatDataProvider>? createProvider = null)
    {
        _bridge = new(this);
        _post = post ?? (action => action());
        _createProvider = createProvider ?? (bridge => new OpenClawChatDataProvider(bridge, post));
    }

    internal OpenClawChatDataProvider? Provider { get; private set; }
    public ChatVerificationState VerificationState { get; private set; }

    // Only the already-confirmed ordinary conversation can supply an offline row.
    // The live roster and creation readiness keep their independent authority gates.
    internal SessionInfo? RetainedNavigationSession(string? sessionKey) =>
        !_disposed && _authority is not null && CaptureScope() == _scope &&
        !_bridge.IsAttached && _client is not { IsConnectedToGateway: true }
            ? Provider?.RetainedNavigationSession(sessionKey) : null;

    public bool RetryVerification() => RetryVerificationForClient(null);

    internal bool RetryVerificationForClient(IOperatorGatewayClient? expectedClient)
    {
        if (expectedClient is not null && !ReferenceEquals(_client, expectedClient)) return false;
        if (_disposed || VerificationState != ChatVerificationState.Failed ||
            _client is not { IsConnectedToGateway: true } client || CaptureScope() != _scope) return false;
        BeginVerification(client, _version);
        return true;
    }

    private void SetVerificationState(ChatVerificationState state)
    {
        if (VerificationState == state) return;
        VerificationState = state;
        Provider?.NotifyVerificationChanged();
        Changed?.Invoke(this, EventArgs.Empty);
    }
    internal event EventHandler? Changed;
    private static int ReadinessTimeoutMs =>
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_CHAT_RECOVERY_TIMEOUT_MS"), out var value) && value > 0
            ? value : DefaultReadinessTimeoutMs;

    internal void ConfigureRegistry(GatewayRegistry registry)
    {
        if (_registry is not null) _registry.Changed -= OnRegistryChanged;
        _registry = registry;
        _registry.Changed += OnRegistryChanged;
    }

    // Friendly-name and last-connected bookkeeping do not change authority. The
    // immutable record comparison also invalidates credential edits without any
    // new credential-file read, token import or credential fallback.
    private GatewayRecord? CaptureScope() => _registry?.GetActive() is { } record
        ? record with { LastConnected = null, FriendlyName = null } : null;

    private bool Current(OpenClawGatewayClient client, int version) =>
        !_disposed && version == _version && ReferenceEquals(_client, client);

    internal void SetClient(OpenClawGatewayClient? client)
    {
        if (_disposed || ReferenceEquals(_client, client)) return;
        _unsubscribe?.Invoke();
        _unsubscribe = null;
        CancelVerification();
        Suspend();
        var scope = CaptureScope();
        if (_scope != scope || _authority is null) DropProvider();
        _scope = scope;
        _client = client;
        var version = ++_version;
        if (client is null) { SetVerificationState(ChatVerificationState.None); return; }

        EventHandler<ConnectionStatus> status = (_, state) =>
        {
            if (!Current(client, version)) return;
            if (state != ConnectionStatus.Connected) { CancelVerification(); Suspend(); }
            _post(() =>
            {
                if (!Current(client, version)) return;
                if (state == ConnectionStatus.Connected && client.IsConnectedToGateway) BeginVerification(client, version);
                else { CancelVerification(); SetVerificationState(ChatVerificationState.None); }
            });
        };
        EventHandler profile = (_, _) =>
        {
            if (!Current(client, version)) return;
            _authority = null;
            CancelVerification();
            Suspend();
            _post(() =>
            {
                if (!Current(client, version)) return;
                Forget();
                if (client.IsConnectedToGateway) BeginVerification(client, version);
            });
        };
        EventHandler<GatewayErrorKind> failure = (_, kind) =>
        {
            if (!InvalidatesAuthority(kind) || !Current(client, version)) return;
            _authority = null;
            CancelVerification();
            Suspend();
            _post(() => { if (Current(client, version)) Forget(); });
        };
        client.StatusChanged += status;
        client.SelfProfileChanged += profile;
        client.ConnectionFailure += failure;
        _unsubscribe = () =>
        {
            client.StatusChanged -= status;
            client.SelfProfileChanged -= profile;
            client.ConnectionFailure -= failure;
        };
        if (client.IsConnectedToGateway) BeginVerification(client, version);
    }

    internal void ObserveConnection(GatewayConnectionSnapshot snapshot)
    {
        if (snapshot.OperatorErrorKind is not { } kind || !InvalidatesAuthority(kind)) return;
        var version = _version;
        _authority = null;
        CancelVerification();
        Suspend();
        _post(() => { if (!_disposed && version == _version) Forget(); });
    }

    private static bool InvalidatesAuthority(GatewayErrorKind kind) => kind is
        GatewayErrorKind.Auth or GatewayErrorKind.TokenDrift or GatewayErrorKind.DeviceTokenMismatch or
        GatewayErrorKind.ScopeMismatch or GatewayErrorKind.PairingRequired or GatewayErrorKind.PairingRejected or
        GatewayErrorKind.Tls or GatewayErrorKind.LocalPortConflict;

    private void OnRegistryChanged(object? sender, GatewayRegistryChangedEventArgs args)
    {
        if (_disposed || CaptureScope() == _scope) return;
        _authority = null;
        CancelVerification();
        Suspend();
        _post(() =>
        {
            if (_disposed || CaptureScope() == _scope) return;
            var nextScope = CaptureScope();
            var sameEndpoint = _scope is not null && nextScope is not null &&
                GatewayDashboardBinding.Capture(_scope) == GatewayDashboardBinding.Capture(nextScope);
            _scope = nextScope;
            if (!sameEndpoint)
            {
                _unsubscribe?.Invoke();
                _unsubscribe = null;
                _client = null;
                _version++;
            }
            Forget();
            // Same-route bookkeeping may complete bootstrap while the accepted
            // client stays alive. It can mount fresh data, never reuse the old provider.
            if (_client is { IsConnectedToGateway: true } client) BeginVerification(client, _version);
        });
    }

    private void BeginVerification(OpenClawGatewayClient client, int version)
    {
        CancelVerification();
        Suspend();
        if (_authority is { } previous && client.AuthenticatedSigningDeviceId is { Length: > 0 } signingId &&
            previous.SigningDeviceId != signingId) Forget();
        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        Interlocked.Exchange(ref _verification, cancellation);
        SetVerificationState(ChatVerificationState.Verifying);
        _ = VerifyAsync(client, version, token, client.ServerHandshakeGeneration, CaptureScope());
    }

    private async Task VerifyAsync(OpenClawGatewayClient client, int version, CancellationToken token,
        long handshake, GatewayRecord? scope)
    {
        ChatConnectionAuthority? authority = null;
        SessionInfo[]? sessions = null;
        var rejected = false;
        var timedOut = false;
        try
        {
            var response = await client.RunWithRequestCancellationAsync(token,
                () => client.SendWizardRequestAsync("users.self", new { }, timeoutMs: ReadinessTimeoutMs)).ConfigureAwait(false);
            if (response.ValueKind != JsonValueKind.Object ||
                !response.TryGetProperty("profile", out var profile) || profile.ValueKind != JsonValueKind.Object ||
                !profile.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(id.GetString()))
                throw new JsonException("Invalid users.self profile identity.");
            if (scope is not null && client.AuthenticatedSigningDeviceId is { Length: > 0 } signingId)
                authority = new(scope.Id, GatewayDashboardBinding.Capture(scope), signingId, id.GetString()!);
            _post(() =>
            {
                if (!Current(client, version) || token.IsCancellationRequested ||
                    client.ServerHandshakeGeneration != handshake || CaptureScope() != scope) return;
                // A fresh profile response can disprove reuse before the roster
                // completes. Do not retain that known-foreign history on timeout.
                if (_authority?.CanRetain(authority) == false)
                {
                    _authority = null;
                    DropProvider();
                }
            });
            sessions = await client.RequestAuthoritativeSessionsAsync(ReadinessTimeoutMs, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (InvalidOperationException ex) when (authority is null &&
            ex.Message.Contains("unknown method", StringComparison.OrdinalIgnoreCase))
        {
            // Only explicit unsupported-method compatibility admits fresh chat.
            // Auth denials, timeouts and malformed rosters must remain blocked.
            Logger.Debug("[ChatConnection] Profile confirmation is unsupported; retained history cannot be reused.");
        }
        catch (TimeoutException) { timedOut = true; }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or OperationCanceledException)
        {
            rejected = true;
            Logger.Debug("[ChatConnection] Retained conversation authority could not be confirmed.");
        }
        _post(() =>
        {
            if (!Current(client, version) || token.IsCancellationRequested || !client.IsConnectedToGateway ||
                client.ServerHandshakeGeneration != handshake || CaptureScope() != scope) return;
            if (rejected || timedOut)
            {
                // A timeout does not disprove the previously confirmed identity.
                // Keep readable state suspended, but retire this verification and
                // require an explicit fresh check before any port can reattach.
                if (rejected) Forget(); else CancelVerification();
                SetVerificationState(ChatVerificationState.Failed);
                return;
            }
            if (_authority?.CanRetain(authority) != true || sessions is null ||
                Provider?.CanRetainHistory(sessions) == false) DropProvider();
            _authority = authority;
            _scope = scope;
            _bridge.Attach(new GatewayClientChatBridge(client));
            if (Provider is null)
            {
                Provider = _createProvider(_bridge);
            }
            else _bridge.StartProactiveBootstrap();
            SetVerificationState(ChatVerificationState.None);
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    private void Suspend()
    {
        try { _bridge.Attach(null); }
        catch (ObjectDisposedException) { /* A concurrent authority change already retired this port. */ }
    }

    private void CancelVerification()
    {
        var previous = Interlocked.Exchange(ref _verification, null);
        try { previous?.Cancel(); }
        finally { previous?.Dispose(); }
    }

    private void Forget()
    {
        CancelVerification();
        _authority = null;
        DropProvider();
        SetVerificationState(ChatVerificationState.None);
    }

    private void DropProvider()
    {
        var previous = Provider;
        Provider = null;
        if (previous is null) return;
        previous.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _bridge = new(this);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _version++;
        _unsubscribe?.Invoke();
        if (_registry is not null) _registry.Changed -= OnRegistryChanged;
        CancelVerification();
        DropProvider();
        _bridge.Dispose();
    }
}
