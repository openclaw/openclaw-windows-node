using OpenClaw.Shared;
using OpenClawTray.Services;
using System.Text.Json;

namespace OpenClaw.Tray.Tests;

public sealed class OnboardingChatBootstrapperTests : IDisposable
{
    private readonly string _settingsDir;

    public OnboardingChatBootstrapperTests()
    {
        _settingsDir = Path.Combine(Directory.GetCurrentDirectory(), "test-artifacts", "bootstrapper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_settingsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_settingsDir, true); } catch { }
    }

    [Fact]
    public async Task BootstrapAsync_SendsOnceAndConsumesGate_OnCompletedRun()
    {
        var settings = new SettingsManager(_settingsDir);
        var client = new FakeOperatorGatewayClient { Result = new ChatSendResult { RunId = "run-1", SessionKey = "agent:main:main" } };

        var task = OnboardingChatBootstrapper.BootstrapAsync(client, settings, TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        client.RaiseFinalAssistant("run-1");
        var result = await task;

        Assert.True(result);
        Assert.Equal(1, client.SendCount);
        Assert.Equal(OnboardingChatBootstrapper.Message, client.LastMessage);
        Assert.True(settings.HasInjectedFirstRunBootstrap);
    }

    [Fact]
    public async Task BootstrapAsync_ConsumesGate_WhenCompletionArrivesSynchronouslyDuringSend()
    {
        var settings = new SettingsManager(_settingsDir);
        var client = new FakeOperatorGatewayClient
        {
            Result = new ChatSendResult { RunId = "run-sync", SessionKey = "agent:main:main" },
            FinalRunIdRaisedDuringSend = "run-sync"
        };

        var result = await OnboardingChatBootstrapper.BootstrapAsync(client, settings, TimeSpan.FromMilliseconds(25));

        Assert.True(result);
        Assert.Equal(1, client.SendCount);
        Assert.Equal(OnboardingChatBootstrapper.Message, client.LastMessage);
        Assert.True(settings.HasInjectedFirstRunBootstrap);
    }

    [Fact]
    public async Task BootstrapAsync_DoesNotConsumeGate_WhenSendFails()
    {
        var settings = new SettingsManager(_settingsDir);
        var client = new FakeOperatorGatewayClient { SendException = new InvalidOperationException("rejected") };

        var result = await OnboardingChatBootstrapper.BootstrapAsync(client, settings, TimeSpan.FromMilliseconds(10));

        Assert.False(result);
        Assert.Equal(1, client.SendCount);
        Assert.False(settings.HasInjectedFirstRunBootstrap);
    }

    [Fact]
    public async Task BootstrapAsync_DoesNotConsumeGate_WhenCompletionTimesOut()
    {
        var settings = new SettingsManager(_settingsDir);
        var client = new FakeOperatorGatewayClient { Result = new ChatSendResult { RunId = "run-timeout" } };

        var result = await OnboardingChatBootstrapper.BootstrapAsync(client, settings, TimeSpan.FromMilliseconds(25));

        Assert.False(result);
        Assert.Equal(1, client.SendCount);
        Assert.False(settings.HasInjectedFirstRunBootstrap);
    }

#pragma warning disable CS0067
    private sealed class FakeOperatorGatewayClient : IOperatorGatewayClient
    {
        public int SendCount { get; private set; }
        public string? LastMessage { get; private set; }
        public Exception? SendException { get; init; }
        public ChatSendResult Result { get; init; } = new();
        public string? FinalRunIdRaisedDuringSend { get; init; }
        public bool IsConnectedToGateway { get; init; } = true;
        public string? OperatorDeviceId => "operator";
        public IReadOnlyList<string> GrantedOperatorScopes => Array.Empty<string>();

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

        public Task SendChatMessageAsync(string message, string? sessionKey = null) => SendChatMessageForRunAsync(message, sessionKey);

        public Task<ChatSendResult> SendChatMessageForRunAsync(string message, string? sessionKey = null)
        {
            SendCount++;
            LastMessage = message;
            if (SendException != null) throw SendException;
            if (!string.IsNullOrWhiteSpace(FinalRunIdRaisedDuringSend))
                RaiseFinalAssistant(FinalRunIdRaisedDuringSend);
            return Task.FromResult(Result);
        }

        public void RaiseFinalAssistant(string runId)
        {
            using var doc = JsonDocument.Parse("""{"state":"final"}""");
            ChatEventReceived?.Invoke(this, new AgentEventInfo
            {
                RunId = runId,
                Stream = "assistant",
                Data = doc.RootElement.Clone()
            });
        }

        public void SetUserRules(IReadOnlyList<UserNotificationRule>? rules) { }
        public void SetPreferStructuredCategories(bool value) { }
        public Task CheckHealthAsync() => Task.CompletedTask;
        public Task RequestSessionsAsync(string? agentId = null) => Task.CompletedTask;
        public Task RequestUsageAsync() => Task.CompletedTask;
        public Task RequestNodesAsync() => Task.CompletedTask;
        public Task RequestUsageStatusAsync() => Task.CompletedTask;
        public Task RequestUsageCostAsync(int days = 30) => Task.CompletedTask;
        public Task RequestSessionPreviewAsync(string[] keys, int limit = 12, int maxChars = 240) => Task.CompletedTask;
        public Task<bool> PatchSessionAsync(string key, string? thinkingLevel = null, string? verboseLevel = null) => Task.FromResult(false);
        public Task<bool> ResetSessionAsync(string key) => Task.FromResult(false);
        public Task<bool> DeleteSessionAsync(string key, bool deleteTranscript = true) => Task.FromResult(false);
        public Task<bool> CompactSessionAsync(string key, int maxLines = 400) => Task.FromResult(false);
        public Task RequestCronListAsync() => Task.CompletedTask;
        public Task RequestCronStatusAsync() => Task.CompletedTask;
        public Task<bool> RunCronJobAsync(string jobId, bool force = true) => Task.FromResult(false);
        public Task<bool> RemoveCronJobAsync(string jobId) => Task.FromResult(false);
        // Stubbed for interface compliance — not exercised by these tests.
        public Task<bool> AddCronJobAsync(object jobDefinition) => Task.FromResult(false);
        public Task<bool> UpdateCronJobAsync(string id, object patch) => Task.FromResult(false);
        public Task RequestCronRunsAsync(string? id = null, int limit = 20, int offset = 0) => Task.CompletedTask;
        public Task RequestSkillsStatusAsync(string? agentId = null) => Task.CompletedTask;
        public Task<bool> InstallSkillAsync(string skillId) => Task.FromResult(false);
        public Task<bool> UpdateSkillAsync(string skillId) => Task.FromResult(false);
        public Task RequestConfigAsync() => Task.CompletedTask;
        public Task RequestConfigSchemaAsync() => Task.CompletedTask;
        public Task<bool> SetConfigAsync(string path, object value) => Task.FromResult(false);
        public Task<bool> PatchConfigAsync(JsonElement fullConfig, string? baseHash) => Task.FromResult(false);
        public Task RequestAgentsListAsync() => Task.CompletedTask;
        public Task RequestAgentFilesListAsync(string agentId = "main") => Task.CompletedTask;
        public Task RequestAgentFileGetAsync(string agentId, string name) => Task.CompletedTask;
        public Task RequestModelsListAsync() => Task.CompletedTask;
        public Task RequestNodePairListAsync() => Task.CompletedTask;
        public Task<bool> NodePairApproveAsync(string requestId) => Task.FromResult(false);
        public Task<bool> NodePairRejectAsync(string requestId) => Task.FromResult(false);
        public Task RequestDevicePairListAsync() => Task.CompletedTask;
        public Task<bool> DevicePairApproveAsync(string requestId) => Task.FromResult(false);
        public Task<bool> DevicePairRejectAsync(string requestId) => Task.FromResult(false);
        public Task<bool> StartChannelAsync(string channelName) => Task.FromResult(false);
        public Task<bool> StopChannelAsync(string channelName) => Task.FromResult(false);
        public Task<JsonElement> SendWizardRequestAsync(string method, object? parameters = null, int timeoutMs = 30000) => Task.FromResult(default(JsonElement));
    }
#pragma warning restore CS0067
}
