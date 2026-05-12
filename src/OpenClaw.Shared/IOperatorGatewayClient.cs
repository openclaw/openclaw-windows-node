using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>
/// Read-only facade for the operator gateway client.
/// Exposes data events and request methods needed by UI consumers
/// without exposing connection lifecycle methods (connect/disconnect/dispose).
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
    event EventHandler<JsonElement>? SkillsStatusUpdated;
    event EventHandler<JsonElement>? ConfigUpdated;
    event EventHandler<JsonElement>? ConfigSchemaUpdated;
    event EventHandler<AgentEventInfo>? AgentEventReceived;
    event EventHandler<PairingListInfo>? NodePairListUpdated;
    event EventHandler<DevicePairingListInfo>? DevicePairListUpdated;
    event EventHandler<ModelsListInfo>? ModelsListUpdated;
    event EventHandler<PresenceEntry[]>? PresenceUpdated;
    event EventHandler<JsonElement>? AgentsListUpdated;
    event EventHandler<JsonElement>? AgentFilesListUpdated;
    event EventHandler<JsonElement>? AgentFileContentUpdated;
    event EventHandler<AgentEventInfo>? ChatEventReceived;

    // ─── Query ───
    string? OperatorDeviceId { get; }
    IReadOnlyList<string> GrantedOperatorScopes { get; }
    bool IsConnectedToGateway { get; }

    // ─── Connection events (from WebSocketClientBase) ───
    event EventHandler<ConnectionStatus>? StatusChanged;
    event EventHandler<string>? AuthenticationFailed;
    event EventHandler<DeviceTokenReceivedEventArgs>? DeviceTokenReceived;
    event EventHandler? HandshakeSucceeded;

    // ─── Configuration ───
    void SetUserRules(IReadOnlyList<UserNotificationRule>? rules);
    void SetPreferStructuredCategories(bool value);

    // ─── Request Methods ───
    Task SendChatMessageAsync(string message, string? sessionKey = null);
    Task<ChatSendResult> SendChatMessageForRunAsync(string message, string? sessionKey = null);
    Task CheckHealthAsync();
    Task RequestSessionsAsync(string? agentId = null);
    Task RequestUsageAsync();
    Task RequestNodesAsync();
    Task RequestUsageStatusAsync();
    Task RequestUsageCostAsync(int days = 30);
    Task RequestSessionPreviewAsync(string[] keys, int limit = 12, int maxChars = 240);
    Task<bool> PatchSessionAsync(string key, string? thinkingLevel = null, string? verboseLevel = null);
    Task<bool> ResetSessionAsync(string key);
    Task<bool> DeleteSessionAsync(string key, bool deleteTranscript = true);
    Task<bool> CompactSessionAsync(string key, int maxLines = 400);
    Task RequestCronListAsync();
    Task RequestCronStatusAsync();
    Task<bool> RunCronJobAsync(string jobId, bool force = true);
    Task<bool> RemoveCronJobAsync(string jobId);
    Task<bool> AddCronJobAsync(object jobDefinition);
    Task<bool> UpdateCronJobAsync(string id, object patch);
    Task RequestCronRunsAsync(string? id = null, int limit = 20, int offset = 0);
    Task RequestSkillsStatusAsync(string? agentId = null);
    Task<bool> InstallSkillAsync(string skillId);
    Task<bool> UpdateSkillAsync(string skillId);
    Task RequestConfigAsync();
    Task RequestConfigSchemaAsync();
    Task<bool> SetConfigAsync(string path, object value);
    Task<bool> PatchConfigAsync(JsonElement fullConfig, string? baseHash);
    Task RequestAgentsListAsync();
    Task RequestAgentFilesListAsync(string agentId = "main");
    Task RequestAgentFileGetAsync(string agentId, string name);
    Task RequestModelsListAsync();
    Task RequestNodePairListAsync();
    Task<bool> NodePairApproveAsync(string requestId);
    Task<bool> NodePairRejectAsync(string requestId);
    Task RequestDevicePairListAsync();
    Task<bool> DevicePairApproveAsync(string requestId);
    Task<bool> DevicePairRejectAsync(string requestId);
    Task<bool> StartChannelAsync(string channelName);
    Task<bool> StopChannelAsync(string channelName);
    Task<JsonElement> SendWizardRequestAsync(string method, object? parameters = null, int timeoutMs = 30000);
}
