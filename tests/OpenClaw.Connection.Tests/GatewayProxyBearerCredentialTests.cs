using OpenClaw.Connection;

namespace OpenClaw.Connection.Tests;

public sealed class GatewayProxyBearerCredentialTests
{
    [Fact]
    public void OriginRequiresTlsAndRejectsUrlCredentials()
    {
        Assert.Equal("wss://gateway.example", GatewayProxyBearerCredential.RequireOrigin("wss://gateway.example/ws"));
        Assert.Throws<GatewayProxyCredentialException>(() => GatewayProxyBearerCredential.RequireOrigin("ws://gateway.example/ws"));
        Assert.Throws<GatewayProxyCredentialException>(() => GatewayProxyBearerCredential.RequireOrigin("wss://user:pass@gateway.example/ws"));
    }

    [Theory]
    [InlineData("=abc")]
    [InlineData("abc=def")]
    [InlineData("abc!def")]
    public void ProtectRejectsInvalidRfc6750Tokens(string token)
    {
        var error = Assert.Throws<GatewayProxyCredentialException>(() =>
            GatewayProxyBearerCredential.Protect(token, "wss://gateway.example"));
        Assert.Equal(GatewayProxyCredentialFailure.InvalidToken, error.Failure);
        Assert.DoesNotContain(token, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SavedCredentialMismatch_HasAnActionableFiniteFailureWithoutSecretText()
    {
        var error = Assert.Throws<GatewayProxyCredentialException>(() => GatewayProxyBearerCredential.Reveal(
            new GatewayRecord
            {
                Url = "wss://replacement.example/ws", ProxyBearerOrigin = "wss://original.example",
                ProtectedProxyBearerToken = "private-test-value",
            }));
        Assert.Equal(GatewayProxyCredentialFailure.SavedCredential, error.Failure);
        Assert.DoesNotContain("private-test-value", error.Message, StringComparison.Ordinal);
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
        Assert.Throws<GatewayProxyCredentialException>(() => GatewayProxyBearerCredential.Reveal(
            record with { Url = "wss://other.example/ws" }));
        Assert.Throws<GatewayProxyCredentialException>(() => GatewayProxyBearerCredential.Reveal(
            record with { ProxyBearerOrigin = "wss://other.example" }));
    }
}
