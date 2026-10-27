using System.Text.Json;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Shared.Tests;

public sealed partial class GatewayFixtureProtocolTests
{
    [Fact]
    public async Task SkillsOwner_AcceptedMutationRequiresExplicitScopedRefresh()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateSkillsEditing(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        JsonElement? report = null;
        client.SkillsStatusUpdated += (_, value) => report = value;
        await client.RequestSkillsStatusAsync("research");
        Assert.False(report!.Value.GetProperty("skills")[0].GetProperty("disabled").GetBoolean());

        Assert.True(await client.SetSkillEnabledAsync("fixture-calendar", false));
        Assert.False(report.Value.GetProperty("skills")[0].GetProperty("disabled").GetBoolean());
        await client.RequestSkillsStatusAsync("research");

        Assert.True(report.Value.GetProperty("skills")[0].GetProperty("disabled").GetBoolean());
        Assert.Equal("research", report.Value.GetProperty("agentId").GetString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendWizardRequestAsync("skills.update",
            new { skillKey = "fixture-calendar", enabled = true, agentId = "research" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendWizardRequestAsync("skills.update",
            new { skillKey = "unknown", enabled = true }));
        Assert.DoesNotContain(server.Requests, row => row.Method == "skills.install");
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task SkillsOwner_ReadsScopedStatusAndCannotTurnFixtureRejectionIntoEnabledState()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        JsonElement? report = null;
        client.SkillsStatusUpdated += (_, value) => report = value;

        await client.RequestSkillsStatusAsync("research");

        Assert.Equal("research", report!.Value.GetProperty("agentId").GetString());
        var before = report.Value.GetRawText();
        Assert.False(await client.SetSkillEnabledAsync("fixture-calendar", false));
        await client.RequestSkillsStatusAsync("research");
        Assert.Equal(before, report.Value.GetRawText());
        Assert.Equal(2, report.Value.GetProperty("skills").GetArrayLength());
        Assert.Contains(server.Requests, request => request.Method == "skills.update" && request.Outcome == "error:FIXTURE_READ_ONLY");
        Assert.Empty(server.UnexpectedRequests);
    }
}
