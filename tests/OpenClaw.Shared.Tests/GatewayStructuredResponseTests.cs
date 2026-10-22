using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public sealed class GatewayStructuredResponseTests
{
    [Theory]
    [InlineData("{}", false)]
    [InlineData("""{"features":{}}""", false)]
    [InlineData("""{"features":{"methods":null}}""", false)]
    [InlineData("""{"features":{"methods":"sessions.files.get"}}""", false)]
    [InlineData("""{"features":{"methods":["sessions.files.get",1]}}""", false)]
    [InlineData("""{"features":{"methods":[""]}}""", false)]
    [InlineData("""{"features":{"methods":[]}}""", true)]
    [InlineData("""{"features":{"methods":["sessions.files.get"]}}""", true)]
    public void AdvertisementKnownRequiresExplicitValidArray(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, GatewayServerMethodAdvertisement.HasMethods(document.RootElement));
    }

    [Fact]
    public void StructuredRejectionClonesDetailsAndPreservesCatchCompatibility()
    {
        GatewayRequestException error;
        using (var document = JsonDocument.Parse(
            """{"error":{"code":"CONFLICT","message":"Changed","details":{"type":"session_file_conflict","currentHash":"abcd"}}}"""))
            error = GatewayRequestException.FromResponse(document.RootElement, "Changed");
        Assert.IsAssignableFrom<InvalidOperationException>(error);
        Assert.Equal("CONFLICT", error.Code);
        Assert.Equal("session_file_conflict", error.DetailType);
        Assert.Equal("abcd", error.Details!.Value.GetProperty("currentHash").GetString());
        Assert.DoesNotContain("abcd", error.ToString());
    }
}

public sealed partial class GatewayProtocolLiveRoundTripTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilesAdvertisementDistinguishesOmittedFromEmptyAndClearsOnDisconnect(bool known)
    {
        using var server = new LoopbackGatewayServer();
        server.OnMethod("connect", _ => known
            ? new { type = "hello-ok", protocol = 4, features = new { methods = Array.Empty<string>() } }
            : (object)new { type = "hello-ok", protocol = 4 });
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: _identityDir);
        Assert.False(client.HasServerMethodAdvertisement);
        await ConnectAndWaitAsync(client, server, configureHandshake: false);
        Assert.Equal(known, client.HasServerMethodAdvertisement);
        await client.DisconnectAsync();
        Assert.False(client.HasServerMethodAdvertisement);
        server.OnMethod("connect", _ => known
            ? (object)new { type = "hello-ok", protocol = 4 }
            : new { type = "hello-ok", protocol = 4, features = new { methods = new[] { "sessions.files.get" } } });
        await ConnectAndWaitAsync(client, server, configureHandshake: false);
        Assert.Equal(!known, client.HasServerMethodAdvertisement);
        Assert.Equal(known ? Array.Empty<string>() : new[] { "sessions.files.get" }, client.AdvertisedServerMethods);
        await client.DisconnectAsync();
    }

    [Fact]
    public async Task StructuredGatewayRejectionSurvivesActualResponseTransport()
    {
        using var server = new LoopbackGatewayServer();
        server.OnMethod("files-proof", _ => new LoopbackResponse(false, null, new
        {
            code = "FORBIDDEN", message = "Not authorized",
            details = new { type = "session_file_source_changed", marker = "retained-not-logged" }
        }));
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: _identityDir);
        await ConnectAndWaitAsync(client, server);
        var error = await Assert.ThrowsAsync<GatewayRequestException>(
            () => client.SendWizardRequestAsync("files-proof"));
        Assert.Equal("FORBIDDEN", error.Code);
        Assert.Equal("session_file_source_changed", error.DetailType);
        Assert.Equal("retained-not-logged", error.Details!.Value.GetProperty("marker").GetString());
        await client.DisconnectAsync();
    }
}
