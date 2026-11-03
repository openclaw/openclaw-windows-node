using OpenClaw.Shared;
using OpenClaw.TestSupport.Gateway;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed partial class GatewayFixtureProtocolTests
{
    [Theory]
    [InlineData(TranscriptSearchFixtureMode.Unsupported)]
    [InlineData(TranscriptSearchFixtureMode.Forbidden)]
    [InlineData(TranscriptSearchFixtureMode.Indexing)]
    public async Task TranscriptSearch_CompatibilityPermissionAndIndexReadinessStayDistinct(TranscriptSearchFixtureMode mode)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateTranscriptSearch(mode), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token, expectedSessionCount: 7);
        if (mode == TranscriptSearchFixtureMode.Forbidden)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                connected.Client.SearchSessionsAsync(new("lighthouse")));
            Assert.Contains("operator.read", error.Message);
        }
        else
        {
            var result = await connected.Client.SearchSessionsAsync(new("lighthouse"));
            Assert.Equal(mode != TranscriptSearchFixtureMode.Unsupported, result.IsSupported);
            Assert.Equal(mode == TranscriptSearchFixtureMode.Indexing, result.Indexing);
        }
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task TranscriptSearch_RealClientFindsMessagesWithDistinctProvenance()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var result = await connected.Client.SearchSessionsAsync(new("FIXTURE RESEARCH:"));
        Assert.True(result.IsSupported);
        Assert.Equal(2, result.Results.Count);
        Assert.All(result.Results, hit =>
        {
            Assert.Equal("agent:research:fixture-other", hit.SessionKey);
            Assert.Equal("00000000-0000-4000-8000-000000000003", hit.SessionId);
            Assert.Equal("assistant", hit.Role);
            Assert.Equal("Fixture: research conversation", hit.DisplayTitle);
            Assert.Equal("FIXTURE RESEARCH: the violet compass points north.", hit.Snippet);
        });
        Assert.Equal("fixture-shared-002", result.Results[0].MessageId);
        Assert.Equal("00000000-0000-4000-8000-000000000003-message-004", result.Results[1].MessageId);
        Assert.True(result.Results[0].Timestamp < result.Results[1].Timestamp);
        Assert.Empty((await connected.Client.SearchSessionsAsync(new("no-such-fixture-message"))).Results);
        Assert.Contains(server.Requests, r => r.Method == "sessions.search" && r.Outcome == "ok");
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task TranscriptSearch_FiltersMembershipBeforeLimitAndReflectsArchival()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateTranscriptSearch(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token, expectedSessionCount: 7);
        var client = connected.Client;
        var visible = await client.SearchSessionsAsync(new("lighthouse"));
        Assert.Equal("agent:main:main", Assert.Single(visible.Results).SessionKey);
        Assert.False(visible.Truncated);
        var background = await client.SearchSessionsAsync(new("lighthouse", IncludeBackground: true));
        Assert.Equal(25, background.Results.Count);
        Assert.True(background.Truncated);
        Assert.All(background.Results, hit => Assert.Equal("agent:main:cron:fixture-search", hit.SessionKey));

        await client.SendWizardRequestAsync("sessions.patch",
            new { key = GatewayScenario.OtherSessionKey, archived = true });
        Assert.Empty((await client.SearchSessionsAsync(new("FIXTURE RESEARCH:"))).Results);
        await client.SendWizardRequestAsync("sessions.patch",
            new { key = GatewayScenario.OtherSessionKey, archived = false });
        Assert.Equal(2, (await client.SearchSessionsAsync(new("FIXTURE RESEARCH:"))).Results.Count);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Theory]
    [InlineData("""{"query":"lighthouse","scope":{},"limit":26}""")]
    [InlineData("""{"query":"lighthouse","scope":{"excludeCron":"yes"}}""")]
    [InlineData("""{"query":"lighthouse","scope":{},"sessionKeys":["agent:main:main"]}""")]
    public async Task TranscriptSearch_FixtureRejectsUnsupportedOrInvalidContracts(string json)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var parameters = System.Text.Json.JsonDocument.Parse(json);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connected.Client.SendWizardRequestAsync("sessions.search", parameters.RootElement));
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task TranscriptSearch_BeforeConnectDoesNotReachWire()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        using var directory = new OpenClaw.TestSupport.TempDirectory();
        using var client = new OpenClawGatewayClient(server.Endpoint.ToString(), token, identityPath: directory.Path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SearchSessionsAsync(new("lighthouse")));
        Assert.DoesNotContain(server.Requests, r => r.Method == "sessions.search");
    }
}
