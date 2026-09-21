using System.Text;
using System.Text.Json;
using OpenClaw.Shared.Browser;

namespace OpenClaw.Connection.Tests;

public class BrowserBootstrapServiceTests
{
    private static readonly byte[] Request = Encoding.UTF8.GetBytes("{\"v\":1,\"op\":\"bootstrap\",\"nonce\":\"AAAAAAAAAAAAAAAAAAAAAA\"}");
    private static GatewayRecord Local => new() { Id = "local", Url = "ws://127.0.0.1:18789", IsLocal = true, SetupManagedDistroName = "OpenClawGateway" };
    private static string PairingJson(int port = 18789, bool remote = false) => JsonSerializer.Serialize(new
    {
        remote,
        pairingString = $"ws://127.0.0.1:{port}/browser/extension?gateway=ws%3A%2F%2F127.0.0.1%3A{port}#" + new string('a', 64),
        relayPort = 18792
    });
    private static bool IsOk(byte[] bytes) { using var doc = JsonDocument.Parse(bytes); return doc.RootElement.GetProperty("ok").GetBoolean(); }

    [Fact]
    public async Task FirstInstallBeforeSetup_RemainsRetryable()
    {
        var service = new BrowserBootstrapService(() => null, _ => false,
            (_, _) => throw new InvalidOperationException("Must not probe"),
            (_, _) => throw new InvalidOperationException("Must not run"));
        using var result = JsonDocument.Parse(await service.HandleAsync(Request, default));
        Assert.Equal("pairing_unavailable", result.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Local_DelegatesToPinnedDistroWithoutReadingGatewayCredentials()
    {
        var record = Local with { SharedGatewayToken = "test-token-placeholder", BootstrapToken = "test-token-placeholder" };
        var calls = 0;
        var service = new BrowserBootstrapService(() => record, _ => true, (_, _) => Task.FromResult(true),
            (distro, _) => { Assert.Equal("OpenClawGateway", distro); calls++; return Task.FromResult(PairingJson()); });
        Assert.True(IsOk(await service.HandleAsync(Request, default)));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task DisabledOrUnverified_DoesNotRunCli(bool allowed, bool verified)
    {
        var service = new BrowserBootstrapService(() => Local, _ => allowed, (_, _) => Task.FromResult(verified),
            (_, _) => throw new InvalidOperationException("Must not run"));
        Assert.False(IsOk(await service.HandleAsync(Request, default)));
    }

    [Fact]
    public async Task DisconnectDuringCli_DiscardsPairing()
    {
        var allowed = true;
        var service = new BrowserBootstrapService(() => Local, _ => allowed, (_, _) => Task.FromResult(true),
            (_, _) => { allowed = false; return Task.FromResult(PairingJson()); });
        Assert.False(IsOk(await service.HandleAsync(Request, default)));
    }

    [Fact]
    public async Task SwitchAwayAndBackDuringCli_DiscardsOldGeneration()
    {
        BrowserBootstrapService? service = null;
        service = new BrowserBootstrapService(() => Local, _ => true, (_, _) => Task.FromResult(true),
            (_, _) => { service!.Invalidate(); return Task.FromResult(PairingJson()); });
        Assert.False(IsOk(await service.HandleAsync(Request, default)));
    }

    [Fact]
    public async Task EndpointChangedAfterCli_DiscardsPairing()
    {
        var probes = 0;
        var service = new BrowserBootstrapService(() => Local, _ => true, (_, _) => Task.FromResult(++probes == 1),
            (_, _) => Task.FromResult(PairingJson()));
        Assert.False(IsOk(await service.HandleAsync(Request, default)));
    }

    [Fact]
    public void LocalAdmission_RejectsRemoteManualSshAndLegacyNameInference()
    {
        Assert.True(BrowserBootstrapService.IsManagedLocal(Local));
        Assert.False(BrowserBootstrapService.IsManagedLocal(Local with { Url = "wss://example.com" }));
        Assert.False(BrowserBootstrapService.IsManagedLocal(Local with { IsLocal = false }));
        Assert.False(BrowserBootstrapService.IsManagedLocal(Local with { SetupManagedDistroName = null, FriendlyName = "Local (OpenClawGateway)" }));
        Assert.False(BrowserBootstrapService.IsManagedLocal(Local with { Url = "ws://127.0.0.1:18789/path" }));
        Assert.False(BrowserBootstrapService.IsManagedLocal(Local with { Url = "ws://127.0.0.1:18789?unexpected=1" }));
    }

    [Fact]
    public void PairingOutput_MustMatchLocalGatewayTopologyAndPort()
    {
        Assert.NotEmpty(BrowserBootstrapService.ParsePairing(PairingJson(), 18789));
        Assert.Throws<InvalidDataException>(() => BrowserBootstrapService.ParsePairing(PairingJson(18790), 18789));
        Assert.Throws<InvalidDataException>(() => BrowserBootstrapService.ParsePairing(PairingJson(remote: true), 18789));
        Assert.Throws<InvalidDataException>(() => BrowserBootstrapService.ParsePairing(PairingJson().Replace("/browser/extension", "/extension"), 18789));
    }

    [Theory]
    [InlineData("ws://[::1]:18789")]
    [InlineData("ws://localhost:18789")]
    public async Task AliasRecord_CannotAuthorizeAnUnownedIpv4Destination(string activeUrl)
    {
        var active = Local with { Url = activeUrl };
        var service = new BrowserBootstrapService(() => active, _ => true,
            (destination, _) => Task.FromResult(destination.Url == activeUrl),
            (_, _) => throw new InvalidOperationException("Must not run against unowned IPv4"));
        Assert.False(IsOk(await service.HandleAsync(Request, default)));
    }

    [Fact]
    public async Task BothProvenanceChecks_PinActualDestinationAndDistro()
    {
        var active = Local with { Url = "ws://[::1]:18789" };
        var calls = 0;
        var service = new BrowserBootstrapService(() => active, _ => true, (destination, _) =>
        {
            Assert.Equal("ws://127.0.0.1:18789", destination.Url);
            Assert.Equal(active.Id, destination.Id);
            Assert.Equal(active.SetupManagedDistroName, destination.SetupManagedDistroName);
            calls++;
            return Task.FromResult(true);
        }, (_, _) => Task.FromResult(PairingJson()));
        Assert.True(IsOk(await service.HandleAsync(Request, default)));
        Assert.Equal(2, calls);
        Assert.Equal("ws://[::1]:18789", active.Url);
    }

    [Theory]
    [InlineData("set -e\necho proof", "set -e\necho proof\n")]
    [InlineData("set -e\r\necho proof\r\n", "set -e\necho proof\n")]
    [InlineData("set -e\recho proof", "set -e\necho proof\n")]
    public void WslInput_NormalizesWindowsCheckoutLineEndings(string input, string expected) =>
        Assert.Equal(expected, BrowserBootstrapWslCommand.NormalizeStandardInput(input));

    [Fact]
    public void Command_UsesCanonicalPairingNotGatewaySecretsOrLifecycle()
    {
        var script = BrowserBootstrapWslCommand.BuildInput(new string('a', 32));
        var encoded = System.Text.RegularExpressions.Regex.Match(script, "printf '%s' '([A-Za-z0-9+/=]+)'").Groups[1].Value;
        var program = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        Assert.Contains("[process.argv[1],'browser','extension','pair','--json','--local-gateway']", program);
        Assert.DoesNotContain("exec \"$cli\"", script);
        Assert.DoesNotContain("\r", script);
        Assert.DoesNotContain("DelayMs", program);
        Assert.Contains("unset OPENCLAW_PROFILE OPENCLAW_STATE_DIR OPENCLAW_CONFIG_PATH", BrowserBootstrapWslCommand.Script);
        Assert.DoesNotContain("gateway start", BrowserBootstrapWslCommand.Script);
        Assert.DoesNotContain("auth.token", BrowserBootstrapWslCommand.Script);
    }
}
