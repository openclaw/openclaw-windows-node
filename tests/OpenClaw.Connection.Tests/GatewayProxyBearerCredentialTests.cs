using OpenClaw.Connection;

namespace OpenClaw.Connection.Tests;

public sealed class GatewayProxyBearerCredentialTests
{
    [Fact]
    public void OriginRequiresTlsAndRejectsUrlCredentials()
    {
        Assert.Equal("wss://gateway.example", GatewayProxyBearerCredential.RequireOrigin("wss://gateway.example/ws"));
        Assert.Throws<InvalidDataException>(() => GatewayProxyBearerCredential.RequireOrigin("ws://gateway.example/ws"));
        Assert.Throws<InvalidDataException>(() => GatewayProxyBearerCredential.RequireOrigin("wss://user:pass@gateway.example/ws"));
    }

    [Theory]
    [InlineData("=abc")]
    [InlineData("abc=def")]
    [InlineData("abc!def")]
    public void ProtectRejectsInvalidRfc6750Tokens(string token)
    {
        Assert.Throws<InvalidDataException>(() =>
            GatewayProxyBearerCredential.Protect(token, "wss://gateway.example"));
    }

    [Fact]
    public void ProtectedCredentialCannotBeReboundToAnotherOrigin()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string origin = "wss://gateway.example";
        var protectedToken = GatewayProxyBearerCredential.Protect("abc.def-123", origin);
        Assert.DoesNotContain("abc.def-123", protectedToken, StringComparison.Ordinal);
        var record = new GatewayRecord
        {
            Url = "wss://gateway.example/ws", ProxyBearerOrigin = origin,
            ProtectedProxyBearerToken = protectedToken,
        };
        Assert.Equal("abc.def-123", GatewayProxyBearerCredential.Reveal(record));
        Assert.Throws<InvalidDataException>(() => GatewayProxyBearerCredential.Reveal(
            record with { Url = "wss://other.example/ws" }));
        Assert.Throws<InvalidDataException>(() => GatewayProxyBearerCredential.Reveal(
            record with { ProxyBearerOrigin = "wss://other.example" }));
    }
}
