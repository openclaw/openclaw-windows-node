using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class GatewayFileAccessPolicySyncTests
{
    public class GatewayProxy : DispatchProxy
    {
        public long Epoch = 7;
        public string[] Scopes = ["operator.admin"];
        public List<string> Calls = [];
        public JsonElement? Patch;
        public Action? OnConfigGet;
        public string ConfigJson = """{"config":{},"hash":"h1"}""";
        public string NodeListJson = """{"nodes":[]}""";
        public Queue<ConfigPatchResult> PatchResults = new();
        // A write that times out or returns UNAVAILABLE after persisting is still saved.
        public bool PersistFailedWrites;
        // A concurrent writer replaced the config, so an acknowledged write never appears in it.
        public bool LoseWrites;
        public Exception? ConfigGetError;
        public readonly TaskCompletionSource Refreshed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Saved/applied revisions reported by config.get; null omits them (older Gateways).
        public string? SavedRevision;
        public string? AppliedRevision;
        public int ApplyAfterConfigGets = int.MaxValue;
        private int _configGets;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "get_SessionMutationConnectionEpoch":
                    return (long?)Epoch;
                case "get_IsConnectedToGateway":
                    return true;
                case "get_GrantedOperatorScopes":
                    return (IReadOnlyList<string>)Scopes;
                case "SendWizardRequestOnConnectionAsync":
                    var method = (string)args![0]!;
                    Calls.Add(method);
                    if (method == "node.list")
                        return Task.FromResult(Parse(NodeListJson));
                    OnConfigGet?.Invoke();
                    if (ConfigGetError is not null)
                        return Task.FromException<JsonElement>(ConfigGetError);
                    var envelope = JsonNode.Parse(ConfigJson)!.AsObject();
                    if (SavedRevision is not null)
                    {
                        envelope["configRevisionHash"] = SavedRevision;
                        envelope["appliedConfigHash"] = ++_configGets >= ApplyAfterConfigGets ? SavedRevision : AppliedRevision;
                    }
                    return Task.FromResult(Parse(envelope.ToJsonString()));
                case "PatchConfigOnConnectionAsync":
                    Calls.Add("config.patch@" + (long)args![2]!);
                    Patch = ((JsonElement)args[0]!).Clone();
                    var result = PatchResults.Count > 0 ? PatchResults.Dequeue() : new ConfigPatchResult { Ok = true };
                    if (!LoseWrites && (result.Ok || PersistFailedWrites && !result.LooksLikeStaleBaseHash))
                        ConfigJson = "{\"config\":" + Patch.Value.GetRawText() + ",\"hash\":\"h2\"}";
                    return Task.FromResult(result);
                case "RequestConfigAsync":
                    Calls.Add("config.refresh");
                    Refreshed.TrySetResult();
                    return Task.CompletedTask;
                default:
                    throw new NotSupportedException(targetMethod?.Name);
            }
        }

        private static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }

    private sealed class Fixture
    {
        public IOperatorGatewayClient Client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
        public GatewayProxy Proxy => (GatewayProxy)Client;
        public GatewayConnectionSnapshot Snapshot = new()
        {
            OperatorState = RoleConnectionState.Connected,
            NodeState = RoleConnectionState.Connected,
            NodePairingStatus = PairingStatus.Paired,
            NodeDeviceId = "node1",
            GatewayId = "gw-a",
        };
        public bool? EnforcesSandbox = true;
        public Action? OnDelay;
        public readonly GatewayFileAccessPolicySync Sync;

        public Fixture() => Sync = new(() => Client, () => Snapshot, () => EnforcesSandbox, new NullLogger(),
            _ => { OnDelay?.Invoke(); return Task.CompletedTask; });
    }

    private static JsonElement NodeEntry(GatewayProxy proxy) =>
        proxy.Patch!.Value.GetProperty("plugins").GetProperty("entries").GetProperty("file-transfer")
            .GetProperty("config").GetProperty("nodes").GetProperty("node1");

    [Fact]
    public async Task Sync_AllowsEveryPathOnceOnTheBoundConnection()
    {
        var fixture = new Fixture();

        await fixture.Sync.RequestSync();
        await fixture.Sync.RequestSync();

        Assert.Equal(["config.get", "config.patch@7", "config.get", "config.refresh"], fixture.Proxy.Calls);
        Assert.Equal(GatewayFileAccessSyncStatus.Synced, fixture.Sync.Status);
        Assert.Equal("""["**"]""", NodeEntry(fixture.Proxy).GetProperty("allowReadPaths").GetRawText());
        Assert.Equal("""["**"]""", NodeEntry(fixture.Proxy).GetProperty("allowWritePaths").GetRawText());
    }

    [Fact]
    public async Task Sync_ReplacementClientWithTheSameEpochRereadsConfig()
    {
        var fixture = new Fixture();
        await fixture.Sync.RequestSync();

        fixture.Client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
        await fixture.Sync.RequestSync();

        Assert.Equal(["config.get", "config.patch@7", "config.get", "config.refresh"], fixture.Proxy.Calls);
    }

    [Fact]
    public async Task Sync_TimedOutPatchIsReadBackInsteadOfReplayed()
    {
        var fixture = new Fixture();
        fixture.Proxy.PersistFailedWrites = true;
        fixture.Proxy.PatchResults.Enqueue(new ConfigPatchResult { Ok = false, TimedOut = true });

        await fixture.Sync.RequestSync();

        Assert.Equal(["config.get", "config.patch@7", "config.get"], fixture.Proxy.Calls);
        Assert.Equal(GatewayFileAccessSyncStatus.Synced, fixture.Sync.Status);
    }

    [Fact]
    public async Task Sync_RejectedPatchIsRetriedOnlyOnTheNextConnection()
    {
        var fixture = new Fixture();
        fixture.Proxy.PatchResults.Enqueue(new ConfigPatchResult { Ok = false });

        await fixture.Sync.RequestSync();
        await fixture.Sync.RequestSync();
        Assert.Equal(["config.get", "config.patch@7", "config.get"], fixture.Proxy.Calls);
        Assert.Equal(GatewayFileAccessSyncStatus.Failed, fixture.Sync.Status);

        fixture.Proxy.Epoch++;
        await fixture.Sync.RequestSync();
        Assert.Equal(GatewayFileAccessSyncStatus.Synced, fixture.Sync.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Sync_WithoutSandboxEnforcementKeepsTheGatewayPolicy(bool? enforces)
    {
        // Uncontained compatibility execution has no Sandbox to stand behind an allow-all entry.
        var fixture = new Fixture { EnforcesSandbox = enforces };

        await fixture.Sync.RequestSync();

        Assert.Empty(fixture.Proxy.Calls);
        Assert.Equal(GatewayFileAccessSyncStatus.NotApplicable, fixture.Sync.Status);
    }

    [Fact]
    public async Task Sync_SavedButUnappliedPolicyIsReportedPending()
    {
        var fixture = new Fixture();
        // A Gateway whose plugin reload is held by unsettled work persists the write but keeps the old generation.
        fixture.Proxy.PersistFailedWrites = true;
        fixture.Proxy.SavedRevision = "r2";
        fixture.Proxy.AppliedRevision = "r1";
        fixture.Proxy.PatchResults.Enqueue(new ConfigPatchResult { Ok = false });

        await fixture.Sync.RequestSync();

        Assert.Equal(GatewayFileAccessSyncStatus.PendingApplication, fixture.Sync.Status);
        Assert.Single(fixture.Proxy.Calls, call => call.StartsWith("config.patch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sync_WaitsForTheGatewayToApplyTheSavedPolicy()
    {
        var fixture = new Fixture();
        fixture.Proxy.SavedRevision = "r2";
        fixture.Proxy.AppliedRevision = "r1";
        fixture.Proxy.ApplyAfterConfigGets = 3;

        await fixture.Sync.RequestSync();

        Assert.Equal(["config.get", "config.patch@7", "config.get", "config.get", "config.refresh"], fixture.Proxy.Calls);
        Assert.Equal(GatewayFileAccessSyncStatus.Synced, fixture.Sync.Status);
    }

    [Fact]
    public async Task Sync_NewEntryInheritsTheGatewaysDisplayNamePolicy()
    {
        var fixture = new Fixture();
        fixture.Proxy.ConfigJson = """{"config":{"plugins":{"entries":{"file-transfer":{"config":{"policyVersion":2,"nodes":{"Win PC":{"ask":"always"}}}}}}},"hash":"h1"}""";
        fixture.Proxy.NodeListJson = """{"nodes":[{"nodeId":"node1","displayName":"Win PC"}]}""";

        await fixture.Sync.RequestSync();

        Assert.Equal(["config.get", "node.list", "config.patch@7", "config.get", "config.refresh"], fixture.Proxy.Calls);
        Assert.Equal("always", NodeEntry(fixture.Proxy).GetProperty("ask").GetString());
    }

    [Fact]
    public async Task Sync_WithoutAdminScopeReportsItWithoutCallingTheGateway()
    {
        var fixture = new Fixture();
        fixture.Proxy.Scopes = ["operator.read", "operator.write"];

        await fixture.Sync.RequestSync();

        Assert.Empty(fixture.Proxy.Calls);
        Assert.Equal(GatewayFileAccessSyncStatus.NeedsConfigAccess, fixture.Sync.Status);
    }

    [Fact]
    public async Task Sync_ReconnectDuringReadNeverWritesToTheNewConnection()
    {
        var fixture = new Fixture();
        fixture.Proxy.OnConfigGet = () => fixture.Proxy.Epoch++;

        await fixture.Sync.RequestSync();

        Assert.Equal(["config.get"], fixture.Proxy.Calls);
        Assert.Equal(GatewayFileAccessSyncStatus.NotApplicable, fixture.Sync.Status);
    }

    [Fact]
    public async Task Sync_RetriesAStaleHashOnceThenReportsFailure()
    {
        var fixture = new Fixture();
        fixture.Proxy.PatchResults.Enqueue(new ConfigPatchResult { Ok = false, Error = "baseHash mismatch" });
        fixture.Proxy.PatchResults.Enqueue(new ConfigPatchResult { Ok = false, Error = "baseHash mismatch" });

        await fixture.Sync.RequestSync();

        Assert.Equal(["config.get", "config.patch@7", "config.get", "config.patch@7"], fixture.Proxy.Calls);
        Assert.Equal(GatewayFileAccessSyncStatus.Failed, fixture.Sync.Status);
    }

    [Fact]
    public async Task Sync_RetriesWhenTheSandboxStateBecomesKnownWithoutAConnectionChange()
    {
        // A failed probe is retried by NodeService, but nothing tells the sync when the retry succeeds.
        var fixture = new Fixture { EnforcesSandbox = null };
        fixture.OnDelay = () => fixture.EnforcesSandbox = true;

        await fixture.Sync.RequestSync();
        // The requeued pass ends by refreshing config; a following request joins or reads its settled outcome.
        await fixture.Proxy.Refreshed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Sync.RequestSync();

        Assert.Equal(GatewayFileAccessSyncStatus.Synced, fixture.Sync.Status);
        Assert.Single(fixture.Proxy.Calls, call => call.StartsWith("config.patch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sync_DoesNothingUntilThePairedNodeIsConnected()
    {
        var fixture = new Fixture();
        fixture.Snapshot = fixture.Snapshot with { NodeState = RoleConnectionState.Connecting };

        await fixture.Sync.RequestSync();

        Assert.Empty(fixture.Proxy.Calls);
    }

    [Fact]
    public async Task Sync_RequestErrorOnTheCurrentConnectionIsNotRepeatedByLaterStateChanges()
    {
        var fixture = new Fixture();
        fixture.Proxy.ConfigGetError = new TimeoutException();

        await fixture.Sync.RequestSync();
        await fixture.Sync.RequestSync();

        Assert.Equal(["config.get"], fixture.Proxy.Calls);
        Assert.Equal(GatewayFileAccessSyncStatus.Failed, fixture.Sync.Status);
    }

    [Fact]
    public async Task Sync_AppliedConfigWithoutThisNodesEntryIsNotReportedSynced()
    {
        var fixture = new Fixture();
        fixture.Proxy.LoseWrites = true;

        await fixture.Sync.RequestSync();

        Assert.Equal(GatewayFileAccessSyncStatus.Failed, fixture.Sync.Status);
        Assert.Single(fixture.Proxy.Calls, call => call.StartsWith("config.patch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sync_InheritsOnlyFromTheGatewaysDisplayNameField()
    {
        // The Gateway policy matches displayName, so a node.list "name" must not select that entry.
        var fixture = new Fixture();
        fixture.Proxy.ConfigJson = """{"config":{"plugins":{"entries":{"file-transfer":{"config":{"policyVersion":2,"nodes":{"Win PC":{"ask":"always"}}}}}}},"hash":"h1"}""";
        fixture.Proxy.NodeListJson = """{"nodes":[{"nodeId":"node1","name":"Win PC"}]}""";

        await fixture.Sync.RequestSync();

        Assert.False(NodeEntry(fixture.Proxy).TryGetProperty("ask", out _));
    }
}
