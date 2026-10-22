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
    long ConnectionEpoch => 0;
    object ConnectionOwner => this;
    bool SupportsInputCustody => false;
    bool CanWriteInputs => false;
    /// <summary>Canonical main session key resolved by the gateway handshake; <c>null</c> until ready.</summary>
    string? MainSessionKey { get; }
    /// <summary>True once the gateway handshake has resolved session defaults.</summary>
    bool HasHandshakeSnapshot { get; }
    IChatPlacementGateway? PlacementGateway => null;
    SessionInfo[] GetSessionList();
    ModelsListInfo? GetCurrentModelsList();
    bool SupportsSessionScopedModelCatalog => false;
    Task<ModelsListInfo?> RequestSessionModelsListAsync(string sessionKey) =>
        Task.FromResult<ModelsListInfo?>(null);

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
    Task<ChatSendResult> SendChatMessageForRunAsync(
        string message,
        string? sessionKey,
        string? sessionId,
        IReadOnlyList<ChatAttachment>? attachments = null,
        string? idempotencyKey = null);
    Task<ChatSendResult> SendChatMessageForRunAtEpochAsync(
        string message, string sessionKey, string? sessionId,
        IReadOnlyList<ChatAttachment>? attachments, string idempotencyKey, long expectedConnectionEpoch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        if (ConnectionEpoch != expectedConnectionEpoch)
            throw new InvalidOperationException("The Gateway connection changed before chat.send could be sent.");
        return SendChatMessageForRunAsync(message, sessionKey, sessionId, attachments, idempotencyKey);
    }
    Task<ChatSendResult> SendChatInputAtEpochAsync(
        string message, string sessionKey, string? sessionId, IReadOnlyList<ChatAttachment>? attachments,
        string idempotencyKey, long expectedConnectionEpoch, ChatSendOptions options)
    {
        if (options.QueueMode is not null)
            throw new NotSupportedException("The Gateway does not advertise the input-custody transport contract.");
        if (options.IsCurrent?.Invoke() == false)
            throw new ChatSendDeliveryException("The originating conversation changed. No input was sent.", false);
        return SendChatMessageForRunAtEpochAsync(
            message, sessionKey, sessionId, attachments, idempotencyKey, expectedConnectionEpoch);
    }
    Task<ChatHistoryInfo> RequestChatInputHistoryAsync(
        string sessionKey, string? sessionId, IReadOnlyList<string> inputRunIds, long expectedConnectionEpoch,
        string? agentId = null) =>
        throw new NotSupportedException("The Gateway does not advertise input receipts.");
    /// <summary>
    /// Fetches the gateway command catalog (<c>commands.list</c>) via the typed
    /// protocol API. Returns a <see cref="CommandCatalog"/> whose
    /// <see cref="CommandCatalog.IsSupported"/> is <c>false</c> when the gateway
    /// does not implement the method. Request/response — no event subscription.
    /// </summary>
    Task<CommandCatalog> ListCommandsAsync(CommandCatalogQuery? query = null);
    /// <summary>Returns null only when the Gateway does not support shared welcome content.</summary>
    Task<ChatWelcomeCatalog?> GetWelcomeCatalogAsync(string locale) =>
        Task.FromResult<ChatWelcomeCatalog?>(null);
    Task<SessionCreateResult> CreateSessionAsync(SessionCreateRequest request) =>
        Task.FromResult(new SessionCreateResult
        {
            Ok = false,
            IsSupported = false,
            Error = "sessions.create is not supported by this chat bridge."
        });
    Task<bool> ResetSessionAsync(string sessionKey) => Task.FromResult(false);
    Task<SessionResetResult> ResetSessionDetailedAsync(string sessionKey) =>
        Task.FromResult(new SessionResetResult
        {
            Ok = false,
            Error = "Response-aware sessions.reset is not supported by this chat bridge."
        });
    Task<bool> CompactSessionAsync(string sessionKey, int maxLines = 400) => Task.FromResult(false);
    Task<SessionCompactResult> CompactSessionDetailedAsync(string sessionKey) =>
        Task.FromResult(new SessionCompactResult
        {
            Ok = false,
            Error = "Response-aware sessions.compact is not supported by this chat bridge."
        });
    Task RequestSessionsAsync() => Task.CompletedTask;
    Task PatchSessionModelAsync(string sessionKey, string model);
    /// <summary>
    /// Clears the session's model override (tri-state <c>sessions.patch</c> with
    /// an explicit JSON null), reverting the session to the gateway/agent
    /// default. Distinct from <see cref="PatchSessionModelAsync"/>, which sets a
    /// concrete model.
    /// </summary>
    Task ClearSessionModelAsync(string sessionKey);
    Task PatchSessionThinkingLevelAsync(string sessionKey, string thinkingLevel);
    /// <summary>
    /// Clears the session's thinking-level override with an explicit JSON null,
    /// restoring the gateway/provider default.
    /// </summary>
    Task ClearSessionThinkingLevelAsync(string sessionKey);
    Task PatchSessionModelAtEpochAsync(string key, string model, long epoch, Func<bool>? isCurrent = null)
    {
        if (isCurrent is not null) throw new NotSupportedException("This adapter does not support origin-bound model writes.");
        if (ConnectionEpoch != epoch) throw new InvalidOperationException("The Gateway connection changed before the model update.");
        return PatchSessionModelAsync(key, model);
    }
    Task ClearSessionModelAtEpochAsync(string key, long epoch, Func<bool>? isCurrent = null)
    {
        if (isCurrent is not null) throw new NotSupportedException("This adapter does not support origin-bound model writes.");
        if (ConnectionEpoch != epoch) throw new InvalidOperationException("The Gateway connection changed before the model update.");
        return ClearSessionModelAsync(key);
    }
    Task PatchSessionThinkingLevelAtEpochAsync(string key, string level, long epoch, Func<bool>? isCurrent = null)
    {
        if (isCurrent is not null) throw new NotSupportedException("This adapter does not support origin-bound reasoning writes.");
        if (ConnectionEpoch != epoch) throw new InvalidOperationException("The Gateway connection changed before the reasoning update.");
        return PatchSessionThinkingLevelAsync(key, level);
    }
    Task ClearSessionThinkingLevelAtEpochAsync(string key, long epoch, Func<bool>? isCurrent = null)
    {
        if (isCurrent is not null) throw new NotSupportedException("This adapter does not support origin-bound reasoning writes.");
        if (ConnectionEpoch != epoch) throw new InvalidOperationException("The Gateway connection changed before the reasoning update.");
        return ClearSessionThinkingLevelAsync(key);
    }
    Task<ChatHistoryInfo> RequestChatHistoryAsync(string? sessionKey);
    Task<AssistantMediaResolutionResult> ResolveAssistantMediaAsync(
        string sessionKey,
        ChatMediaContentInfo media,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(AssistantMediaResolutionResult.Unavailable);
    /// <summary>
    /// Downloads a structured file attachment through the default browser. The bridge
    /// resolves the ticketed Gateway URL and launches it itself; callers only receive the
    /// typed outcome, never the URL or ticket.
    /// </summary>
    Task<AssistantFileDownloadStatus> DownloadAssistantFileAsync(
        string sessionKey,
        ChatMediaContentInfo media,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(AssistantFileDownloadStatus.Unavailable);
    Task SendChatAbortAsync(string runId, string? sessionKey = null);
    Task ResolveExecApprovalAsync(string approvalId, string decision);
    Task SendChatAbortAtEpochAsync(string runId, string sessionKey, long expectedConnectionEpoch, Func<bool>? isCurrent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        if (isCurrent is not null)
            throw new NotSupportedException("This adapter cannot enforce scoped chat.abort writes.");
        if (ConnectionEpoch != expectedConnectionEpoch)
            throw new InvalidOperationException("The Gateway connection changed before chat.abort could be sent.");
        return SendChatAbortAsync(runId, sessionKey);
    }
    Task ResolveExecApprovalAtEpochAsync(string approvalId, string decision, long expectedConnectionEpoch)
    {
        if (ConnectionEpoch != expectedConnectionEpoch)
            throw new InvalidOperationException("The Gateway connection changed before exec.approval.resolve could be sent.");
        return ResolveExecApprovalAsync(approvalId, decision);
    }

    event EventHandler<ConnectionStatus>? StatusChanged;
    event EventHandler<SessionInfo[]>? SessionsUpdated;
    event EventHandler<SessionUsageSnapshot>? SessionUsageSnapshotUpdated;
    event EventHandler<SessionCommandResult>? SessionCommandCompleted;
    event EventHandler<ChatMessageInfo>? ChatMessageReceived;
    event EventHandler<AgentEventInfo>? AgentEventReceived;
    event EventHandler<ModelsListInfo>? ModelsListUpdated;
    event EventHandler? ModelCatalogInvalidated
    {
        add { }
        remove { }
    }
    event EventHandler? InputAuthorityChanged
    {
        add { }
        remove { }
    }
}

/// <summary>
/// Production bridge wrapping a real <see cref="OpenClawGatewayClient"/>.
/// </summary>
public sealed class GatewayClientChatBridge : IChatGatewayBridge
{
    private readonly OpenClawGatewayClient _client;
    private readonly EventHandler<ConnectionStatus> _statusChangedHandler;
    private readonly EventHandler<SessionInfo[]> _sessionsUpdatedHandler;
    private readonly EventHandler<SessionUsageSnapshot> _sessionUsageSnapshotUpdatedHandler;
    private readonly EventHandler<SessionCommandResult> _sessionCommandCompletedHandler;
    private readonly EventHandler<ChatMessageInfo> _chatMessageReceivedHandler;
    private readonly EventHandler<AgentEventInfo> _agentEventReceivedHandler;
    private readonly EventHandler<ModelsListInfo> _modelsListUpdatedHandler;
    private readonly EventHandler _modelCatalogInvalidatedHandler;
    private readonly EventHandler _inputAuthorityChangedHandler;
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
    private readonly Func<Uri, Task<bool>> _launchUri;
    private bool _disposed;

    public GatewayClientChatBridge(
        OpenClawGatewayClient client,
        Func<Uri, Task<bool>>? launchUri = null)
    {
        _client = client;
        PlacementGateway = client.CreateChatPlacementGateway();
        _launchUri = launchUri ?? LaunchWithDefaultBrowserAsync;
        _statusChangedHandler = (s, e) =>
        {
            _currentStatus = e;
            if (e != ConnectionStatus.Connected)
                _currentModels = null;
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
        _sessionUsageSnapshotUpdatedHandler = (s, e) =>
            SessionUsageSnapshotUpdated?.Invoke(s, e);
        _sessionCommandCompletedHandler = (s, e) => SessionCommandCompleted?.Invoke(s, e);
        _chatMessageReceivedHandler = (s, e) => ChatMessageReceived?.Invoke(s, e);
        _agentEventReceivedHandler = (s, e) => AgentEventReceived?.Invoke(s, e);
        _modelsListUpdatedHandler = (s, e) =>
        {
            _currentModels = e;
            ModelsListUpdated?.Invoke(s, e);
        };
        _modelCatalogInvalidatedHandler = (s, e) => ModelCatalogInvalidated?.Invoke(s, e);
        _inputAuthorityChangedHandler = (s, e) => InputAuthorityChanged?.Invoke(s, e);

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
        _client.SessionUsageSnapshotUpdated += _sessionUsageSnapshotUpdatedHandler;
        _client.SessionCommandCompleted += _sessionCommandCompletedHandler;
        _client.ChatMessageReceived += _chatMessageReceivedHandler;
        _client.AgentEventReceived += _agentEventReceivedHandler;
        _client.ModelsListUpdated += _modelsListUpdatedHandler;
        _client.ModelCatalogInvalidated += _modelCatalogInvalidatedHandler;
        _client.SelfProfileChanged += _inputAuthorityChangedHandler;

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
    public IChatPlacementGateway PlacementGateway { get; }
    public ConnectionStatus CurrentStatus => _currentStatus;
    public long ConnectionEpoch => _client.SessionMutationConnectionEpoch ?? -1;
    public object ConnectionOwner => _client;
    public bool SupportsInputCustody => _client.AdvertisedServerCapabilities.Contains(ChatInputCustody.Capability);
    public bool CanWriteInputs => _client.IsConnectedToGateway &&
        _client.GrantedOperatorScopes.Any(scope => scope is "operator.write" or "operator.admin");
    public string? MainSessionKey => _client.MainSessionKey;
    public bool HasHandshakeSnapshot => _client.HasHandshakeSnapshot;
    public SessionInfo[] GetSessionList() => _client.GetSessionList();
    public ModelsListInfo? GetCurrentModelsList() => _currentModels;
    public bool SupportsSessionScopedModelCatalog =>
        _client.AdvertisedServerCapabilities.Contains(
            "session-scoped-model-catalog",
            StringComparer.Ordinal);
    public Task<ModelsListInfo?> RequestSessionModelsListAsync(string sessionKey) =>
        _client.RequestSessionModelsListAsync(sessionKey);

    public Task SendChatMessageAsync(string message, string? sessionKey, string? sessionId, IReadOnlyList<ChatAttachment>? attachments = null) =>
        _client.SendChatMessageAsync(message, sessionKey, sessionId, attachments);

    public Task<ChatSendResult> SendChatMessageForRunAsync(
        string message,
        string? sessionKey,
        string? sessionId,
        IReadOnlyList<ChatAttachment>? attachments = null,
        string? idempotencyKey = null) =>
        _client.SendChatMessageForRunAsync(message, sessionKey, sessionId, attachments, idempotencyKey);

    public Task<ChatSendResult> SendChatMessageForRunAtEpochAsync(
        string message, string sessionKey, string? sessionId,
        IReadOnlyList<ChatAttachment>? attachments, string idempotencyKey, long expectedConnectionEpoch) =>
        _client.SendChatMessageForRunAsync(message, sessionKey, sessionId, attachments, idempotencyKey,
            expectedConnectionEpoch);

    public Task<ChatSendResult> SendChatInputAtEpochAsync(
        string message, string sessionKey, string? sessionId, IReadOnlyList<ChatAttachment>? attachments,
        string idempotencyKey, long expectedConnectionEpoch, ChatSendOptions options) =>
        _client.SendChatMessageForRunAsync(message, sessionKey, sessionId, attachments, idempotencyKey,
            expectedConnectionEpoch, options);

    public Task<ChatHistoryInfo> RequestChatInputHistoryAsync(
        string sessionKey, string? sessionId, IReadOnlyList<string> inputRunIds, long expectedConnectionEpoch,
        string? agentId = null) =>
        _client.RequestChatInputHistoryAsync(sessionKey, sessionId, inputRunIds, expectedConnectionEpoch, agentId);

    public Task PatchSessionModelAsync(string sessionKey, string model) =>
        _client.PatchSessionAsync(sessionKey, new SessionPatch { Model = model });

    public Task ClearSessionModelAsync(string sessionKey) =>
        _client.PatchSessionAsync(sessionKey, new SessionPatch { Model = SessionPatch.Clear });

    public Task PatchSessionThinkingLevelAsync(string sessionKey, string thinkingLevel) =>
        _client.PatchSessionAsync(sessionKey, new SessionPatch { ThinkingLevel = thinkingLevel });

    public Task ClearSessionThinkingLevelAsync(string sessionKey) =>
        _client.PatchSessionAsync(sessionKey, new SessionPatch { ThinkingLevel = SessionPatch.Clear });

    public Task PatchSessionModelAtEpochAsync(string key, string model, long epoch, Func<bool>? isCurrent = null) =>
        PatchChatSessionOptionsAsync(key, new SessionPatch { Model = model }, epoch, isCurrent);
    public Task ClearSessionModelAtEpochAsync(string key, long epoch, Func<bool>? isCurrent = null) =>
        PatchChatSessionOptionsAsync(key, new SessionPatch { Model = SessionPatch.Clear }, epoch, isCurrent);
    public Task PatchSessionThinkingLevelAtEpochAsync(string key, string level, long epoch, Func<bool>? isCurrent = null) =>
        PatchChatSessionOptionsAsync(key, new SessionPatch { ThinkingLevel = level }, epoch, isCurrent);
    public Task ClearSessionThinkingLevelAtEpochAsync(string key, long epoch, Func<bool>? isCurrent = null) =>
        PatchChatSessionOptionsAsync(key, new SessionPatch { ThinkingLevel = SessionPatch.Clear }, epoch, isCurrent);

    private async Task PatchChatSessionOptionsAsync(string key, SessionPatch patch, long epoch, Func<bool>? isCurrent)
    {
        if (isCurrent is null)
            await _client.PatchSessionConfirmedAsync(key, patch, epoch).ConfigureAwait(false);
        else
            await _client.PatchSessionConfirmedScopedAsync(key, patch, epoch, isCurrent).ConfigureAwait(false);
        if (_client.SessionMutationConnectionEpoch == epoch && (isCurrent?.Invoke() ?? true))
            await _client.RequestSessionsAsync().ConfigureAwait(false);
    }

    public Task<CommandCatalog> ListCommandsAsync(CommandCatalogQuery? query = null) =>
        _client.ListCommandsAsync(query);

    public Task<ChatWelcomeCatalog?> GetWelcomeCatalogAsync(string locale) =>
        _client.GetChatWelcomeCatalogAsync(locale);

    public Task<SessionCreateResult> CreateSessionAsync(SessionCreateRequest request) =>
        _client.CreateSessionAsync(request);

    public Task<bool> ResetSessionAsync(string sessionKey) =>
        _client.ResetSessionAsync(sessionKey);

    public Task<SessionResetResult> ResetSessionDetailedAsync(string sessionKey) =>
        _client.ResetSessionDetailedAsync(sessionKey);

    public Task<bool> CompactSessionAsync(string sessionKey, int maxLines = 400) =>
        _client.CompactSessionAsync(sessionKey, maxLines);

    public Task<SessionCompactResult> CompactSessionDetailedAsync(string sessionKey) =>
        _client.CompactSessionDetailedAsync(sessionKey);

    public Task RequestSessionsAsync() =>
        _client.RequestSessionsAsync();

    public Task<ChatHistoryInfo> RequestChatHistoryAsync(string? sessionKey) =>
        _client.RequestChatHistoryAsync(sessionKey);

    public Task<AssistantMediaResolutionResult> ResolveAssistantMediaAsync(
        string sessionKey,
        ChatMediaContentInfo media,
        CancellationToken cancellationToken = default) =>
        _client.ResolveAssistantMediaAsync(sessionKey, media, cancellationToken);

#if OPENCLAW_TRAY_TESTS
    // The test host has no WinRT launcher; tests inject one through the constructor.
    private static Task<bool> LaunchWithDefaultBrowserAsync(Uri uri) => Task.FromResult(false);
#else
    private static async Task<bool> LaunchWithDefaultBrowserAsync(Uri uri) =>
        await global::Windows.System.Launcher.LaunchUriAsync(uri);
#endif

    public Task<AssistantFileDownloadStatus> DownloadAssistantFileAsync(
        string sessionKey,
        ChatMediaContentInfo media,
        CancellationToken cancellationToken = default) =>
        AssistantFileDownloadLauncher.DownloadAsync(
            ct => _client.ResolveAssistantFileDownloadUriAsync(sessionKey, media, ct),
            _launchUri,
            cancellationToken);

    public Task SendChatAbortAsync(string runId, string? sessionKey = null) => _client.SendChatAbortAsync(runId, sessionKey);

    public Task ResolveExecApprovalAsync(string approvalId, string decision) =>
        _client.ResolveExecApprovalAsync(approvalId, decision);

    public Task SendChatAbortAtEpochAsync(string runId, string sessionKey, long expectedConnectionEpoch, Func<bool>? isCurrent = null) =>
        isCurrent is null
            ? _client.SendChatAbortAsync(runId, sessionKey, expectedConnectionEpoch: expectedConnectionEpoch)
            : _client.SendChatAbortScopedAsync(runId, sessionKey, expectedConnectionEpoch, isCurrent);

    public Task ResolveExecApprovalAtEpochAsync(string approvalId, string decision, long expectedConnectionEpoch) =>
        _client.ResolveExecApprovalAsync(approvalId, decision, expectedConnectionEpoch);

    public event EventHandler<ConnectionStatus>? StatusChanged;
    public event EventHandler<SessionInfo[]>? SessionsUpdated;
    public event EventHandler<SessionUsageSnapshot>? SessionUsageSnapshotUpdated;
    public event EventHandler<SessionCommandResult>? SessionCommandCompleted;
    public event EventHandler<ChatMessageInfo>? ChatMessageReceived;
    public event EventHandler<AgentEventInfo>? AgentEventReceived;
    public event EventHandler<ModelsListInfo>? ModelsListUpdated;
    public event EventHandler? ModelCatalogInvalidated;
    public event EventHandler? InputAuthorityChanged;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _client.StatusChanged -= _statusChangedHandler;
        _client.SessionsUpdated -= _sessionsUpdatedHandler;
        _client.SessionUsageSnapshotUpdated -= _sessionUsageSnapshotUpdatedHandler;
        _client.SessionCommandCompleted -= _sessionCommandCompletedHandler;
        _client.ChatMessageReceived -= _chatMessageReceivedHandler;
        _client.AgentEventReceived -= _agentEventReceivedHandler;
        _client.ModelsListUpdated -= _modelsListUpdatedHandler;
        _client.ModelCatalogInvalidated -= _modelCatalogInvalidatedHandler;
        _client.SelfProfileChanged -= _inputAuthorityChangedHandler;

        StatusChanged = null;
        SessionsUpdated = null;
        SessionUsageSnapshotUpdated = null;
        SessionCommandCompleted = null;
        ChatMessageReceived = null;
        AgentEventReceived = null;
        ModelsListUpdated = null;
        ModelCatalogInvalidated = null;
    }
}
