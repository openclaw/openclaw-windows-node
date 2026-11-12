using System.Text.Json;
using OpenClaw.TestSupport;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class PersonalTailscaleGatewayClientTests
{
    [Fact]
    public async Task CurrentSocketRequiresPersonalHelloAndVerifiedProfileBeforeApplicationReadiness()
    {
        using var identity = new TempDirectory("personal-wire-");
        await using var peer = new PersonalGatewayProtocolFixture();
        using var client = peer.CreateClient(identity.Path);
        var hello = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.HandshakeSucceeded += (_, _) => hello.TrySetResult();
        await client.ConnectAsync();
        await peer.ChallengeAsync();
        var connect = await peer.ReceiveAsync();
        Assert.Equal("connect", connect.GetProperty("method").GetString());
        var parameters = connect.GetProperty("params");
        Assert.Empty(parameters.GetProperty("auth").EnumerateObject());
        Assert.Equal("gateway-client", parameters.GetProperty("client").GetProperty("id").GetString());
        Assert.Equal("ui", parameters.GetProperty("client").GetProperty("mode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(parameters.GetProperty("device").GetProperty("signature").GetString()));
        await peer.HelloAsync(connect);
        await hello.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(client.IsConnectedToGateway);
        Assert.Null(client.SessionMutationConnectionEpoch);
        var epoch = Assert.IsType<long>(client.PersonalHandshakeConnectionEpoch);
        Assert.Throws<InvalidOperationException>(() => client.CreatePersonalBrowserAuthResponse("nonce", 1, epoch));
        var reading = client.ReadPersonalIdentityAsync(epoch);
        var self = await peer.ReceiveAsync();
        Assert.Equal("users.self", self.GetProperty("method").GetString());
        await peer.SelfAsync(self);
        var profile = await reading;
        client.VerifyPersonalIdentity(profile.GetProperty("profile").GetProperty("id").GetString()!, epoch);
        Assert.True(client.IsConnectedToGateway);
        using var signed = JsonDocument.Parse(JsonSerializer.Serialize(client.CreatePersonalBrowserAuthResponse("browser-nonce", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), epoch)));
        Assert.Empty(signed.RootElement.GetProperty("auth").EnumerateObject());
        Assert.Equal("tailscale", signed.RootElement.GetProperty("requiredAuthMethod").GetString());
        Assert.Equal("fixture-scope", signed.RootElement.GetProperty("expectedRecoveryScope").GetString());
        Assert.Equal(client.AuthenticatedSigningDeviceId, signed.RootElement.GetProperty("device").GetProperty("id").GetString());
        await client.DisconnectAsync();
        Assert.Null(client.PersonalHandshakeConnectionEpoch);
        Assert.False(client.IsConnectedToGateway);
        Assert.Throws<InvalidOperationException>(() => client.CreatePersonalBrowserAuthResponse("late", 1, epoch));
    }

    [Theory]
    [InlineData("token", "fixture-scope")]
    [InlineData("tailscale", "")]
    [InlineData("tailscale", "  ")]
    [InlineData("", "fixture-scope")]
    public async Task HelloWithWrongMethodOrMissingScopeNeverPublishesPersonalReadiness(string method, string scope)
    {
        using var identity = new TempDirectory("personal-invalid-wire-");
        await using var peer = new PersonalGatewayProtocolFixture();
        using var client = peer.CreateClient(identity.Path);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.AuthenticationFailed += (_, _) => failed.TrySetResult();
        await client.ConnectAsync();
        await peer.ChallengeAsync();
        var connect = await peer.ReceiveAsync();
        await peer.HelloAsync(connect, method, scope);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(client.PersonalHandshakeConnectionEpoch);
        Assert.False(client.IsConnectedToGateway);
    }

    [Theory]
    [InlineData("ws://protocol.example.ts.net", "")]
    [InlineData("wss://127.0.0.1", "")]
    [InlineData("wss://protocol.example.ts.net?token=private", "")]
    [InlineData("wss://protocol.example.ts.net", "shared")]
    public void PersonalConstructorRejectsUnsafeRoutesAndMixedCredentials(string route, string token)
    {
        using var identity = new TempDirectory("personal-route-");
        Assert.Throws<ArgumentException>(() => new OpenClawGatewayClient(route, token,
            identityPath: identity.Path, operatorAuthentication: OperatorAuthenticationMode.PersonalTailscale));
    }
}
