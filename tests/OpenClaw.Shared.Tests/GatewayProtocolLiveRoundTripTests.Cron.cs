using System.Text.Json;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed partial class GatewayProtocolLiveRoundTripTests
{
    [Theory]
    [InlineData("cron.add", true)]
    [InlineData("cron.add", false)]
    [InlineData("cron.update", true)]
    [InlineData("cron.update", false)]
    [InlineData("cron.remove", true)]
    [InlineData("cron.remove", false)]
    public async Task CronMutation_DoesNotReportSuccessBeforeGatewayAcceptance(string method, bool accept)
    {
        const int timeoutMs = 20000;
        using var server = new LoopbackGatewayServer();
        server.SilenceMethod(method);
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: _identityDir);
        await ConnectAndWaitAsync(client, server);
        var request = method switch
        {
            "cron.add" => client.AddCronJobAsync(new
            {
                name = "Test job", schedule = new { kind = "at", at = "2030-01-01T00:00:00.000Z" },
                deleteAfterRun = true, sessionTarget = "isolated", wakeMode = "now",
                payload = new { kind = "agentTurn", message = "Test prompt" }, delivery = new { mode = "none" }
            }),
            "cron.update" => client.UpdateCronJobAsync("job", new { enabled = false, deleteAfterRun = true }),
            _ => client.RemoveCronJobAsync("job")
        };
        using var frame = JsonDocument.Parse(await server.WaitFrameAsync(method, occurrence: 0, timeoutMs));
        var parameters = frame.RootElement.GetProperty("params");
        if (method != "cron.add")
        {
            Assert.Equal("job", parameters.GetProperty("id").GetString());
            Assert.False(parameters.TryGetProperty("jobId", out _));
        }
        if (method == "cron.update")
        {
            Assert.False(parameters.GetProperty("patch").GetProperty("enabled").GetBoolean());
            Assert.True(parameters.GetProperty("patch").GetProperty("deleteAfterRun").GetBoolean());
        }
        await client.SendWizardRequestAsync("health");
        Assert.False(request.IsCompleted);
        Assert.False(server.HasFrame("cron.list"));
        await server.SendTextToCurrentSocketAsync(JsonSerializer.Serialize(new
        {
            type = "res", id = frame.RootElement.GetProperty("id").GetString(), ok = accept,
            payload = new { ok = true },
            error = accept ? null : new { code = "MISSING_SCOPE", message = "missing scope: operator.admin" }
        }));
        Assert.Equal(accept, await request.WaitAsync(TimeSpan.FromSeconds(20)));
        if (accept) await server.WaitFrameAsync("cron.list", occurrence: 0, timeoutMs);
        else Assert.False(server.HasFrame("cron.list"));
        _output.WriteLine($"{method}: held response kept task incomplete; Gateway acceptance={accept}; list refresh={accept}");
        await client.DisconnectAsync();
    }

    [Fact]
    public async Task CronDueRun_LegacyFallbackNeverForcesTheJob()
    {
        using var server = new LoopbackGatewayServer();
        server.OnMethod("cron.run", parameters =>
        {
            if (parameters.TryGetProperty("jobId", out _))
                return LoopbackResponse.Fail("invalid cron.run params: unexpected property mode");
            Assert.Equal("job", parameters.GetProperty("id").GetString());
            Assert.False(parameters.GetProperty("force").GetBoolean());
            return new { ok = true, ran = false, reason = "not-due" };
        });
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: _identityDir);
        await ConnectAndWaitAsync(client, server);
        var result = await client.RunCronJobDetailedAsync("job", force: false);
        Assert.True(result.Accepted);
        Assert.False(result.Enqueued);
        Assert.Equal("not-due", result.Reason);
        using var first = JsonDocument.Parse(server.FrameFor("cron.run", 0));
        Assert.Equal("due", first.RootElement.GetProperty("params").GetProperty("mode").GetString());
        Assert.Throws<InvalidOperationException>(() => server.FrameFor("cron.run", 2));
        await client.DisconnectAsync();
    }
}
