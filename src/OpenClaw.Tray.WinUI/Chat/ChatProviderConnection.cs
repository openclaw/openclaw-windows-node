using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

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
internal sealed class ChatProviderConnection : IDisposable
{
    private const int DefaultReadinessTimeoutMs = 12_000;
    private readonly Action<Action> _post;
    private readonly Func<IChatGatewayBridge, OpenClawChatDataProvider> _createProvider;
    private RetainedChatGatewayBridge _bridge = new();
    private GatewayRegistry? _registry;
    private GatewayRecord? _scope;
    private OpenClawGatewayClient? _client;
    private ChatConnectionAuthority? _authority;
    private CancellationTokenSource? _verification;
    private Action? _unsubscribe;
    private int _version;
    private bool _disposed;

    internal ChatProviderConnection(Action<Action>? post, Func<IChatGatewayBridge, OpenClawChatDataProvider>? createProvider = null)
    {
        _post = post ?? (action => action());
        _createProvider = createProvider ?? (bridge => new OpenClawChatDataProvider(bridge, post));
    }

    internal OpenClawChatDataProvider? Provider { get; private set; }
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
        if (client is null) return;

        EventHandler<ConnectionStatus> status = (_, state) =>
        {
            if (!Current(client, version)) return;
            if (state != ConnectionStatus.Connected) Suspend();
            _post(() =>
            {
                if (!Current(client, version)) return;
                if (state == ConnectionStatus.Connected && client.IsConnectedToGateway) BeginVerification(client, version);
                else CancelVerification();
            });
        };
        EventHandler profile = (_, _) =>
        {
            if (!Current(client, version)) return;
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
        var cancellation = new CancellationTokenSource();
        _verification = cancellation;
        _ = VerifyAsync(client, version, cancellation.Token, client.ServerHandshakeGeneration, CaptureScope());
    }

    private async Task VerifyAsync(OpenClawGatewayClient client, int version, CancellationToken token,
        long handshake, GatewayRecord? scope)
    {
        ChatConnectionAuthority? authority = null;
        SessionInfo[]? sessions = null;
        try
        {
            var response = await client.SendWizardRequestAsync("users.self", new { }, timeoutMs: ReadinessTimeoutMs)
                .WaitAsync(token).ConfigureAwait(false);
            if (scope is not null && response.ValueKind == JsonValueKind.Object &&
                response.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.Object &&
                profile.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                client.AuthenticatedSigningDeviceId is { Length: > 0 } signingId && id.GetString() is { Length: > 0 } profileId)
                authority = new(scope.Id, GatewayDashboardBinding.Capture(scope), signingId, profileId);
            var roster = new TaskCompletionSource<SessionInfo[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnSessions(object? sender, SessionInfo[] rows) => roster.TrySetResult(rows);
            client.SessionsUpdated += OnSessions;
            try
            {
                await client.RequestSessionsAsync().WaitAsync(token).ConfigureAwait(false);
                sessions = await roster.Task.WaitAsync(TimeSpan.FromMilliseconds(ReadinessTimeoutMs), token).ConfigureAwait(false);
            }
            finally { client.SessionsUpdated -= OnSessions; }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or JsonException or OperationCanceledException)
        {
            // Older Gateways may lack users.self. They may mount a fresh provider,
            // but unknown authority must never reactivate the retained transcript.
            Logger.Debug("[ChatConnection] Retained conversation authority could not be confirmed.");
        }
        _post(() =>
        {
            if (!Current(client, version) || token.IsCancellationRequested || !client.IsConnectedToGateway ||
                client.ServerHandshakeGeneration != handshake || CaptureScope() != scope) return;
            if (_authority?.CanRetain(authority) != true || sessions is null ||
                Provider?.CanRetainHistory(client.GetSessionList()) == false) DropProvider();
            _authority = authority;
            _scope = scope;
            _bridge.Attach(new GatewayClientChatBridge(client));
            if (Provider is null)
            {
                Provider = _createProvider(_bridge);
            }
            else _bridge.StartProactiveBootstrap();
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
        var previous = _verification;
        _verification = null;
        previous?.Cancel();
        previous?.Dispose();
    }

    private void Forget()
    {
        CancelVerification();
        _authority = null;
        DropProvider();
    }

    private void DropProvider()
    {
        var previous = Provider;
        Provider = null;
        if (previous is null) return;
        previous.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _bridge = new();
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
