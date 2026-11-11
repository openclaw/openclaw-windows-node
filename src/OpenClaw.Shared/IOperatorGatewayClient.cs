// <summary>
// Operator-facing read facade for an authenticated Gateway connection. UI consumers use it to
// request Gateway data and observe asynchronous updates without receiving lifecycle controls.
// Implementations must preserve the authenticated connection and epoch constraints for mutations.
// Usage:
//   IOperatorGatewayClient gateway = connectionManager.Client;
//   await gateway.RequestSessionsAsync();
//   gateway.SessionsUpdated += (_, sessions) => ShowSessions(sessions);
// </summary>

using System.Text.Json;
using OpenClaw.Shared.Usage;

namespace OpenClaw.Shared;

/// <summary>
/// Defines the Gateway data, request, and event contract exposed to operator-facing consumers.
/// Connection owners retain lifecycle control outside this facade.
/// </summary>
public interface IOperatorGatewayClient
{
    // ─── Data Events ───
    event EventHandler<OpenClawNotification>? NotificationReceived;
    event EventHandler<AgentActivity>? ActivityChanged;
    event EventHandler<ChannelHealth[]>? ChannelHealthUpdated;
    event EventHandler<SessionInfo[]>? SessionsUpdated;
    event EventHandler<GatewayUsageInfo>? UsageUpdated;
    event EventHandler<GatewayUsageStatusInfo>? UsageStatusUpdated;
    event EventHandler<GatewayCostUsageInfo>? UsageCostUpdated;
    event EventHandler<GatewayNodeInfo[]>? NodesUpdated;
    event EventHandler<SessionsPreviewPayloadInfo>? SessionPreviewUpdated;
    event EventHandler<SessionCommandResult>? SessionCommandCompleted;
    event EventHandler<GatewaySelfInfo>? GatewaySelfUpdated;
    event EventHandler<JsonElement>? CronListUpdated;
    event EventHandler<JsonElement>? CronStatusUpdated;
    event EventHandler<JsonElement>? CronRunsUpdated;
    event EventHandler<JsonElement>? ConfigUpdated;
    event EventHandler<JsonElement>? ConfigSchemaUpdated;
    event EventHandler<AgentEventInfo>? AgentEventReceived;
    event EventHandler<PairingListInfo>? NodePairListUpdated;
    event EventHandler<DevicePairingListInfo>? DevicePairListUpdated;
    event EventHandler<ModelsListInfo>? ModelsListUpdated;
    event EventHandler<PresenceEntry[]>? PresenceUpdated;
    /// <summary>The authenticated profile changed; consumers should reload users.self.</summary>
    event EventHandler? SelfProfileChanged { add { } remove { } }
    /// <summary>Raw <c>cron</c> Gateway event payload (job ran, changed, or was removed).</summary>
    event EventHandler<JsonElement>? CronEventReceived { add { } remove { } }
    /// <summary>Machine inventory may have changed (presence, pairing, runner inventory or config).</summary>
    event EventHandler<string>? SystemsInventoryChanged { add { } remove { } }
    /// <summary>A node published new host statistics.</summary>
    event EventHandler<Systems.NodeHostStatsEvent>? NodeHostStatsReceived { add { } remove { } }
    event EventHandler<JsonElement>? AgentsListUpdated;
    event EventHandler<bool>? AgentsListUnsupportedChanged { add { } remove { } }
    event EventHandler<JsonElement>? AgentFilesListUpdated;
    event EventHandler<JsonElement>? AgentFileContentUpdated;
    event EventHandler<AgentEventInfo>? ChatEventReceived;
    /// <summary>A Gateway approval (exec, plugin or system agent) now waits for a decision.</summary>
    event EventHandler<GatewayPendingApproval>? PendingApprovalRequested { add { } remove { } }
    /// <summary>A pending Gateway approval was decided; the argument is its id.</summary>
    event EventHandler<string>? PendingApprovalResolved { add { } remove { } }

    /// <summary>Advertised authenticated RPCs, not local node/MCP capabilities.</summary>
    IReadOnlyList<string> AdvertisedServerMethods => Array.Empty<string>();
    /// <summary>Requests synthesized speech on the captured authenticated connection.</summary>
    Task<GatewaySpeechAudio> SynthesizeSpeechAsync(string text, long connectionEpoch, CancellationToken cancellationToken = default)
        => Task.FromException<GatewaySpeechAudio>(new GatewaySpeechException(GatewaySpeechFailure.Unsupported));

    // ─── Query ───
    string? OperatorDeviceId { get; }
    string? AuthenticatedSigningDeviceId => null;
    IReadOnlyList<string> GrantedOperatorScopes { get; }
    bool IsConnectedToGateway { get; }
    /// <summary>Canonical main session key resolved from hello-ok; <c>null</c> until handshake.</summary>
    string? MainSessionKey { get; }
    /// <summary>True once the hello-ok handshake has been processed.</summary>
    bool HasHandshakeSnapshot { get; }
    long ServerHandshakeGeneration => 0;
    Task<GatewaySearchResult> SearchSessionsAsync(string query, bool messages, CancellationToken cancellationToken) =>
        Task.FromException<GatewaySearchResult>(new NotSupportedException("Session search is unavailable."));
    Task<JsonElement> ReadSearchCatalogAsync(string method, object? parameters, CancellationToken cancellationToken) =>
        Task.FromException<JsonElement>(new NotSupportedException("Search catalogs are unavailable."));
    /// <summary>True only after the current Gateway explicitly rejects agents.list as unsupported.</summary>
    bool IsAgentsListUnsupported => false;
    /// <summary>Authenticated transport epoch, used to bind a mutation across an asynchronous dialog.</summary>
    long? SessionMutationConnectionEpoch => null;

    // ─── Connection events (from WebSocketClientBase) ───
    event EventHandler<ConnectionStatus>? StatusChanged;
    event EventHandler<string>? AuthenticationFailed;
    event EventHandler<DeviceTokenReceivedEventArgs>? DeviceTokenReceived;
    event EventHandler? HandshakeSucceeded;

    // ─── Configuration ───
    /// <summary>Updates the notification rules used to classify later Gateway notifications.</summary>
    void SetUserRules(IReadOnlyList<UserNotificationRule>? rules);
    /// <summary>Chooses whether structured notification metadata takes precedence over keyword classification.</summary>
    void SetPreferStructuredCategories(bool value);

    // ─── Request Methods ───
    /// <summary>Sends a chat message to the selected or current session.</summary>
    Task SendChatMessageAsync(string message, string? sessionKey = null);
    /// <summary>Sends a chat message and returns the Gateway run accepted for it.</summary>
    Task<ChatSendResult> SendChatMessageForRunAsync(string message, string? sessionKey = null);
    /// <summary>
    /// Fetches the normalized conversation transcript for a session
    /// (<c>chat.history</c>). Ships with a default so adding it does not
    /// source-break external implementers (test doubles); the real client
    /// overrides it. Non-overriding clients fail explicitly instead of looking
    /// like an empty transcript.
    /// </summary>
    Task<ChatHistoryInfo> RequestChatHistoryAsync(string? sessionKey = null, int timeoutMs = 15000)
        => Task.FromException<ChatHistoryInfo>(new NotSupportedException("chat.history is not supported by this gateway client."));
    /// <summary>Checks the active connection and reconnects when necessary.</summary>
    Task CheckHealthAsync();
    Task RequestSessionsAsync(string? agentId = null);
    Task RequestUsageAsync();
    Task RequestNodesAsync();
    Task RequestUsageStatusAsync();
    Task RequestUsageCostAsync(int days = 30);
    Task RequestSessionPreviewAsync(string[] keys, int limit = 12, int maxChars = 240);
    Task<bool> PatchSessionAsync(string key, string? model = null, string? thinkingLevel = null, string? verboseLevel = null);
    Task<bool> ResetSessionAsync(string key);
    Task<SessionResetResult> ResetSessionDetailedAsync(string key, int timeoutMs = 15000) =>
        Task.FromResult(new SessionResetResult
        {
            Ok = false,
            Error = "Response-aware sessions.reset is not supported by this gateway client."
        });
    Task<bool> DeleteSessionAsync(string key, bool deleteTranscript = true);
    Task<bool> CompactSessionAsync(string key, int maxLines = 400);
    Task<SessionCompactResult> CompactSessionDetailedAsync(string key, int timeoutMs = 120000) =>
        Task.FromResult(new SessionCompactResult
        {
            Ok = false,
            Error = "Response-aware sessions.compact is not supported by this gateway client."
        });
    Task RequestCronListAsync();
    Task RequestCronStatusAsync();
    Task<bool> RunCronJobAsync(string jobId, bool force = true);
    /// <summary>
    /// Response-aware cron run request. Compatibility fallback for implementers
    /// that only provide <see cref="RunCronJobAsync"/>; the fallback cannot
    /// enforce <paramref name="timeoutMs"/> and implementers should not implement
    /// <see cref="RunCronJobAsync"/> by delegating back to this method.
    /// </summary>
    async Task<CronRunRequestResult> RunCronJobDetailedAsync(string jobId, bool force = true, int timeoutMs = 12000)
    {
        var accepted = await RunCronJobAsync(jobId, force).ConfigureAwait(false);
        return accepted
            ? new CronRunRequestResult(true, true, null)
            : CronRunRequestResult.NotAccepted(null);
    }
    Task<bool> RemoveCronJobAsync(string jobId);
    Task<bool> AddCronJobAsync(object jobDefinition);
    Task<bool> UpdateCronJobAsync(string id, object patch);
    Task RequestCronRunsAsync(string? id = null, int limit = 20, int offset = 0);
    /// <summary>Requests the effective Gateway configuration and raises <see cref="ConfigUpdated"/>.</summary>
    Task RequestConfigAsync();
    Task RequestConfigSchemaAsync();
    Task<bool> SetConfigAsync(string path, object value);
    /// <summary>Applies a full configuration document against an optional Gateway base hash.</summary>
    Task<bool> PatchConfigAsync(JsonElement fullConfig, string? baseHash);
    /// <summary>Response-aware variant of <see cref="PatchConfigAsync"/>: awaits the gateway's reply and returns the real error on failure.</summary>
    Task<ConfigPatchResult> PatchConfigDetailedAsync(JsonElement fullConfig, string? baseHash, int timeoutMs = 15000);
    /// <summary>
    /// <see cref="PatchConfigDetailedAsync"/> on the captured authenticated connection. A changed epoch
    /// throws <see cref="InvalidOperationException"/> before transmission instead of returning a failed result.
    /// </summary>
    Task<ConfigPatchResult> PatchConfigOnConnectionAsync(JsonElement fullConfig, string? baseHash, long connectionEpoch, int timeoutMs = 15000)
        => Task.FromException<ConfigPatchResult>(new NotSupportedException("Connection-bound config.patch is not supported by this gateway client."));
    Task RequestAgentsListAsync();
    Task RequestAgentFilesListAsync(string agentId = "main");
    Task RequestAgentFileGetAsync(string agentId, string name);
    Task RequestModelsListAsync();
    Task RequestNodePairListAsync();
    Task<bool> NodePairApproveAsync(string requestId);
    Task<bool> NodePairRejectAsync(string requestId);
    Task<NodeForgetResult> NodePairRemoveAsync(string nodeId);
    Task<NodeRenameResult> NodeRenameAsync(string nodeId, string displayName);
    Task RequestDevicePairListAsync();
    Task<bool> DevicePairApproveAsync(string requestId);
    Task<bool> DevicePairRejectAsync(string requestId);
    /// <summary>
    /// Fetches the Gateway's effective update track (<c>update.status</c>).
    /// Older Gateway client implementations return no status.
    /// </summary>
    Task<GatewayUpdateStatus?> GetUpdateStatusAsync(int timeoutMs = 5000) =>
        Task.FromResult<GatewayUpdateStatus?>(null);
    Task<JsonElement> SendWizardRequestAsync(string method, object? parameters = null, int timeoutMs = 30000);
    /// <summary>Sends only on the captured authenticated connection; refuses before transmission when the epoch changed.</summary>
    Task<JsonElement> SendWizardRequestOnConnectionAsync(string method, object? parameters, long connectionEpoch, int timeoutMs = 30000)
        => Task.FromException<JsonElement>(new NotSupportedException("Connection-bound requests are not supported by this gateway client."));

    // ─── Gateway protocol APIs ───
    // These ship with default implementations so adding them does not source-break
    // existing external implementers of this interface (e.g. test doubles). The
    // real gateway client overrides them; defaults degrade gracefully to an
    // "unsupported" typed result, matching older-gateway behavior.
    /// <summary>Fetch the gateway command catalog (<c>commands.list</c>); applies <paramref name="query"/> client-side. Returns IsSupported=false on older gateways.</summary>
    Task<CommandCatalog> ListCommandsAsync(CommandCatalogQuery? query = null, int timeoutMs = 15000)
        => Task.FromResult(new CommandCatalog { IsSupported = false });
    /// <summary>Apply an extended <see cref="SessionPatch"/> (rich field set) to a session.</summary>
    Task<bool> PatchSessionAsync(string key, SessionPatch patch)
        => Task.FromResult(false);
    /// <summary>Waits for Gateway acceptance on the captured connection. Does not publish generic session-action notifications.</summary>
    Task PatchSessionConfirmedAsync(string key, SessionPatch patch, long connectionEpoch, int timeoutMs = 15000)
        => Task.FromException(new NotSupportedException("Response-aware sessions.patch is not supported by this gateway client."));
    /// <summary>Waits for Gateway acceptance on the captured connection. The caller owns navigation and list refresh.</summary>
    Task DeleteSessionConfirmedAsync(string key, long connectionEpoch, int timeoutMs = 15000)
        => Task.FromException(new NotSupportedException("Response-aware sessions.delete is not supported by this gateway client."));
    /// <summary>List session files, optionally scoped to a sub-path/search (<c>sessions.files.list</c>).</summary>
    Task<SessionFileList> ListSessionFilesAsync(string key, string? path = null, string? search = null, int timeoutMs = 15000)
        => Task.FromResult(new SessionFileList { Key = key, IsSupported = false });
    /// <summary>Read a session file's content (<c>sessions.files.get</c>).</summary>
    Task<SessionFileContent> GetSessionFileAsync(string key, string path, int timeoutMs = 15000)
        => Task.FromResult(new SessionFileContent { Key = key, Path = path, IsSupported = false });
    /// <summary>List compaction checkpoints for a session (<c>sessions.compaction.list</c>).</summary>
    Task<SessionCompactionCheckpointList> ListCompactionCheckpointsAsync(string key, int timeoutMs = 15000)
        => Task.FromResult(new SessionCompactionCheckpointList { Key = key, IsSupported = false });
    /// <summary>Fetch a single compaction checkpoint's metadata (<c>sessions.compaction.get</c>).</summary>
    Task<SessionCompactionCheckpointResult> GetCompactionCheckpointAsync(string key, string checkpointId, int timeoutMs = 15000)
        => Task.FromResult(new SessionCompactionCheckpointResult { Key = key, IsSupported = false });
    /// <summary>Branch a new session from a compaction checkpoint (<c>sessions.compaction.branch</c>).</summary>
    Task<SessionCompactionMutationResult> BranchCompactionCheckpointAsync(string key, string checkpointId, int timeoutMs = 15000)
        => Task.FromResult(new SessionCompactionMutationResult { Key = key, CheckpointId = checkpointId, IsSupported = false });
    /// <summary>Restore a session to a compaction checkpoint (<c>sessions.compaction.restore</c>).</summary>
    Task<SessionCompactionMutationResult> RestoreCompactionCheckpointAsync(string key, string checkpointId, int timeoutMs = 15000)
        => Task.FromResult(new SessionCompactionMutationResult { Key = key, CheckpointId = checkpointId, IsSupported = false });
    /// <summary>Create a distinct session through <c>sessions.create</c>.</summary>
    Task<SessionCreateResult> CreateSessionAsync(SessionCreateRequest request, int timeoutMs = 15000)
        => Task.FromResult(new SessionCreateResult
        {
            Ok = false,
            IsSupported = false,
            Error = "sessions.create is not supported by this gateway client."
        });
    /// <summary>List only the archived sessions (<c>sessions.list</c> with <c>archived:true</c>). Rows are detached snapshots.</summary>
    Task<SessionListResult> ListArchivedSessionsAsync(int timeoutMs = 15000)
        => Task.FromResult(new SessionListResult { IsSupported = false });

    // ─── Automations (cron) ───
    // Response-aware cron APIs shared by every implementer through SendWizardRequestAsync.
    // Gateway rejections throw InvalidOperationException carrying GatewayRequestErrors codes.
    /// <summary>Job page (<c>cron.list</c>); an omitted agent lists every agent.</summary>
    Task<Cron.CronListPage> ListCronJobsAsync(Cron.CronListQuery query, int timeoutMs = Cron.CronGatewayRequests.DefaultTimeoutMs)
        => Cron.CronGatewayRequests.ListAsync(this, query, timeoutMs);
    /// <summary>Scheduler status (<c>cron.status</c>).</summary>
    Task<Cron.CronSchedulerStatus> GetCronStatusAsync(int timeoutMs = Cron.CronGatewayRequests.DefaultTimeoutMs)
        => Cron.CronGatewayRequests.StatusAsync(this, timeoutMs);
    /// <summary>Exact job definition (<c>cron.get</c>).</summary>
    Task<Cron.CronJob> GetCronJobAsync(string id, int timeoutMs = Cron.CronGatewayRequests.DefaultTimeoutMs)
        => Cron.CronGatewayRequests.GetAsync(this, id, timeoutMs);
    /// <summary>Create a job (<c>cron.add</c>) and return the stored definition.</summary>
    Task<Cron.CronJob> AddCronJobDetailedAsync(System.Text.Json.Nodes.JsonObject definition, int timeoutMs = Cron.CronGatewayRequests.DefaultTimeoutMs)
        => Cron.CronGatewayRequests.AddAsync(this, definition, timeoutMs);
    /// <summary>Patch a job (<c>cron.update</c>) guarded by its <c>configRevision</c>.</summary>
    Task<Cron.CronJob> UpdateCronJobDetailedAsync(string id, string? expectedConfigRevision, System.Text.Json.Nodes.JsonObject patch, int timeoutMs = Cron.CronGatewayRequests.DefaultTimeoutMs)
        => Cron.CronGatewayRequests.UpdateAsync(this, id, expectedConfigRevision, patch, timeoutMs);
    /// <summary>Remove a job (<c>cron.remove</c>); a loaded revision is confirmed with <c>cron.get</c> first.</summary>
    Task RemoveCronJobDetailedAsync(string id, string? expectedConfigRevision = null, int timeoutMs = Cron.CronGatewayRequests.DefaultTimeoutMs)
        => Cron.CronGatewayRequests.RemoveAsync(this, id, expectedConfigRevision, timeoutMs);
    /// <summary>Run history page (<c>cron.runs</c>).</summary>
    Task<Cron.CronRunsPage> ListCronRunsAsync(Cron.CronRunsQuery query, int timeoutMs = Cron.CronGatewayRequests.DefaultTimeoutMs)
        => Cron.CronGatewayRequests.RunsAsync(this, query, timeoutMs);

    // ─── Usage analytics (Usage page) ───
    // Response-aware reads; defaults report Unsupported so test doubles keep compiling.
    /// <summary>Per-session usage report (<c>sessions.usage</c>).</summary>
    Task<UsageReadResult<SessionsUsageReport>> GetSessionsUsageAsync(
        SessionsUsageQuery query, int timeoutMs = 30000, CancellationToken cancellationToken = default)
        => Task.FromResult(UsageReadResult<SessionsUsageReport>.Unsupported());
    /// <summary>Provider plans, quotas and billing (<c>usage.status</c>) as a typed response.</summary>
    Task<UsageReadResult<ProviderUsageSummary>> GetProviderUsageAsync(
        int timeoutMs = 30000, CancellationToken cancellationToken = default)
        => Task.FromResult(UsageReadResult<ProviderUsageSummary>.Unsupported());
    /// <summary>Fetches the authenticated user's avatar from the Gateway route. Older clients report Unsupported.</summary>
    Task<UserAvatarResult> ResolveUserAvatarAsync(string profileId, string? revision, CancellationToken cancellationToken = default)
        => Task.FromResult(UserAvatarResult.Of(UserAvatarStatus.Unsupported));
}

public sealed record CronRunRequestResult(
    bool Accepted,
    bool Enqueued,
    string? RunId,
    string? Reason = null,
    string? Error = null)
{
    public static CronRunRequestResult NotAccepted(string? error) =>
        new(false, false, null, null, error);
}
