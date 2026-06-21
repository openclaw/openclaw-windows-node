using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class PairingApprovalCoordinatorTests
{
    [Fact]
    public async Task ApproveAsync_WhenClientChangesBeforeAck_DoesNotMarkSubmitted()
    {
        var originalClient = new FakeOperatorGatewayClient();
        var replacementClient = new FakeOperatorGatewayClient();
        var approveGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        originalClient.DeviceApproveResult = approveGate.Task;
        IOperatorGatewayClient? currentClient = originalClient;
        var coordinator = NewCoordinator(() => currentClient);

        coordinator.OnPairListsUpdated(Devices(Device("req-1")), null);
        var approval = Assert.Single(coordinator.Current);

        var approveTask = coordinator.ApproveAsync(approval.Key);
        Assert.Equal(1, originalClient.DeviceApproveCalls);

        currentClient = replacementClient;
        approveGate.SetResult(true);
        var result = await approveTask;

        Assert.False(result);
        Assert.Single(coordinator.Current);
        Assert.Equal(0, originalClient.DeviceListRequests);
        Assert.Equal(0, replacementClient.DeviceListRequests);
    }

    [Fact]
    public async Task ApproveAsync_WhenSameClientAcks_RecordsSubmissionAndRefreshesList()
    {
        var client = new FakeOperatorGatewayClient();
        var coordinator = NewCoordinator(() => client);

        coordinator.OnPairListsUpdated(Devices(Device("req-1")), null);
        var approval = Assert.Single(coordinator.Current);

        var result = await coordinator.ApproveAsync(approval.Key);

        Assert.True(result);
        Assert.Empty(coordinator.Current);
        Assert.Equal(1, client.DeviceApproveCalls);
        Assert.Equal(1, client.DeviceListRequests);
    }

    private static PairingApprovalCoordinator NewCoordinator(Func<IOperatorGatewayClient?> getClient) =>
        new(
            getClient,
            getOwnNodeIds: () => Array.Empty<string>(),
            isPromptEnabled: () => true,
            logger: NullLogger.Instance);

    private static DevicePairingListInfo Devices(params DevicePairingRequest[] requests) =>
        new() { Pending = requests.ToList() };

    private static DevicePairingRequest Device(string requestId) =>
        new()
        {
            RequestId = requestId,
            DeviceId = $"device-{requestId}",
            DisplayName = requestId,
            Scopes = ["operator.read"],
        };

#pragma warning disable CS0067
    private sealed class FakeOperatorGatewayClient : IOperatorGatewayClient
    {
        public Task<bool> DeviceApproveResult { get; set; } = Task.FromResult(true);
        public int DeviceApproveCalls { get; private set; }
        public int DeviceListRequests { get; private set; }
        public string? OperatorDeviceId => "operator";
        public IReadOnlyList<string> GrantedOperatorScopes { get; set; } = ["operator.admin"];
        public bool IsConnectedToGateway { get; set; } = true;
        public string? MainSessionKey => "main";
        public bool HasHandshakeSnapshot => true;

        public event EventHandler<OpenClawNotification>? NotificationReceived;
        public event EventHandler<AgentActivity>? ActivityChanged;
        public event EventHandler<ChannelHealth[]>? ChannelHealthUpdated;
        public event EventHandler<SessionInfo[]>? SessionsUpdated;
        public event EventHandler<GatewayUsageInfo>? UsageUpdated;
        public event EventHandler<GatewayUsageStatusInfo>? UsageStatusUpdated;
        public event EventHandler<GatewayCostUsageInfo>? UsageCostUpdated;
        public event EventHandler<GatewayNodeInfo[]>? NodesUpdated;
        public event EventHandler<SessionsPreviewPayloadInfo>? SessionPreviewUpdated;
        public event EventHandler<SessionCommandResult>? SessionCommandCompleted;
        public event EventHandler<GatewaySelfInfo>? GatewaySelfUpdated;
        public event EventHandler<JsonElement>? CronListUpdated;
        public event EventHandler<JsonElement>? CronStatusUpdated;
        public event EventHandler<JsonElement>? CronRunsUpdated;
        public event EventHandler<JsonElement>? SkillsStatusUpdated;
        public event EventHandler<JsonElement>? ConfigUpdated;
        public event EventHandler<JsonElement>? ConfigSchemaUpdated;
        public event EventHandler<AgentEventInfo>? AgentEventReceived;
        public event EventHandler<PairingListInfo>? NodePairListUpdated;
        public event EventHandler<DevicePairingListInfo>? DevicePairListUpdated;
        public event EventHandler<ModelsListInfo>? ModelsListUpdated;
        public event EventHandler<PresenceEntry[]>? PresenceUpdated;
        public event EventHandler<JsonElement>? AgentsListUpdated;
        public event EventHandler<JsonElement>? AgentFilesListUpdated;
        public event EventHandler<JsonElement>? AgentFileContentUpdated;
        public event EventHandler<AgentEventInfo>? ChatEventReceived;
        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<string>? AuthenticationFailed;
        public event EventHandler<DeviceTokenReceivedEventArgs>? DeviceTokenReceived;
        public event EventHandler? HandshakeSucceeded;

        public void SetUserRules(IReadOnlyList<UserNotificationRule>? rules) { }
        public void SetPreferStructuredCategories(bool value) { }
        public Task SendChatMessageAsync(string message, string? sessionKey = null) => Task.CompletedTask;
        public Task<ChatSendResult> SendChatMessageForRunAsync(string message, string? sessionKey = null) => Task.FromResult(new ChatSendResult());
        public Task CheckHealthAsync() => Task.CompletedTask;
        public Task RequestSessionsAsync(string? agentId = null) => Task.CompletedTask;
        public Task RequestUsageAsync() => Task.CompletedTask;
        public Task RequestNodesAsync() => Task.CompletedTask;
        public Task RequestUsageStatusAsync() => Task.CompletedTask;
        public Task RequestUsageCostAsync(int days = 30) => Task.CompletedTask;
        public Task RequestSessionPreviewAsync(string[] keys, int limit = 12, int maxChars = 240) => Task.CompletedTask;
        public Task<bool> PatchSessionAsync(string key, string? model = null, string? thinkingLevel = null, string? verboseLevel = null) => Task.FromResult(false);
        public Task<bool> ResetSessionAsync(string key) => Task.FromResult(false);
        public Task<bool> DeleteSessionAsync(string key, bool deleteTranscript = true) => Task.FromResult(false);
        public Task<bool> CompactSessionAsync(string key, int maxLines = 400) => Task.FromResult(false);
        public Task RequestCronListAsync() => Task.CompletedTask;
        public Task RequestCronStatusAsync() => Task.CompletedTask;
        public Task<bool> RunCronJobAsync(string jobId, bool force = true) => Task.FromResult(false);
        public Task<bool> RemoveCronJobAsync(string jobId) => Task.FromResult(false);
        public Task<bool> AddCronJobAsync(object jobDefinition) => Task.FromResult(false);
        public Task<bool> UpdateCronJobAsync(string id, object patch) => Task.FromResult(false);
        public Task RequestCronRunsAsync(string? id = null, int limit = 20, int offset = 0) => Task.CompletedTask;
        public Task RequestSkillsStatusAsync(string? agentId = null) => Task.CompletedTask;
        public Task<bool> InstallSkillAsync(string skillId) => Task.FromResult(false);
        public Task<bool> SetSkillEnabledAsync(string skillKey, bool enabled) => Task.FromResult(false);
        public Task RequestConfigAsync() => Task.CompletedTask;
        public Task RequestConfigSchemaAsync() => Task.CompletedTask;
        public Task<bool> SetConfigAsync(string path, object value) => Task.FromResult(false);
        public Task<bool> PatchConfigAsync(JsonElement fullConfig, string? baseHash) => Task.FromResult(false);
        public Task<ConfigPatchResult> PatchConfigDetailedAsync(JsonElement fullConfig, string? baseHash, int timeoutMs = 15000) =>
            Task.FromResult(new ConfigPatchResult { Ok = false, Error = "stub" });
        public Task RequestAgentsListAsync() => Task.CompletedTask;
        public Task RequestAgentFilesListAsync(string agentId = "main") => Task.CompletedTask;
        public Task RequestAgentFileGetAsync(string agentId, string name) => Task.CompletedTask;
        public Task RequestModelsListAsync() => Task.CompletedTask;
        public Task RequestNodePairListAsync() => Task.CompletedTask;
        public Task<bool> NodePairApproveAsync(string requestId) => Task.FromResult(false);
        public Task<bool> NodePairRejectAsync(string requestId) => Task.FromResult(false);
        public Task<NodeForgetResult> NodePairRemoveAsync(string nodeId) => Task.FromResult(new NodeForgetResult(false, "stub"));
        public Task<NodeRenameResult> NodeRenameAsync(string nodeId, string displayName) => Task.FromResult(new NodeRenameResult(false, ErrorMessage: "stub"));
        public Task RequestDevicePairListAsync()
        {
            DeviceListRequests++;
            return Task.CompletedTask;
        }

        public Task<bool> DevicePairApproveAsync(string requestId)
        {
            DeviceApproveCalls++;
            return DeviceApproveResult;
        }

        public Task<bool> DevicePairRejectAsync(string requestId) => Task.FromResult(false);
        public Task<bool> StartChannelAsync(string channelName) => Task.FromResult(false);
        public Task<ChannelStartResult?> StartChannelDetailedAsync(string channelName, int timeoutMs = 12000) => Task.FromResult<ChannelStartResult?>(null);
        public Task<bool> StopChannelAsync(string channelName) => Task.FromResult(false);
        public Task<ChannelsStatusSnapshot?> GetChannelsStatusAsync(bool probe = false, int timeoutMs = 12000) => Task.FromResult<ChannelsStatusSnapshot?>(null);
        public Task<bool> LogoutChannelAsync(string channelName, int timeoutMs = 12000) => Task.FromResult(false);
        public Task<WebLoginStartResult?> WebLoginStartAsync(bool force = false, int timeoutMs = 30000) => Task.FromResult<WebLoginStartResult?>(null);
        public Task<WebLoginWaitResult?> WebLoginWaitAsync(string? currentQrDataUrl = null, int timeoutMs = 30000) => Task.FromResult<WebLoginWaitResult?>(null);
        public Task<JsonElement> SendWizardRequestAsync(string method, object? parameters = null, int timeoutMs = 30000) => Task.FromResult(default(JsonElement));
    }
#pragma warning restore CS0067
}
