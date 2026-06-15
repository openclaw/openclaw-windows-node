using System.Text.Json;
using OpenClaw.E2ETests;

namespace OpenClaw.E2ETests.Setup;

[CollectionDefinition("E2E Network Recovery", DisableParallelization = true)]
public class E2ENetworkRecoveryCollection : ICollectionFixture<E2ESetupFixture> { }

[Collection("E2E Network Recovery")]
public class NetworkRecoveryTests
{
    private readonly E2ESetupFixture _fixture;

    public NetworkRecoveryTests(E2ESetupFixture fixture)
    {
        _fixture = fixture;
        if (_fixture.SetupError is not null)
            throw new InvalidOperationException($"E2E setup failed: {_fixture.SetupError}");
        if (_fixture.Client is null)
            throw new InvalidOperationException("E2E fixture MCP client not initialized");
    }

    [E2EFact]
    public async Task GatewayStopAndStart_TrayLeavesReadyThenRecovers()
    {
        using (var initialStatus = await _fixture.Client!.CallToolExpectSuccessAsync("app.status"))
        {
            AssertReadyStatus(initialStatus.RootElement);
        }

        var stop = await _fixture.RunInWslAsync(
            "systemctl --user stop openclaw-gateway.service",
            TimeSpan.FromSeconds(30));
        AssertCommandSucceeded(stop, "stop WSL gateway service");

        try
        {
            await WaitForNotReadyAsync();
        }
        finally
        {
            var start = await _fixture.RunInWslAsync(
                "openclaw gateway start || systemctl --user start openclaw-gateway.service",
                TimeSpan.FromSeconds(60));
            AssertCommandSucceeded(start, "restart WSL gateway service");
        }

        await _fixture.WaitForConnectionReady(TimeSpan.FromSeconds(120));
        await _fixture.WaitForNodeListReady(TimeSpan.FromSeconds(90));
        using var recoveredStatus = await _fixture.Client!.CallToolExpectSuccessAsync("app.status");
        AssertReadyStatus(recoveredStatus.RootElement);
    }

    [E2EFact]
    public async Task RepeatedGatewayRestart_TrayAndNodeRecoverEachTime()
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var restart = await _fixture.RunInWslAsync(
                "openclaw gateway restart || (systemctl --user restart openclaw-gateway.service && echo restarted-via-systemctl)",
                TimeSpan.FromSeconds(60));
            AssertCommandSucceeded(restart, $"restart WSL gateway service, attempt {attempt}");
            Console.WriteLine($"[E2E] gateway restart attempt {attempt} output:\n{restart.Stdout}");

            await _fixture.WaitForConnectionReady(TimeSpan.FromSeconds(120));
            await _fixture.WaitForNodeListReady(TimeSpan.FromSeconds(90));
            using var statusDoc = await _fixture.Client!.CallToolExpectSuccessAsync("app.status");
            AssertReadyStatus(statusDoc.RootElement);
        }
    }

    private async Task WaitForNotReadyAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        string lastStatus = "<none>";
        while (DateTime.UtcNow < deadline)
        {
            using var statusDoc = await _fixture.Client!.CallToolExpectSuccessAsync("app.status");
            var root = statusDoc.RootElement;
            lastStatus = root.GetRawText();
            var connectionStatus = root.GetProperty("connectionStatus").GetString();
            var nodeConnected = root.TryGetProperty("nodeConnected", out var node) && node.GetBoolean();
            if (connectionStatus is not ("Ready" or "Connected") || !nodeConnected)
                return;

            await Task.Delay(1000);
        }

        throw new TimeoutException($"Tray stayed ready after gateway stop. Last status: {lastStatus}");
    }

    private static void AssertCommandSucceeded(OpenClaw.SetupEngine.CommandResult result, string description)
    {
        Assert.False(result.TimedOut, $"{description} timed out");
        Assert.Equal(0, result.ExitCode);
    }

    private static void AssertReadyStatus(JsonElement root)
    {
        var rawJson = root.GetRawText();
        var connectionStatus = root.GetProperty("connectionStatus").GetString();
        Assert.True(connectionStatus is "Ready" or "Connected",
            $"connectionStatus should be Ready or Connected, got '{connectionStatus}'; full status: {rawJson}");
        Assert.True(root.GetProperty("nodeConnected").GetBoolean(), $"nodeConnected should be true; full status: {rawJson}");
        Assert.True(root.GetProperty("nodePaired").GetBoolean(), $"nodePaired should be true; full status: {rawJson}");
    }
}
