using OpenClaw.Shared;
#if !OPENCLAW_TRAY_TESTS
using OpenClawTray.Helpers;
#endif

namespace OpenClawTray.Chat;

/// <summary>
/// Stable chat port over successive manager-owned connections. It retains no chat
/// payloads and creates no client. Only the lifetime owner may attach a verified route.
/// </summary>
internal sealed class RetainedChatGatewayBridge : IChatGatewayBridge, IChatQuestionGateway
{
    private readonly object _gate = new();
    private readonly PlacementPort _placement;
    private IChatGatewayBridge? _inner;
    private Action? _unsubscribe;
    private CancellationTokenSource _cancellation = new();
    private long _generation;
    private bool _disposed;

    internal RetainedChatGatewayBridge() => _placement = new(this);
    internal bool IsAttached { get { lock (_gate) return !_disposed && _inner is not null; } }
    internal bool IsBoundTo(IOperatorGatewayClient client) => Read(inner =>
        inner is GatewayClientChatBridge gateway && gateway.IsBoundTo(client), false);

    // Takes ownership of the adapter, never of the connection manager's client.
    internal void Attach(IChatGatewayBridge? next)
    {
        IChatGatewayBridge? previous;
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (ReferenceEquals(_inner, next)) return;
            previous = _inner;
            _unsubscribe?.Invoke();
            _unsubscribe = null;
            _inner = null;
            var generation = ++_generation;
            cancellation = _cancellation;
            _cancellation = new();
            // Cancellation is part of retiring the binding, before observers run
            // or another serialized transport write can use the old authority.
            cancellation.Cancel();
            StatusChanged?.Invoke(this, ConnectionStatus.Disconnected);
            if (next is not null)
            {
                _inner = next;
                void Forward(Action action)
                {
                    lock (_gate)
                        if (!_disposed && generation == _generation && ReferenceEquals(_inner, next)) action();
                }
                EventHandler<ConnectionStatus> status = (_, value) => Forward(() => StatusChanged?.Invoke(this, value));
                EventHandler<SessionInfo[]> sessions = (_, value) => Forward(() => SessionsUpdated?.Invoke(this, value));
                EventHandler<SessionUsageSnapshot> usage = (_, value) => Forward(() => SessionUsageSnapshotUpdated?.Invoke(this, value));
                EventHandler<SessionCommandResult> command = (_, value) => Forward(() => SessionCommandCompleted?.Invoke(this, value));
                EventHandler<ChatMessageInfo> message = (_, value) => Forward(() => ChatMessageReceived?.Invoke(this, value));
                EventHandler<AgentEventInfo> agent = (_, value) => Forward(() => AgentEventReceived?.Invoke(this, value));
                EventHandler<ModelsListInfo> models = (_, value) => Forward(() => ModelsListUpdated?.Invoke(this, value));
                EventHandler catalog = (_, _) => Forward(() => ModelCatalogInvalidated?.Invoke(this, EventArgs.Empty));
                EventHandler<GatewayQuestionEvent> question = (_, value) => Forward(() => QuestionReceived?.Invoke(this, value));
                EventHandler protocol = (_, _) => Forward(() => QuestionProtocolError?.Invoke(this, EventArgs.Empty));
                next.StatusChanged += status;
                next.SessionsUpdated += sessions;
                next.SessionUsageSnapshotUpdated += usage;
                next.SessionCommandCompleted += command;
                next.ChatMessageReceived += message;
                next.AgentEventReceived += agent;
                next.ModelsListUpdated += models;
                next.ModelCatalogInvalidated += catalog;
                var questions = next as IChatQuestionGateway;
                if (questions is not null) { questions.QuestionReceived += question; questions.QuestionProtocolError += protocol; }
                _unsubscribe = () =>
                {
                    next.StatusChanged -= status;
                    next.SessionsUpdated -= sessions;
                    next.SessionUsageSnapshotUpdated -= usage;
                    next.SessionCommandCompleted -= command;
                    next.ChatMessageReceived -= message;
                    next.AgentEventReceived -= agent;
                    next.ModelsListUpdated -= models;
                    next.ModelCatalogInvalidated -= catalog;
                    if (questions is not null) { questions.QuestionReceived -= question; questions.QuestionProtocolError -= protocol; }
                };
                StatusChanged?.Invoke(this, next.CurrentStatus);
                if (next.IsConnected) SessionsUpdated?.Invoke(this, next.GetSessionList());
            }
        }
        cancellation.Dispose();
        previous?.Dispose();
    }

    private T Read<T>(Func<IChatGatewayBridge, T> read, T unavailable)
    {
        lock (_gate) return !_disposed && _inner is { } inner ? read(inner) : unavailable;
    }

    private Task<T> Invoke<T>(Func<IChatGatewayBridge, Task<T>> action) => Invoke((inner, _) => action(inner));
    private async Task<T> Invoke<T>(Func<IChatGatewayBridge, CancellationToken, Task<T>> action)
    {
        Task<T> pending;
        long generation;
        CancellationToken token;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var inner = _inner is { IsConnected: true } current ? current
                : throw new InvalidOperationException(LocalizationHelper.GetString("Chat_Composer_Placeholder_NotConnected"));
            generation = _generation;
            token = _cancellation.Token;
            if (inner is not IChatGatewayRequestScope requestScope) throw new NotSupportedException();
            pending = requestScope.RunWithRequestCancellationAsync(token, () => action(inner, token));
        }
        var result = await pending.WaitAsync(token).ConfigureAwait(false);
        lock (_gate)
            if (_disposed || generation != _generation) throw new OperationCanceledException();
        return result;
    }
    private Task Invoke(Func<IChatGatewayBridge, Task> action) => Invoke(async inner => { await action(inner).ConfigureAwait(false); return true; });
    private Task Invoke(Func<IChatGatewayBridge, CancellationToken, Task> action) => Invoke(async (inner, token) => { await action(inner, token).ConfigureAwait(false); return true; });

    public bool IsConnected => Read(inner => inner.IsConnected, false);
    public ConnectionStatus CurrentStatus => Read(inner => inner.CurrentStatus, ConnectionStatus.Disconnected);
    public string? MainSessionKey => Read(inner => inner.MainSessionKey, null);
    public bool HasHandshakeSnapshot => Read(inner => inner.HasHandshakeSnapshot, false);
    public SessionInfo[] GetSessionList() => Read(inner => inner.GetSessionList(), []);
    public ModelsListInfo? GetCurrentModelsList() => Read(inner => inner.GetCurrentModelsList(), null);
    public bool SupportsSessionScopedModelCatalog => Read(inner => inner.SupportsSessionScopedModelCatalog, false);
    public IChatPlacementGateway PlacementGateway => _placement;
    public void StartProactiveBootstrap() => Read(inner => { inner.StartProactiveBootstrap(); return true; }, false);
    public Task<ModelsListInfo?> RequestSessionModelsListAsync(string key) => Invoke(inner => inner.RequestSessionModelsListAsync(key));
    public Task SendChatMessageAsync(string message, string? key, string? id, IReadOnlyList<ChatAttachment>? attachments = null) => Invoke(inner => inner.SendChatMessageAsync(message, key, id, attachments));
    public Task<ChatSendResult> SendChatMessageForRunAsync(string message, string? key, string? id, IReadOnlyList<ChatAttachment>? attachments = null, string? idempotencyKey = null) => Invoke(inner => inner.SendChatMessageForRunAsync(message, key, id, attachments, idempotencyKey));
    public Task<CommandCatalog> ListCommandsAsync(CommandCatalogQuery? query = null) => Invoke(inner => inner.ListCommandsAsync(query));
    public Task<SessionCreateResult> CreateSessionAsync(SessionCreateRequest request) => Invoke(inner => inner.CreateSessionAsync(request));
    public Task<bool> ResetSessionAsync(string key) => Invoke(inner => inner.ResetSessionAsync(key));
    public Task<SessionResetResult> ResetSessionDetailedAsync(string key) => Invoke(inner => inner.ResetSessionDetailedAsync(key));
    public Task<bool> CompactSessionAsync(string key, int maxLines = 400) => Invoke(inner => inner.CompactSessionAsync(key, maxLines));
    public Task<SessionCompactResult> CompactSessionDetailedAsync(string key) => Invoke(inner => inner.CompactSessionDetailedAsync(key));
    public Task RequestSessionsAsync() => Invoke(inner => inner.RequestSessionsAsync());
    public Task PatchSessionModelAsync(string key, string model) => Invoke(inner => inner.PatchSessionModelAsync(key, model));
    public Task ClearSessionModelAsync(string key) => Invoke(inner => inner.ClearSessionModelAsync(key));
    public Task PatchSessionThinkingLevelAsync(string key, string level) => Invoke(inner => inner.PatchSessionThinkingLevelAsync(key, level));
    public Task ClearSessionThinkingLevelAsync(string key) => Invoke(inner => inner.ClearSessionThinkingLevelAsync(key));
    public Task<ChatHistoryInfo> RequestChatHistoryAsync(string? key) => Invoke(inner => inner.RequestChatHistoryAsync(key));
    public Task<AssistantMediaResolutionResult> ResolveAssistantMediaAsync(string key, ChatMediaContentInfo media, CancellationToken cancellationToken = default) =>
        Invoke(async (inner, token) => { using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken); return await inner.ResolveAssistantMediaAsync(key, media, linked.Token).ConfigureAwait(false); });
    public Task<AssistantFileDownloadStatus> DownloadAssistantFileAsync(string key, ChatMediaContentInfo media, CancellationToken cancellationToken = default) =>
        Invoke(async (inner, token) => { using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken); return await inner.DownloadAssistantFileAsync(key, media, linked.Token).ConfigureAwait(false); });
    public Task SendChatAbortAsync(string runId, string? key = null) => Invoke(inner => inner.SendChatAbortAsync(runId, key));
    public Task ResolveExecApprovalAsync(string id, string decision) => Invoke(inner => inner.ResolveExecApprovalAsync(id, decision));
    public Task<GatewayQuestionList> ListQuestionsAsync() => Invoke(inner => ((IChatQuestionGateway)inner).ListQuestionsAsync());
    public Task<GatewayQuestionRequest> GetQuestionAsync(string id) => Invoke(inner => ((IChatQuestionGateway)inner).GetQuestionAsync(id));
    public Task<string> ResolveQuestionAsync(string id, IReadOnlyDictionary<string, string[]>? answers) => Invoke(inner => ((IChatQuestionGateway)inner).ResolveQuestionAsync(id, answers));

    public event EventHandler<ConnectionStatus>? StatusChanged;
    public event EventHandler<SessionInfo[]>? SessionsUpdated;
    public event EventHandler<SessionUsageSnapshot>? SessionUsageSnapshotUpdated;
    public event EventHandler<SessionCommandResult>? SessionCommandCompleted;
    public event EventHandler<ChatMessageInfo>? ChatMessageReceived;
    public event EventHandler<AgentEventInfo>? AgentEventReceived;
    public event EventHandler<ModelsListInfo>? ModelsListUpdated;
    public event EventHandler? ModelCatalogInvalidated;
    public event EventHandler<GatewayQuestionEvent>? QuestionReceived;
    public event EventHandler? QuestionProtocolError;

    public void Dispose()
    {
        IChatGatewayBridge? previous;
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            _unsubscribe?.Invoke();
            _unsubscribe = null;
            previous = _inner;
            _inner = null;
            cancellation = _cancellation;
            StatusChanged = null;
            SessionsUpdated = null;
            SessionUsageSnapshotUpdated = null;
            SessionCommandCompleted = null;
            ChatMessageReceived = null;
            AgentEventReceived = null;
            ModelsListUpdated = null;
            ModelCatalogInvalidated = null;
            QuestionReceived = null;
            QuestionProtocolError = null;
        }
        cancellation.Cancel();
        cancellation.Dispose();
        previous?.Dispose();
    }

    private sealed class PlacementPort(RetainedChatGatewayBridge owner) : IChatPlacementGateway
    {
        private (long Binding, long Transport)? _last;
        private long _epoch;
        public long? ConnectionEpoch
        {
            get
            {
                lock (owner._gate)
                {
                    var transport = owner.Read(inner => inner.PlacementGateway?.ConnectionEpoch, null);
                    if (transport is null) return null;
                    var current = (owner._generation, transport.Value);
                    if (_last != current) { _last = current; _epoch++; }
                    return _epoch;
                }
            }
        }
        private IChatPlacementGateway Current(IChatGatewayBridge inner, long epoch)
        {
            if (ConnectionEpoch != epoch) throw new OperationCanceledException();
            return inner.PlacementGateway ?? throw new InvalidOperationException(LocalizationHelper.GetString("Chat_Placement_CommandUnavailable"));
        }
        public async Task<ChatPlacementCatalog> LoadAsync(string agentId, CancellationToken cancellationToken)
        {
            var epoch = ConnectionEpoch ?? throw new InvalidOperationException(LocalizationHelper.GetString("Chat_Composer_Placeholder_NotConnected"));
            var catalog = await owner.Invoke(async (inner, token) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
                return await Current(inner, epoch).LoadAsync(agentId, linked.Token).ConfigureAwait(false);
            }).ConfigureAwait(false);
            if (ConnectionEpoch != epoch) throw new OperationCanceledException();
            return catalog with { ConnectionEpoch = epoch };
        }
        public Task<ChatWorkspaceDirectory> BrowseAsync(string? path, long connectionEpoch, CancellationToken cancellationToken) =>
            owner.Invoke(async (inner, token) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
                var target = Current(inner, connectionEpoch);
                return await target.BrowseAsync(path, target.ConnectionEpoch!.Value, linked.Token).ConfigureAwait(false);
            });
        public Task PrepareAsync(string key, string agentId, ChatPlacementSelection selection, long connectionEpoch,
            CancellationToken cancellationToken, Func<CancellationToken, Task>? beforeMutation = null) =>
            owner.Invoke(async (inner, token) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
                var target = Current(inner, connectionEpoch);
                await target.PrepareAsync(key, agentId, selection, target.ConnectionEpoch!.Value, linked.Token, async ct =>
                {
                    if (beforeMutation is not null) await beforeMutation(ct).ConfigureAwait(false);
                    lock (owner._gate) Current(inner, connectionEpoch);
                }).ConfigureAwait(false);
            });
    }
}
