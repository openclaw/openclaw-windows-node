using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class GatewayDashboardLinkServiceTests
{
    [Fact]
    public async Task BuildAsync_SharedCredential_AppendsCredentialWithoutRevalidation()
    {
        var revalidationCalls = 0;
        var service = CreateService((_, _) =>
        {
            revalidationCalls++;
            return Task.FromResult(true);
        });

        var result = await service.BuildAsync(Request());

        Assert.True(result.Success);
        Assert.True(result.BrowserCredentialIncluded);
        Assert.Contains("token=shared-token", result.Url);
        Assert.Equal(0, revalidationCalls);
    }

    [Fact]
    public async Task BuildAsync_TrustedTailscaleGateway_OmitsSharedCredential()
    {
        var service = CreateService((_, _) => Task.FromResult(true));

        var result = await service.BuildAsync(Request(tailscaleGatewayId: "gateway-1"));

        Assert.True(result.Success);
        Assert.True(result.TrustTailscaleAuth);
        Assert.False(result.BrowserCredentialIncluded);
        Assert.DoesNotContain("token=", result.Url);
    }

    [Fact]
    public async Task BuildAsync_FailedRevalidation_FallsBackToSharedCredential()
    {
        var service = CreateService((_, _) => Task.FromResult(false));

        var result = await service.BuildAsync(Request(tailscaleGatewayId: "gateway-1"));

        Assert.True(result.Success);
        Assert.False(result.TrustTailscaleAuth);
        Assert.True(result.BrowserCredentialIncluded);
        Assert.Contains("token=shared-token", result.Url);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildAsync_ExternalServe_PreservesConsentAndRequiresApprovedSharedFallback(
        bool approveSharedFallback)
    {
        using var temp = new TempDirectory();
        var registry = new GatewayRegistry(temp.Path);
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "gateway-1",
            Url = "wss://host.tail1234.ts.net",
            IsLocal = true,
            SetupManagedDistroName = "OpenClawGateway",
            TrustTailscaleAuth = true,
            SharedGatewayToken = "shared-token",
        });
        registry.SetActive("gateway-1");
        registry.Save();
        var commandRunner = new ExternalServeCommandRunner();
        var client = new AppliedTailscaleConfigClient();
        var upgrade = new GatewayTailscaleAuthUpgradeService(
            registry, new GatewayTailscaleAuthLiveVerifier(commandRunner));
        var service = CreateService((id, cancellationToken) =>
            upgrade.RevalidateAsync(id, client, cancellationToken));

        var result = await service.BuildAsync(Request(
            appendBrowserCredential: approveSharedFallback,
            tailscaleGatewayId: "gateway-1",
            gatewayUrl: "wss://host.tail1234.ts.net"));

        Assert.Equal(1, client.ConfigRequests);
        Assert.Equal(2, commandRunner.ProbeCalls);
        Assert.False(result.TrustTailscaleAuth);
        Assert.Equal(approveSharedFallback, result.Success);
        Assert.Equal(approveSharedFallback, result.BrowserCredentialIncluded);
        if (approveSharedFallback)
            Assert.Equal("https://host.tail1234.ts.net/settings/profile#token=shared-token", result.Url);
        else
        {
            Assert.Null(result.Url);
            Assert.Equal("localized:DashboardLink_NoBrowserCompatibleCredential", result.Error);
        }
        var reloaded = new GatewayRegistry(temp.Path);
        reloaded.Load();
        Assert.True(reloaded.GetActive()!.TrustTailscaleAuth);
        Assert.Equal("shared-token", reloaded.GetActive()!.SharedGatewayToken);
    }

    [Fact]
    public async Task BuildAsync_TokenFreeBootstrapRequest_PreservesDashboardUrl()
    {
        var service = CreateService((_, _) => Task.FromResult(false));

        var result = await service.BuildAsync(Request(
            browserCredential: "bootstrap-do-not-export",
            appendBrowserCredential: false,
            tailscaleGatewayId: null));

        Assert.True(result.Success);
        Assert.False(result.BrowserCredentialIncluded);
        Assert.NotNull(result.Url);
        Assert.DoesNotContain("token=", result.Url);
    }

    [Theory]
    [InlineData("shared", true, "record.SharedGatewayToken", false)]
    [InlineData("shared", false, "record.SharedGatewayToken", true)]
    [InlineData("bootstrap", false, "record.BootstrapToken", false)]
    public async Task BuildAsync_ResolvedCredentialProvenanceDoesNotImplyBrowserInclusion(
        string credentialKind, bool identityReady, string expectedSource, bool expectedInclusion)
    {
        using var data = new TempDirectory();
        var registry = new GatewayRegistry(data.Path);
        var record = new GatewayRecord
        {
            Id = "gateway-1",
            Url = "wss://host.tail1234.ts.net",
            SharedGatewayToken = credentialKind == "shared" ? "shared-token" : null,
            BootstrapToken = credentialKind == "bootstrap" ? "bootstrap-do-not-export" : null,
            TrustTailscaleAuth = credentialKind == "shared",
        };
        registry.AddOrUpdate(record);
        registry.SetActive(record.Id);
        Assert.True(InteractiveGatewayCredentialResolver.TryResolve(
            registry, data.Path, DeviceIdentityFileReader.Instance, record.Url, null, null, out var credential));
        Assert.NotNull(credential);
        Assert.Equal(expectedSource, credential.Source);
        var service = CreateService((_, _) => Task.FromResult(identityReady));

        var result = await service.BuildAsync(new GatewayDashboardLinkRequest(
            credential.GatewayUrl, null, credential.Token,
            !credential.IsBootstrapToken && credential.Source == CredentialResolver.SourceSharedGatewayToken,
            record.TrustTailscaleAuth ? record.Id : null));

        Assert.True(result.Success);
        Assert.Equal(identityReady, result.TrustTailscaleAuth);
        Assert.Equal(expectedInclusion, result.BrowserCredentialIncluded);
        Assert.Equal(expectedInclusion
            ? "https://host.tail1234.ts.net#token=shared-token"
            : "https://host.tail1234.ts.net", result.Url);
        Assert.DoesNotContain("bootstrap-do-not-export", result.Url);
    }

    [Fact]
    public async Task BuildAsync_TrustedTailscaleAuthStripsEmbeddedTokenFragment()
    {
        var service = CreateService((_, _) => Task.FromResult(true));

        var result = await service.BuildAsync(Request(
            appendBrowserCredential: false,
            browserCredential: null,
            tailscaleGatewayId: "gateway-1",
            gatewayUrl: "wss://gateway.example.test/mount/#token=stale&view=compact"));

        Assert.True(result.Success);
        Assert.True(result.TrustTailscaleAuth);
        Assert.False(result.BrowserCredentialIncluded);
        Assert.Equal("https://gateway.example.test/mount/settings/profile#view=compact", result.Url);
    }

    [Fact]
    public async Task BuildAsync_ExistingTokenFragmentCountsAsBrowserCredential()
    {
        var service = CreateService((_, _) => Task.FromResult(false));

        var result = await service.BuildAsync(Request(
            appendBrowserCredential: false,
            browserCredential: null,
            gatewayUrl: "wss://gateway.example.test/mount/#token=stale&view=compact"));

        Assert.True(result.Success);
        Assert.True(result.BrowserCredentialIncluded);
        Assert.Equal("https://gateway.example.test/mount/settings/profile#token=stale&view=compact", result.Url);
    }

    [Fact]
    public async Task BuildAsync_TailscaleRequestWithoutApprovedBrowserCredential_FailsClosed()
    {
        var service = CreateService((_, _) => Task.FromResult(false));

        var result = await service.BuildAsync(Request(
            appendBrowserCredential: false,
            tailscaleGatewayId: "gateway-1"));

        Assert.False(result.Success);
        Assert.Null(result.Url);
        Assert.Equal("localized:DashboardLink_NoBrowserCompatibleCredential", result.Error);
    }

    [Fact]
    public async Task BuildAsync_RevalidationException_PreservesFallbackAndSanitizesDiagnostic()
    {
        var service = CreateService((_, _) =>
            throw new InvalidOperationException("sensitive probe detail"));

        var result = await service.BuildAsync(Request(tailscaleGatewayId: "gateway-1"));

        Assert.True(result.Success);
        Assert.Contains("token=shared-token", result.Url);
        Assert.Equal("localized:DashboardLink_TailscaleRevalidationFailed", result.RevalidationError);
        Assert.DoesNotContain("sensitive", result.RevalidationError);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task BuildAsync_AppendRequestedWithoutCredential_FailsClosed(
        string? browserCredential)
    {
        var service = CreateService((_, _) => Task.FromResult(false));

        var result = await service.BuildAsync(Request(
            browserCredential: browserCredential,
            tailscaleGatewayId: "gateway-1"));

        Assert.False(result.Success);
        Assert.Null(result.Url);
        Assert.Equal("localized:DashboardLink_NoBrowserCompatibleCredential", result.Error);
    }

    [Fact]
    public async Task BuildAsync_CallerCancellation_PropagatesWithoutFallback()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var service = CreateService((_, cancellationToken) =>
            Task.FromCanceled<bool>(cancellationToken));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.BuildAsync(Request(tailscaleGatewayId: "gateway-1"), cts.Token));
    }

    [Fact]
    public async Task BuildAsync_NonCallerCancellation_PreservesFallbackAndSanitizesDiagnostic()
    {
        var service = CreateService((_, _) =>
            Task.FromException<bool>(new OperationCanceledException("sensitive probe detail")));

        var result = await service.BuildAsync(Request(tailscaleGatewayId: "gateway-1"));

        Assert.True(result.Success);
        Assert.Contains("token=shared-token", result.Url);
        Assert.Equal("localized:DashboardLink_TailscaleRevalidationFailed", result.RevalidationError);
        Assert.DoesNotContain("sensitive", result.RevalidationError);
    }

    private static GatewayDashboardLinkService CreateService(
        Func<string, CancellationToken, Task<bool>> revalidate) =>
        new(revalidate, key => $"localized:{key}");

    private sealed class AppliedTailscaleConfigClient : IGatewayTailscaleAuthConfigClient
    {
        public IReadOnlyList<string> GrantedOperatorScopes => ["operator.read"];
        public bool IsConnectedToGateway => true;
        public int ConfigRequests { get; private set; }

        public Task<JsonElement> RequestConfigDetailedAsync(int timeoutMs = 15000)
        {
            ConfigRequests++;
            using var config = JsonDocument.Parse("""
                {
                  "sourceConfig": {
                    "gateway": {
                      "auth": { "allowTailscale": true },
                      "tailscale": { "mode": "off" },
                      "trustedProxies": ["127.0.0.1"]
                    }
                  },
                  "configRevisionHash": "applied-config",
                  "appliedConfigHash": "applied-config"
                }
                """);
            return Task.FromResult(config.RootElement.Clone());
        }

        public Task<ConfigPatchResult> PatchConfigDetailedAsync(
            JsonElement fullConfig, string? baseHash, int timeoutMs = 15000) =>
            throw new NotSupportedException();
    }

    private sealed class ExternalServeCommandRunner : IWslCommandRunner
    {
        public int ProbeCalls { get; private set; }

        public Task<WslCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            ProbeCalls++;
            return Task.FromResult(new WslCommandResult(0, arguments.Contains("serve")
                ? """{"Web":{"host.tail1234.ts.net:443":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:18789"}}}}}"""
                : """{"BackendState":"Running","Self":{"DNSName":"host.tail1234.ts.net."}}""", ""));
        }

        public Task<WslCommandResult> RunInDistroAsync(
            string name, IReadOnlyList<string> command, CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null, string? standardInput = null) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<WslDistroInfo>> ListDistrosAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<WslCommandResult> TerminateDistroAsync(
            string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<WslCommandResult> UnregisterDistroAsync(
            string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static GatewayDashboardLinkRequest Request(
        bool appendBrowserCredential = true,
        string? browserCredential = "shared-token",
        string? tailscaleGatewayId = null,
        string? gatewayUrl = null) => new(
            gatewayUrl ?? "https://gateway.example.test",
            "/settings/profile",
            browserCredential,
            appendBrowserCredential,
            tailscaleGatewayId);
}
