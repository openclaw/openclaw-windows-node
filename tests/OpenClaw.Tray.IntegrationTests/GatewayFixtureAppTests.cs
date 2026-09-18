using System.Text.Json;
using OpenClaw.GatewayFixtureHost;

namespace OpenClaw.Tray.IntegrationTests;

public sealed class GatewayFixtureAppFactAttribute : FactAttribute
{
    public GatewayFixtureAppFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_RUN_GATEWAY_FIXTURE_UI") != "1")
            Skip = "Run scripts\\test-gateway-fixture.ps1 with an explicitly built app.";
    }
}

[Collection("Gateway fixture environment")]
public sealed class GatewayFixtureAppTests
{
    [GatewayFixtureAppFact]
    public async Task RealOperatorPopulatesSessionsWithoutEnablingNodeExecution()
    {
        await using var run = await StartAsync();
        try
        {
            var status = await run.InvokeAsync("app.status");
            Assert.Equal("Connected", status.GetProperty("operatorState").GetString());
            Assert.False(status.GetProperty("nodeConnected").GetBoolean());
            Assert.True(status.GetProperty("sessionCount").GetInt32() >= 5);
            Assert.False((await run.InvokeAsync("app.settings.get", new { name = "EnableNodeMode" })).GetBoolean());
            using var discovery = await run.Client.ListToolsAsync();
            var tools = discovery.RootElement.GetProperty("result").GetProperty("tools")
                .EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).ToArray();
            Assert.Contains("app.chat.snapshot", tools);
            Assert.DoesNotContain("system.run", tools);
            Assert.DoesNotContain("camera.snap", tools);
            Assert.DoesNotContain("browser.proxy", tools);
            var sessions = await run.InvokeAsync("app.sessions");
            Assert.True(sessions.GetArrayLength() >= 5);
            using var autoStart = await run.Client.CallToolAsync("app.settings.set", new { name = "AutoStart", value = "true" });
            Assert.True(autoStart.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
            Assert.False((await run.InvokeAsync("app.settings.get", new { name = "AutoStart" })).GetBoolean());
            Assert.Empty(run.Gateway.UnexpectedRequests);
            await run.WriteReportAsync("passed");
        }
        catch (Exception ex)
        {
            await run.WriteReportAsync("failed", ex);
            throw;
        }
    }

    [GatewayFixtureAppFact]
    public async Task ConcurrentAppsHaveIndependentProfilesMcpTokensAndPreferences()
    {
        var starts = new[] { StartAsync(), StartAsync() };
        try
        {
            var runs = await Task.WhenAll(starts);
            var first = runs[0];
            var second = runs[1];
            Assert.NotEqual(first.AppProcessId, second.AppProcessId);
            Assert.NotEqual(first.McpPort, second.McpPort);
            Assert.NotEqual(first.Gateway.Endpoint, second.Gateway.Endpoint);
            Assert.NotEqual(first.Profile.GatewayId, second.Profile.GatewayId);
            Assert.NotEqual(first.Profile.DataDirectory, second.Profile.DataDirectory);
            await first.InvokeAsync("app.settings.set", new { name = "NotifyInfo", value = "false" });
            Assert.False((await first.InvokeAsync("app.settings.get", new { name = "NotifyInfo" })).GetBoolean());
            Assert.True((await second.InvokeAsync("app.settings.get", new { name = "NotifyInfo" })).GetBoolean());
            var firstToken = (await File.ReadAllTextAsync(Path.Combine(first.Profile.DataDirectory, "mcp-token.txt"))).Trim();
            using var wrongClient = new McpClient($"http://127.0.0.1:{second.McpPort}/mcp", firstToken);
            await Assert.ThrowsAsync<HttpRequestException>(async () =>
            {
                using var response = await wrongClient.ListToolsAsync();
            });
            await first.WriteReportAsync("passed");
            await first.DisposeAsync();
            second.EnsureRunning();
            Assert.True((await second.InvokeAsync("app.sessions")).GetArrayLength() >= 5);
            using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(second.Profile.DataDirectory, "settings.json")));
            Assert.True(persisted.RootElement.GetProperty("NotifyInfo").GetBoolean());
            await second.WriteReportAsync("passed");
        }
        finally
        {
            foreach (var start in starts)
                if (start.IsCompletedSuccessfully)
                    await start.Result.DisposeAsync();
        }
    }

    private static Task<GatewayFixtureRun> StartAsync() =>
        GatewayFixtureRun.StartAsync(
            Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_APP")
                ?? throw new InvalidOperationException("An explicit current app build is required."),
            Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS"));
}
