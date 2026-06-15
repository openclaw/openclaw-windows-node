using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

/// <summary>
/// Subset of <see cref="OpenClawGatewayClient"/> needed by
/// <see cref="OpenClawChatDataProvider"/>. Exposed as an interface so the
/// provider can be unit-tested without a live WebSocket connection.
/// </summary>
public interface IChatGatewayBridge : IDisposable
{
    bool IsConnected { get; }
    ConnectionStatus CurrentStatus { get; }
    /// <summary>Canonical main session key resolved by the gateway handshake; <c>null</c> until ready.</summary>
    string? MainSessionKey { get; }
    /// <summary>True once the gateway handshake has resolved session defaults.</summary>
    bool HasHandshakeSnapshot { get; }
    SessionInfo[] GetSessionList();
    ModelsListInfo? GetCurrentModelsList();

    /// <summary>
    /// If the underlying gateway client was already Connected by the time
    /// the bridge was constructed (so the bridge missed the
    /// <see cref="StatusChanged"/> → Connected edge), proactively re-request
    /// the models list and sessions snapshot so the chat composer's
    /// dropdowns populate without waiting for the user to send a message.
    ///
    /// Callers should invoke this AFTER subscribing to
    /// <see cref="ModelsListUpdated"/> and <see cref="SessionsUpdated"/> so
    /// they actually receive the resulting frames — firing the request
    /// before subscription leaves the response handler unset and the
    /// dropdowns stale until the next gateway-driven update.
    /// </summary>
    void StartProactiveBootstrap();

    Task SendChatMessageAsync(string message, string? sessionKey, string? sessionId, IReadOnlyList<ChatAttachment>? attachments = null);
    Task<ChatSendResult> SendChatMessageForRunAsync(string message, string? sessionKey, string? sessionId, IReadOnlyList<ChatAttachment>? attachments = null);
    Task PatchSessionModelAsync(string sessionKey, string model);
    Task PatchSessionThinkingLevelAsync(string sessionKey, string thinkingLevel);
    Task<ChatHistoryInfo> RequestChatHistoryAsync(string? sessionKey);
    Task SendChatAbortAsync(string runId, string? sessionKey = null);
    Task ResolveExecApprovalAsync(string approvalId, string decision);

    event EventHandler<ConnectionStatus>? StatusChanged;
    event EventHandler<SessionInfo[]>? SessionsUpdated;
    event EventHandler<SessionCommandResult>? SessionCommandCompleted;
    event EventHandler<ChatMessageInfo>? ChatMessageReceived;
    event EventHandler<AgentEventInfo>? AgentEventReceived;
    event EventHandler<ModelsListInfo>? ModelsListUpdated;
}

/// <summary>
/// Production bridge wrapping a real <see cref="OpenClawGatewayClient"/>.
/// </summary>
public sealed class GatewayClientChatBridge : IChatGatewayBridge
{
    private readonly OpenClawGatewayClient _client;
    private readonly EventHandler<ConnectionStatus> _statusChangedHandler;
    private readonly EventHandler<SessionInfo[]> _sessionsUpdatedHandler;
    private readonly EventHandler<SessionCommandResult> _sessionCommandCompletedHandler;
    private readonly EventHandler<ChatMessageInfo> _chatMessageReceivedHandler;
    private readonly EventHandler<AgentEventInfo> _agentEventReceivedHandler;
    private readonly EventHandler<ModelsListInfo> _modelsListUpdatedHandler;
    // _currentStatus is written from the gateway client's StatusChanged
    // callback (arbitrary thread) and read from CurrentStatus on the UI
    // thread. ``volatile`` gives us a memory barrier so the reader can't
    // observe a torn or stale value after the writer fires. Atomicity for
    // ConnectionStatus (4-byte enum) is guaranteed by the CLR.
    //
    // Seeded from the client's current state *before* the StatusChanged
    // handler is subscribed (see ctor) so a real StatusChanged edge that
    // fires during construction can't be stomped back by a stale seed.
    private volatile ConnectionStatus _currentStatus = ConnectionStatus.Disconnected;
    private ModelsListInfo? _currentModels;
    private bool _disposed;

    public GatewayClientChatBridge(OpenClawGatewayClient client)
    {
        _client = client;
        _statusChangedHandler = (s, e) =>
        {
            _currentStatus = e;
            StatusChanged?.Invoke(s, e);
            // Fetch the available models list whenever we connect so the
            // chat composer dropdown is populated without needing to open
            // the Hub's SessionsPage first.
            if (e == ConnectionStatus.Connected)
            {
                Logger.Info("[ChatBridge] StatusChanged→Connected: requesting models.list");
                _ = _client.RequestModelsListAsync();
            }
        };
        _sessionsUpdatedHandler = (s, e) => SessionsUpdated?.Invoke(s, e);
        _sessionCommandCompletedHandler = (s, e) => SessionCommandCompleted?.Invoke(s, e);
        _chatMessageReceivedHandler = (s, e) => ChatMessageReceived?.Invoke(s, e);
        _agentEventReceivedHandler = (s, e) => AgentEventReceived?.Invoke(s, e);
        _modelsListUpdatedHandler = (s, e) =>
        {
            _currentModels = e;
            ModelsListUpdated?.Invoke(s, e);
        };

        // Subscribe StatusChanged BEFORE reading the seed so any
        // ``StatusChanged → X`` edge that fires during construction is
        // captured by our handler. We then read the live property and
        // reconcile ``_currentStatus`` so callers that hit
        // ``CurrentStatus`` immediately (before any further edge) see
        // truth rather than the default ``Disconnected``.
        //
        // The seed only writes if ``_currentStatus`` is still
        // ``Disconnected`` (its default). If a handler edge fired in
        // the subscribe→read window — including intermediate states
        // like ``Connecting`` — the handler's write is preserved
        // rather than collapsed by the 2-state read of
        // ``IsConnectedToGateway``. ``volatile`` covers atomic reads.
        _client.StatusChanged += _statusChangedHandler;
        _client.SessionsUpdated += _sessionsUpdatedHandler;
        _client.SessionCommandCompleted += _sessionCommandCompletedHandler;
        _client.ChatMessageReceived += _chatMessageReceivedHandler;
        _client.AgentEventReceived += _agentEventReceivedHandler;
        _client.ModelsListUpdated += _modelsListUpdatedHandler;

        if (_currentStatus == ConnectionStatus.Disconnected)
        {
            _currentStatus = _client.IsConnectedToGateway
                ? ConnectionStatus.Connected
                : ConnectionStatus.Disconnected;
        }

        Logger.Info($"[ChatBridge] ctor: IsConnectedToGateway={_client.IsConnectedToGateway}");
        // The actual proactive models.list/sessions.list request is
        // deferred to StartProactiveBootstrap() — kicking it off here
        // would race the provider's subscription to ModelsListUpdated:
        // the response can arrive before the provider has wired its
        // handler, leaving the composer dropdowns empty until the next
        // gateway-driven update.
    }

    public void StartProactiveBootstrap()
    {
        if (_disposed) return;
        if (!_client.IsConnectedToGateway) return;
        Logger.Info("[ChatBridge] proactive: requesting models.list and sessions.list");
        try { _ = _client.RequestModelsListAsync(); } catch (Exception ex) { Logger.Warn($"[ChatBridge] proactive models.list failed: {ex.Message}"); }
        try { _ = _client.RequestSessionsAsync(); } catch (Exception ex) { Logger.Warn($"[ChatBridge] proactive sessions.list failed: {ex.Message}"); }
    }

    public bool IsConnected => _client.IsConnectedToGateway;
    public ConnectionStatus CurrentStatus => _currentStatus;
    public string? MainSessionKey => _client.MainSessionKey;
    public bool HasHandshakeSnapshot => _client.HasHandshakeSnapshot;
    public SessionInfo[] GetSessionList() => _client.GetSessionList();
    public ModelsListInfo? GetCurrentModelsList() => _currentModels;

    public Task SendChatMessageAsync(string message, string? sessionKey, string? sessionId, IReadOnlyList<ChatAttachment>? attachments = null) =>
        _client.SendChatMessageAsync(message, sessionKey, sessionId, attachments);

    public Task<ChatSendResult> SendChatMessageForRunAsync(string message, string? sessionKey, string? sessionId, IReadOnlyList<ChatAttachment>? attachments = null) =>
        _client.SendChatMessageForRunAsync(message, sessionKey, sessionId, attachments);

    public Task PatchSessionModelAsync(string sessionKey, string model) =>
        _client.PatchSessionAsync(sessionKey, model: model);

    public Task PatchSessionThinkingLevelAsync(string sessionKey, string thinkingLevel) =>
        _client.PatchSessionAsync(sessionKey, thinkingLevel: thinkingLevel);

    public Task<ChatHistoryInfo> RequestChatHistoryAsync(string? sessionKey) =>
        _client.RequestChatHistoryAsync(sessionKey);

    public Task SendChatAbortAsync(string runId, string? sessionKey = null) => _client.SendChatAbortAsync(runId, sessionKey);

    public Task ResolveExecApprovalAsync(string approvalId, string decision) =>
        _client.ResolveExecApprovalAsync(approvalId, decision);

    public event EventHandler<ConnectionStatus>? StatusChanged;
    public event EventHandler<SessionInfo[]>? SessionsUpdated;
    public event EventHandler<SessionCommandResult>? SessionCommandCompleted;
    public event EventHandler<ChatMessageInfo>? ChatMessageReceived;
    public event EventHandler<AgentEventInfo>? AgentEventReceived;
    public event EventHandler<ModelsListInfo>? ModelsListUpdated;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _client.StatusChanged -= _statusChangedHandler;
        _client.SessionsUpdated -= _sessionsUpdatedHandler;
        _client.SessionCommandCompleted -= _sessionCommandCompletedHandler;
        _client.ChatMessageReceived -= _chatMessageReceivedHandler;
        _client.AgentEventReceived -= _agentEventReceivedHandler;
        _client.ModelsListUpdated -= _modelsListUpdatedHandler;

        StatusChanged = null;
        SessionsUpdated = null;
        SessionCommandCompleted = null;
        ChatMessageReceived = null;
        AgentEventReceived = null;
        ModelsListUpdated = null;
    }
}
