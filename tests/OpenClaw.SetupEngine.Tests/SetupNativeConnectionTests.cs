using System.Text;
using OpenClaw.Connection;

namespace OpenClaw.SetupEngine.Tests;

public sealed class SetupNativeConnectionTests
{
    [Fact]
    public void SetupCode_PreservesBootstrapKindAndResolvesMissingAddress()
    {
        var result = SetupNativeConnectionInputResolver.Resolve(new(SetupCode: Code(
            """{"url":"wss://gateway.example","bootstrapToken":"bootstrap"}""")));
        Assert.Equal("wss://gateway.example", result.GatewayUrl);
        Assert.Equal("bootstrap", result.BootstrapToken);
        Assert.Null(result.SharedToken);
    }

    [Fact]
    public void EditedAddress_MustMatchSetupCode()
    {
        Assert.Throws<ArgumentException>(() => SetupNativeConnectionInputResolver.Resolve(new(
            "wss://changed.example", Code("""{"url":"wss://original.example","bootstrapToken":"bootstrap"}"""))));
    }

    [Theory]
    [InlineData("""{"bootstrapToken":"bootstrap"}""", "wss://gateway.example")]
    [InlineData("""{"url":"wss://gateway.example"}""", "wss://gateway.example")]
    [InlineData("""{"url":"https://gateway.example"}""", "wss://gateway.example")]
    public void PartialSetupCode_IsResolvedAgainstCurrentAddress(string json, string url)
    {
        var result = SetupNativeConnectionInputResolver.Resolve(new(url, Code(json)));
        Assert.Equal(url, result.GatewayUrl);
    }

    [Theory]
    [InlineData("file:///tmp/gateway")]
    [InlineData("wss://user:password@gateway.example")]
    [InlineData("wss://gateway.example?token=secret")]
    [InlineData("wss://gateway.example#secret")]
    public void UnsafeOrInvalidAddress_IsRejected(string url) =>
        Assert.Throws<ArgumentException>(() => SetupNativeConnectionInputResolver.Resolve(new(url)));

    [Fact]
    public void SetupCodeAndSharedToken_AreMutuallyExclusive() =>
        Assert.Throws<ArgumentException>(() => SetupNativeConnectionInputResolver.Resolve(new(
            "wss://gateway.example", Code("""{"bootstrapToken":"bootstrap"}"""), "shared")));

    [Fact]
    public void Ssh_UsesExistingArgumentValidation()
    {
        var ssh = new SshTunnelConfig("user", "host.example", 18789, 45678, SshPort: 2222);
        Assert.NotNull(SetupNativeConnectionInputResolver.Resolve(new("ws://127.0.0.1:18789", SshTunnel: ssh)));
        Assert.Throws<ArgumentException>(() => SetupNativeConnectionInputResolver.Resolve(new(
            "ws://127.0.0.1:18789", SshTunnel: ssh with { Host = "host -oProxyCommand=bad" })));
    }

    [Fact]
    public void Ssh_SavedPublicAddressKeepsExistingHostEndpointSemantics()
    {
        var ssh = new SshTunnelConfig("user", "host.example", 18789, 45678);
        var result = SetupNativeConnectionInputResolver.Resolve(new(
            "wss://gateway.example/gateway", SshTunnel: ssh, EditingGatewayId: "saved-ssh"));
        Assert.Equal("wss://gateway.example/gateway", result.GatewayUrl);
        Assert.Equal("ws://localhost:45678", GatewayClientEndpointResolver.Resolve(
            new GatewayRecord { Url = result.GatewayUrl, SshTunnel = ssh }));
    }

    [Fact]
    public void Ssh_PublicSetupCodeAddressStillMustMatchTheEditedAddress()
    {
        var ssh = new SshTunnelConfig("user", "host.example", 18789, 45678);
        var code = Code("""{"url":"wss://gateway.example","bootstrapToken":"bootstrap"}""");
        var result = SetupNativeConnectionInputResolver.Resolve(new(SetupCode: code, SshTunnel: ssh));
        Assert.Equal("wss://gateway.example", result.GatewayUrl);
        Assert.Equal("bootstrap", result.BootstrapToken);
        Assert.Null(result.SharedToken);
        Assert.Throws<ArgumentException>(() => SetupNativeConnectionInputResolver.Resolve(
            new("wss://different.example", code, SshTunnel: ssh)));
    }

    private static string Code(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
}
