using System.Text.Json;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Shared.Tests;

public sealed partial class GatewayFixtureProtocolTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"sessions\":null}")]
    [InlineData("{\"sessions\":[17]}")]
    [InlineData("{\"sessions\":[{}]}")]
    [InlineData("{\"sessions\":[{\"key\":\" \"}]}")]
    [InlineData("{\"sessions\":[{\"key\":\"agent:research:fixture-other\",\"sessionId\":\"authority-session\"},{}]}")]
    [InlineData("{\"sessions\":[{\"key\":\"agent:research:fixture-other\",\"sessionId\":\"first\"},{\"key\":\"agent:research:fixture-other\",\"sessionId\":\"second\"}]}")]
    public async Task AuthoritativeRosterRejectsMalformedCollectionsAndRows(string payload)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        server.SetSessionListing(JsonSerializer.Deserialize<JsonElement>(payload));
        var first = server.Requests.Count;

        await Assert.ThrowsAsync<JsonException>(() =>
            connected.Client.RequestAuthoritativeSessionsAsync((int)Deadline.TotalMilliseconds).WaitAsync(Deadline));

        Assert.Contains(server.Requests.Skip(first), request =>
            request.Method == "sessions.list" && request.AuthorityScoped && request.Outcome == "ok");
        Assert.Empty(server.UnexpectedRequests);
    }

    [Theory]
    [InlineData("{\"sessions\":[]}", false)]
    [InlineData("{\"sessions\":{}}", false)]
    [InlineData("{\"sessions\":{\"agent:research:fixture-other\":{\"sessionId\":\"authority-session\",\"displayName\":\"Independent wire row\"}}}", true)]
    [InlineData("{\"agent:research:fixture-other\":{\"sessionId\":\"authority-session\",\"displayName\":\"Independent wire row\"}}", true)]
    [InlineData("{\"sessions\":[{\"key\":\"agent:research:fixture-other\",\"sessionId\":\"authority-session\",\"displayName\":\"Independent wire row\"}]}", true)]
    public async Task AuthoritativeRosterAcceptsValidEmptyAndNonemptyCollections(string payload, bool hasRow)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        server.SetSessionListing(JsonSerializer.Deserialize<JsonElement>(payload));

        var rows = await connected.Client.RequestAuthoritativeSessionsAsync((int)Deadline.TotalMilliseconds).WaitAsync(Deadline);

        if (!hasRow) Assert.Empty(rows);
        else
        {
            var row = Assert.Single(rows);
            Assert.Equal("agent:research:fixture-other", row.Key);
            Assert.Equal("authority-session", row.SessionId);
            Assert.Equal("Independent wire row", row.DisplayName);
        }
        Assert.Empty(server.UnexpectedRequests);
    }
}
