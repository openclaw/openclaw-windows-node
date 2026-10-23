using System.Text.Json;

namespace OpenClaw.Shared.Tests;

public sealed partial class GatewayProtocolLiveRoundTripTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("research")]
    [InlineData("  ")]
    public async Task SkillsStatus_UsesAgentScopeAndPublishesAcknowledgedReport(string? agentId)
    {
        using var server = new LoopbackGatewayServer();
        server.OnMethod("skills.status", parameters => new
        {
            agentId = parameters.TryGetProperty("agentId", out var agent) ? agent.GetString() : "main",
            skills = new[] { new { name = "Calendar", skillKey = "calendar-key", disabled = false, eligible = true } }
        });
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: _identityDir);
        await ConnectAndWaitAsync(client, server);
        JsonElement? received = null;
        client.SkillsStatusUpdated += (_, data) => received = data;

        await client.RequestSkillsStatusAsync(agentId);

        var report = received!.Value;
        Assert.Equal(string.IsNullOrWhiteSpace(agentId) ? "main" : agentId, report.GetProperty("agentId").GetString());
        Assert.Equal("calendar-key", Assert.Single(report.GetProperty("skills").EnumerateArray()).GetProperty("skillKey").GetString());
        using var frame = JsonDocument.Parse(server.FrameFor("skills.status"));
        var parameters = frame.RootElement.GetProperty("params");
        Assert.Equal(string.IsNullOrWhiteSpace(agentId) ? 0 : 1, parameters.EnumerateObject().Count());
        if (!string.IsNullOrWhiteSpace(agentId))
            Assert.Equal(agentId, parameters.GetProperty("agentId").GetString());
        _output.WriteLine($"skills.status acknowledged report: {report.GetRawText()}");
        await client.DisconnectAsync();
    }

    [Fact]
    public async Task SkillsStatus_LatePreviousAgentResponseDoesNotReplaceSelectedScope()
    {
        using var server = new LoopbackGatewayServer();
        server.SilenceMethod("skills.status");
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: _identityDir);
        await ConnectAndWaitAsync(client, server);
        var reports = new List<string>();
        client.SkillsStatusUpdated += (_, data) => reports.Add(data.GetProperty("agentId").GetString()!);

        var older = client.RequestSkillsStatusAsync("main");
        using var first = JsonDocument.Parse(await server.WaitFrameAsync("skills.status", 0, 10_000));
        var newer = client.RequestSkillsStatusAsync("research");
        using var second = JsonDocument.Parse(await server.WaitFrameAsync("skills.status", 1, 10_000));
        await ReplyAsync(second, "research");
        await newer.WaitAsync(TimeSpan.FromSeconds(10));
        await ReplyAsync(first, "main");
        await older.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["research"], reports);
        await client.DisconnectAsync();

        Task ReplyAsync(JsonDocument request, string agentId) => server.SendTextToCurrentSocketAsync(
            JsonSerializer.Serialize(new
            {
                type = "res", id = request.RootElement.GetProperty("id").GetString(),
                ok = true, payload = new { agentId, skills = Array.Empty<object>() }
            }));
    }

    [Fact]
    public async Task SkillsStatus_GatewayRefusalPropagatesWithoutPublishingSuccess()
    {
        using var server = new LoopbackGatewayServer();
        server.OnMethod("skills.status", _ => LoopbackResponse.Fail("unknown agent"));
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: _identityDir);
        await ConnectAndWaitAsync(client, server);
        var published = false;
        client.SkillsStatusUpdated += (_, _) => published = true;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestSkillsStatusAsync("missing"));

        Assert.Equal("unknown agent", error.Message);
        Assert.False(published);
        await client.DisconnectAsync();
    }

    [Fact]
    public async Task SkillsUpdate_RejectionIsNotReportedAsSuccessfulToggle()
    {
        using var server = new LoopbackGatewayServer();
        server.OnMethod("skills.update", _ => LoopbackResponse.Fail("operator.admin required"));
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: _identityDir);
        await ConnectAndWaitAsync(client, server);

        Assert.False(await client.SetSkillEnabledAsync("calendar-key", false));
        using var frame = JsonDocument.Parse(server.FrameFor("skills.update"));
        Assert.Equal("""{"skillKey":"calendar-key","enabled":false}""",
            frame.RootElement.GetProperty("params").GetRawText());
        await client.DisconnectAsync();
    }

    [Fact]
    public async Task SkillsUpdate_DoesNotCompleteUntilGatewayAcknowledges()
    {
        using var server = new LoopbackGatewayServer();
        server.SilenceMethod("skills.update");
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: _identityDir);
        await ConnectAndWaitAsync(client, server);

        var update = client.SetSkillEnabledAsync("calendar-key", true);
        using var frame = JsonDocument.Parse(await server.WaitFrameAsync("skills.update", 0, 10_000));
        Assert.False(update.IsCompleted, "A socket send is not an accepted Gateway mutation.");
        await server.SendTextToCurrentSocketAsync(JsonSerializer.Serialize(new
        {
            type = "res", id = frame.RootElement.GetProperty("id").GetString(), ok = true,
            payload = new { ok = true, skillKey = "calendar-key" }
        }));
        Assert.True(await update.WaitAsync(TimeSpan.FromSeconds(10)));
        await client.DisconnectAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SkillsUpdate_UsesAcknowledgementRatherThanOptimisticState(bool accepted)
    {
        using var server = new LoopbackGatewayServer();
        server.OnMethod("skills.update", _ => new { ok = accepted, skillKey = "calendar-key" });
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: _identityDir);
        await ConnectAndWaitAsync(client, server);

        Assert.Equal(accepted, await client.SetSkillEnabledAsync("calendar-key", false));
        Assert.False(server.HasFrame("skills.status")); // The active UI scope owns refresh, not a global mutation callback.
        await client.DisconnectAsync();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("research", true)]
    [InlineData("research", false)]
    public async Task SkillsInstall_UsesAdvertisedRecipeContractAndActualAcknowledgement(string? agentId, bool accepted)
    {
        using var server = new LoopbackGatewayServer();
        server.OnMethod("skills.install", _ => accepted
            ? new { ok = true, message = "Installed dependency" }
            : (object)LoopbackResponse.Fail("install policy denied"));
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: _identityDir);
        await ConnectAndWaitAsync(client, server);

        Assert.Equal(accepted, await client.InstallSkillAsync("calendar", "node", agentId));

        using var frame = JsonDocument.Parse(server.FrameFor("skills.install"));
        var parameters = frame.RootElement.GetProperty("params");
        Assert.Equal("calendar", parameters.GetProperty("name").GetString());
        Assert.Equal("node", parameters.GetProperty("installId").GetString());
        Assert.Equal(agentId is null ? 2 : 3, parameters.EnumerateObject().Count());
        if (agentId is not null)
            Assert.Equal(agentId, parameters.GetProperty("agentId").GetString());
        Assert.False(parameters.TryGetProperty("id", out _));
        Assert.False(parameters.TryGetProperty("dangerouslyForceUnsafeInstall", out _));
        _output.WriteLine($"skills.install request: {parameters.GetRawText()}; accepted={accepted}. Contract-only: no production UI caller.");
        await client.DisconnectAsync();
    }

    [Theory]
    [InlineData("skills.status")]
    [InlineData("skills.update")]
    [InlineData("skills.install")]
    public async Task SkillsCalls_BeforeHelloOkDoNotReachGateway(string method)
    {
        using var server = new LoopbackGatewayServer(sendHandshakeChallenge: false);
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: _identityDir);
        await client.ConnectAsync();
        switch (method)
        {
            case "skills.status": await client.RequestSkillsStatusAsync("research"); break;
            case "skills.update": Assert.False(await client.SetSkillEnabledAsync("calendar-key", false)); break;
            case "skills.install": Assert.False(await client.InstallSkillAsync("calendar", "node")); break;
        }
        Assert.False(server.HasFrame(method));
        await client.DisconnectAsync();
    }

    [Fact]
    public async Task SkillsUpdate_DisconnectWhileAcknowledgementPendingIsNotSuccess()
    {
        using var server = new LoopbackGatewayServer();
        server.SilenceMethod("skills.update");
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: _identityDir);
        await ConnectAndWaitAsync(client, server);
        var update = client.SetSkillEnabledAsync("calendar-key", false);
        await server.WaitFrameAsync("skills.update", 0, 10_000);

        await client.DisconnectAsync();

        Assert.False(await update.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
