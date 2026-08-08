using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

public sealed class GatewayTailscaleAuthUpgradeTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    [Fact]
    public async Task EnableAsync_PersistsRecordBeforePatchingCore()
    {
        var registry = CreateRegistry();
        var client = new FakeConfigClient(Config(allowTailscale: false));
        client.BeforePatch = () => Assert.True(registry.GetActive()!.TrustTailscaleAuth);
        var service = new GatewayTailscaleAuthUpgradeService(registry);

        var result = await service.EnableAsync("gateway-1", client, CancellationToken.None);

        Assert.Equal(GatewayTailscaleAuthUpgradeOutcome.Succeeded, result.Outcome);
        Assert.True(registry.GetActive()!.TrustTailscaleAuth);
        Assert.Equal("test-token-placeholder", registry.GetActive()!.SharedGatewayToken);
        Assert.Equal(1, client.PatchCalls);
        Assert.Equal("base-1", client.PatchBaseHash);
        Assert.True(client.PatchedConfig!.Value
            .GetProperty("gateway")
            .GetProperty("auth")
            .GetProperty("allowTailscale")
            .GetBoolean());
        Assert.False(client.PatchedConfig.Value.TryGetProperty("unrelated", out _));

        var reloaded = new GatewayRegistry(_temp.Path);
        reloaded.Load();
        Assert.True(reloaded.GetActive()!.TrustTailscaleAuth);
        Assert.Equal("test-token-placeholder", reloaded.GetActive()!.SharedGatewayToken);
    }

    [Fact]
    public async Task EnableAsync_CoreAlreadyEnabled_RequiresAcceptedIdempotentPatch()
    {
        var registry = CreateRegistry();
        var client = new FakeConfigClient(Config(allowTailscale: true));
        var service = new GatewayTailscaleAuthUpgradeService(registry);

        var result = await service.EnableAsync("gateway-1", client, CancellationToken.None);

        Assert.Equal(GatewayTailscaleAuthUpgradeOutcome.Succeeded, result.Outcome);
        Assert.True(registry.GetActive()!.TrustTailscaleAuth);
        Assert.Equal(1, client.PatchCalls);
    }

    [Fact]
    public async Task EnableAsync_PersistedMarkerRevalidatesLiveCoreState()
    {
        var registry = CreateRegistry();
        registry.Update("gateway-1", current => current with { TrustTailscaleAuth = true });
        registry.Save();
        var client = new FakeConfigClient(Config(allowTailscale: false));
        var service = new GatewayTailscaleAuthUpgradeService(registry);

        var result = await service.EnableAsync("gateway-1", client, CancellationToken.None);

        Assert.Equal(GatewayTailscaleAuthUpgradeOutcome.Succeeded, result.Outcome);
        Assert.True(registry.GetActive()!.TrustTailscaleAuth);
        Assert.Equal(1, client.ConfigRequests);
        Assert.Equal(1, client.PatchCalls);
    }

    [Fact]
    public async Task EnableAsync_PersistedMarkerAndLiveCoreStateReturnAlreadyEnabled()
    {
        var registry = CreateRegistry();
        registry.Update("gateway-1", current => current with { TrustTailscaleAuth = true });
        registry.Save();
        var client = new FakeConfigClient(Config(allowTailscale: true));
        var service = new GatewayTailscaleAuthUpgradeService(registry);

        var result = await service.EnableAsync("gateway-1", client, CancellationToken.None);

        Assert.Equal(GatewayTailscaleAuthUpgradeOutcome.AlreadyEnabled, result.Outcome);
        Assert.True(registry.GetActive()!.TrustTailscaleAuth);
        Assert.Equal(1, client.ConfigRequests);
        Assert.Equal(0, client.PatchCalls);
    }

    [Fact]
    public async Task EnableAsync_PersistedMarkerDoesNotRequirePatchHashWhenCoreAllowsTailscale()
    {
        var registry = CreateRegistry();
        registry.Update("gateway-1", current => current with { TrustTailscaleAuth = true });
        registry.Save();
        var client = new FakeConfigClient(Config(allowTailscale: true, includeBaseHash: false));
        var service = new GatewayTailscaleAuthUpgradeService(registry);

        var result = await service.EnableAsync("gateway-1", client, CancellationToken.None);

        Assert.Equal(GatewayTailscaleAuthUpgradeOutcome.AlreadyEnabled, result.Outcome);
        Assert.Equal(0, client.PatchCalls);
    }

    [Fact]
    public async Task EnableAsync_MissingBaseHash_FailsClosedBeforePatch()
    {
        var registry = CreateRegistry();
        var client = new FakeConfigClient(Config(allowTailscale: false, includeBaseHash: false));
        var service = new GatewayTailscaleAuthUpgradeService(registry);

        var result = await service.EnableAsync("gateway-1", client, CancellationToken.None);

        Assert.Equal(GatewayTailscaleAuthUpgradeOutcome.ConfigUnavailable, result.Outcome);
        Assert.Equal(0, client.PatchCalls);
        Assert.False(registry.GetActive()!.TrustTailscaleAuth);
    }

    [Fact]
    public async Task EnableAsync_PatchRejected_LeavesRecordAndCredentialsUntouched()
    {
        var registry = CreateRegistry();
        var client = new FakeConfigClient(Config(allowTailscale: false))
        {
            PatchResult = new ConfigPatchResult
            {
                Ok = false,
                Error = "rejected",
                IsGatewayRejection = true,
            },
        };
        var service = new GatewayTailscaleAuthUpgradeService(registry);

        var result = await service.EnableAsync("gateway-1", client, CancellationToken.None);

        Assert.Equal(GatewayTailscaleAuthUpgradeOutcome.PatchRejected, result.Outcome);
        Assert.False(registry.GetActive()!.TrustTailscaleAuth);
        Assert.Equal("test-token-placeholder", registry.GetActive()!.SharedGatewayToken);
    }

    [Fact]
    public async Task EnableAsync_AmbiguousPatchFailure_PreservesMarkerForRevalidation()
    {
        var registry = CreateRegistry();
        var client = new FakeConfigClient(Config(allowTailscale: false))
        {
            PatchResult = new ConfigPatchResult { Ok = false, Error = "request timed out" },
        };
        var service = new GatewayTailscaleAuthUpgradeService(registry);

        var result = await service.EnableAsync("gateway-1", client, CancellationToken.None);

        Assert.Equal(GatewayTailscaleAuthUpgradeOutcome.PatchRejected, result.Outcome);
        Assert.True(registry.GetActive()!.TrustTailscaleAuth);
        Assert.Equal("test-token-placeholder", registry.GetActive()!.SharedGatewayToken);
    }

    [Fact]
    public async Task EnableAsync_MissingScopes_DoesNotRequestOrMutateConfig()
    {
        var registry = CreateRegistry();
        var client = new FakeConfigClient(Config(allowTailscale: false))
        {
            Scopes = ["operator.read"],
        };
        var service = new GatewayTailscaleAuthUpgradeService(registry);

        var result = await service.EnableAsync("gateway-1", client, CancellationToken.None);

        Assert.Equal(GatewayTailscaleAuthUpgradeOutcome.MissingConfigScope, result.Outcome);
        Assert.Equal(0, client.ConfigRequests);
        Assert.False(registry.GetActive()!.TrustTailscaleAuth);
    }

    [Fact]
    public async Task EnableAsync_SaveFailure_RestoresInMemoryMarker()
    {
        var registry = CreateRegistry(new FailingWriteFileSystem());
        var client = new FakeConfigClient(Config(allowTailscale: true));
        var service = new GatewayTailscaleAuthUpgradeService(registry);

        var result = await service.EnableAsync("gateway-1", client, CancellationToken.None);

        Assert.Equal(GatewayTailscaleAuthUpgradeOutcome.PersistenceFailed, result.Outcome);
        Assert.False(registry.GetActive()!.TrustTailscaleAuth);
        Assert.Equal("test-token-placeholder", registry.GetActive()!.SharedGatewayToken);
        Assert.Equal(0, client.PatchCalls);
    }

    [Fact]
    public async Task RevalidateAsync_CoreStillAllowsTailscale_KeepsMarkerAndReturnsTrue()
    {
        var registry = CreateRegistry();
        registry.Update("gateway-1", current => current with { TrustTailscaleAuth = true });
        registry.Save();
        var client = new FakeConfigClient(Config(allowTailscale: true));
        var service = new GatewayTailscaleAuthUpgradeService(registry);

        var trusted = await service.RevalidateAsync("gateway-1", client, CancellationToken.None);

        Assert.True(trusted);
        Assert.True(registry.GetActive()!.TrustTailscaleAuth);
        Assert.Equal(1, client.ConfigRequests);
        Assert.Equal(0, client.PatchCalls);
    }

    [Fact]
    public async Task RevalidateAsync_OmittedExplicitGrantClearsMarker()
    {
        var registry = CreateRegistry();
        registry.Update("gateway-1", current => current with { TrustTailscaleAuth = true });
        registry.Save();
        var client = new FakeConfigClient(ImplicitServeConfig());
        var service = new GatewayTailscaleAuthUpgradeService(registry);

        var trusted = await service.RevalidateAsync("gateway-1", client, CancellationToken.None);

        Assert.False(trusted);
        Assert.False(registry.GetActive()!.TrustTailscaleAuth);
        Assert.Equal(1, client.ConfigRequests);
        Assert.Equal(0, client.PatchCalls);
    }

    [Fact]
    public async Task RevalidateAsync_CoreDisablesTailscale_ClearsPersistedMarkerAndReturnsFalse()
    {
        var registry = CreateRegistry();
        registry.Update("gateway-1", current => current with { TrustTailscaleAuth = true });
        registry.Save();
        var client = new FakeConfigClient(Config(allowTailscale: false));
        var service = new GatewayTailscaleAuthUpgradeService(registry);

        var trusted = await service.RevalidateAsync("gateway-1", client, CancellationToken.None);

        Assert.False(trusted);
        Assert.False(registry.GetActive()!.TrustTailscaleAuth);
        Assert.Equal("test-token-placeholder", registry.GetActive()!.SharedGatewayToken);
        Assert.Equal(0, client.PatchCalls);

        var reloaded = new GatewayRegistry(_temp.Path);
        reloaded.Load();
        Assert.False(reloaded.GetActive()!.TrustTailscaleAuth);
    }

    [Fact]
    public async Task RevalidateAsync_CallerCancellationRemainsCancellation()
    {
        var registry = CreateRegistry();
        registry.Update("gateway-1", current => current with { TrustTailscaleAuth = true });
        var client = new FakeConfigClient(Config(allowTailscale: true)) { EmitConfig = false };
        var service = new GatewayTailscaleAuthUpgradeService(registry);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RevalidateAsync("gateway-1", client, cancellation.Token));
    }

    [Fact]
    public async Task RevalidateAsync_EditedNonTailscaleEndpointDoesNotTrustMarker()
    {
        var registry = CreateRegistry();
        registry.Update("gateway-1", current => current with
        {
            Url = "wss://gateway.example.test",
            TrustTailscaleAuth = true,
        });
        var client = new FakeConfigClient(Config(allowTailscale: true));
        var service = new GatewayTailscaleAuthUpgradeService(registry);

        var trusted = await service.RevalidateAsync("gateway-1", client, CancellationToken.None);

        Assert.False(trusted);
        Assert.Equal(0, client.ConfigRequests);
    }

    [Theory]
    [MemberData(nameof(IneligibleRecords))]
    public void CanOffer_RejectsRecordsOutsideManagedTailnetBoundary(GatewayRecord record) =>
        Assert.False(GatewayTailscaleAuthUpgradePolicy.CanOffer(record));

    public static TheoryData<GatewayRecord> IneligibleRecords => new()
    {
        ManagedRecord() with { IsLocal = false },
        ManagedRecord() with { SetupManagedDistroName = null },
        ManagedRecord() with { SshTunnel = new SshTunnelConfig("user", "host", 18789, 18789) },
        ManagedRecord() with { Url = "ws://127.0.0.1:18789" },
        ManagedRecord() with { Url = "wss://gateway.example.test" },
    };

    public void Dispose() => _temp.Dispose();

    private GatewayRegistry CreateRegistry(IFileSystem? fileSystem = null)
    {
        var registry = new GatewayRegistry(_temp.Path, fileSystem);
        registry.AddOrUpdate(ManagedRecord());
        registry.SetActive("gateway-1");
        return registry;
    }

    private static GatewayRecord ManagedRecord() => new()
    {
        Id = "gateway-1",
        Url = "wss://openclaw-host.tail1234.ts.net",
        IsLocal = true,
        SetupManagedDistroName = "OpenClawGateway",
        SharedGatewayToken = "test-token-placeholder",
    };

    private static JsonElement Config(bool allowTailscale, bool includeBaseHash = true)
    {
        using var document = JsonDocument.Parse($$"""
            {
              "parsed": {
                "gateway": { "auth": { "allowTailscale": {{allowTailscale.ToString().ToLowerInvariant()}} } },
                "unrelated": "keep"
              }{{(includeBaseHash ? ",\n  \"baseHash\": \"base-1\"" : "")}}
            }
            """);
        return document.RootElement.Clone();
    }

    private static JsonElement ImplicitServeConfig()
    {
        using var document = JsonDocument.Parse("""
            {
              "parsed": {
                "gateway": {
                  "auth": { "mode": "token" },
                  "tailscale": { "mode": "serve" }
                }
              },
              "baseHash": "base-1"
            }
            """);
        return document.RootElement.Clone();
    }

    private sealed class FakeConfigClient(JsonElement config) : IGatewayTailscaleAuthConfigClient
    {
        public IReadOnlyList<string> Scopes { get; set; } = ["operator.read", "operator.write"];
        public IReadOnlyList<string> GrantedOperatorScopes => Scopes;
        public bool IsConnectedToGateway { get; set; } = true;
        public int ConfigRequests { get; private set; }
        public int PatchCalls { get; private set; }
        public string? PatchBaseHash { get; private set; }
        public JsonElement? PatchedConfig { get; private set; }
        public ConfigPatchResult PatchResult { get; set; } = new() { Ok = true };
        public bool EmitConfig { get; set; } = true;
        public Action? BeforePatch { get; set; }

        public Task<JsonElement> RequestConfigDetailedAsync(int timeoutMs = 15000)
        {
            ConfigRequests++;
            return EmitConfig
                ? Task.FromResult(config.Clone())
                : new TaskCompletionSource<JsonElement>().Task;
        }

        public Task<ConfigPatchResult> PatchConfigDetailedAsync(
            JsonElement fullConfig,
            string? baseHash,
            int timeoutMs = 15000)
        {
            BeforePatch?.Invoke();
            PatchCalls++;
            PatchBaseHash = baseHash;
            PatchedConfig = fullConfig.Clone();
            return Task.FromResult(PatchResult);
        }
    }

    private sealed class FailingWriteFileSystem : IFileSystem
    {
        public bool FileExists(string path) => false;
        public string ReadAllText(string path) => throw new NotSupportedException();
        public void WriteAllText(string path, string content) => throw new IOException("write failed");
        public void CreateDirectory(string path) { }
        public bool DirectoryExists(string path) => true;
        public void CopyFile(string source, string destination, bool overwrite) => throw new NotSupportedException();
        public void DeleteFile(string path) { }
    }
}
