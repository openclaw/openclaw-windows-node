using OpenClaw.Shared;

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

    Task SendChatMessageAsync(string message, string? sessionKey, string? sessionId, IReadOnlyList<ChatAttachment>? attachments = null);
    Task PatchSessionModelAsync(string sessionKey, string model);
    Task PatchSessionThinkingLevelAsync(string sessionKey, string thinkingLevel);
    Task<ChatHistoryInfo> RequestChatHistoryAsync(string? sessionKey);
    Task SendChatAbortAsync(string runId, string? sessionKey = null);

    event EventHandler<ConnectionStatus>? StatusChanged;
    event EventHandler<SessionInfo[]>? SessionsUpdated;
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
    private readonly EventHandler<ChatMessageInfo> _chatMessageReceivedHandler;
    private readonly EventHandler<AgentEventInfo> _agentEventReceivedHandler;
    private readonly EventHandler<ModelsListInfo> _modelsListUpdatedHandler;
    private ConnectionStatus _currentStatus = ConnectionStatus.Disconnected;
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
                _ = _client.RequestModelsListAsync();
            }
        };
        _sessionsUpdatedHandler = (s, e) => SessionsUpdated?.Invoke(s, e);
        _chatMessageReceivedHandler = (s, e) => ChatMessageReceived?.Invoke(s, e);
        _agentEventReceivedHandler = (s, e) => AgentEventReceived?.Invoke(s, e);
        _modelsListUpdatedHandler = (s, e) =>
        {
            _currentModels = e;
            ModelsListUpdated?.Invoke(s, e);
        };

        _client.StatusChanged += _statusChangedHandler;
        _client.SessionsUpdated += _sessionsUpdatedHandler;
        _client.ChatMessageReceived += _chatMessageReceivedHandler;
        _client.AgentEventReceived += _agentEventReceivedHandler;
        _client.ModelsListUpdated += _modelsListUpdatedHandler;
    }

    public bool IsConnected => _client.IsConnectedToGateway;
    public ConnectionStatus CurrentStatus => _currentStatus;
    public string? MainSessionKey => _client.MainSessionKey;
    public bool HasHandshakeSnapshot => _client.HasHandshakeSnapshot;
    public SessionInfo[] GetSessionList() => _client.GetSessionList();
    public ModelsListInfo? GetCurrentModelsList() => _currentModels;

    public Task SendChatMessageAsync(string message, string? sessionKey, string? sessionId, IReadOnlyList<ChatAttachment>? attachments = null) =>
        _client.SendChatMessageAsync(message, sessionKey, sessionId, attachments);

    public Task PatchSessionModelAsync(string sessionKey, string model) =>
        _client.PatchSessionAsync(sessionKey, model: model);

    public Task PatchSessionThinkingLevelAsync(string sessionKey, string thinkingLevel) =>
        _client.PatchSessionAsync(sessionKey, thinkingLevel: thinkingLevel);

    public Task<ChatHistoryInfo> RequestChatHistoryAsync(string? sessionKey) =>
        _client.RequestChatHistoryAsync(sessionKey);

    public Task SendChatAbortAsync(string runId, string? sessionKey = null) => _client.SendChatAbortAsync(runId, sessionKey);

    public event EventHandler<ConnectionStatus>? StatusChanged;
    public event EventHandler<SessionInfo[]>? SessionsUpdated;
    public event EventHandler<ChatMessageInfo>? ChatMessageReceived;
    public event EventHandler<AgentEventInfo>? AgentEventReceived;
    public event EventHandler<ModelsListInfo>? ModelsListUpdated;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _client.StatusChanged -= _statusChangedHandler;
        _client.SessionsUpdated -= _sessionsUpdatedHandler;
        _client.ChatMessageReceived -= _chatMessageReceivedHandler;
        _client.AgentEventReceived -= _agentEventReceivedHandler;
        _client.ModelsListUpdated -= _modelsListUpdatedHandler;

        StatusChanged = null;
        SessionsUpdated = null;
        ChatMessageReceived = null;
        AgentEventReceived = null;
        ModelsListUpdated = null;
    }
}
