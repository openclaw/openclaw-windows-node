using System.Text.Json;

namespace OpenClaw.E2ETests.Setup;

/// <summary>
/// Defines the xUnit test collection that shares the E2ESetupFixture.
/// All tests in this collection run against a single setup pipeline execution.
/// </summary>
[CollectionDefinition("E2E Setup")]
public class E2ESetupCollection : ICollectionFixture<E2ESetupFixture> { }

/// <summary>
/// Validates that a headless first-time setup produces a working tray
/// with connected operator and node, verified via MCP tool calls.
/// </summary>
[Collection("E2E Setup")]
public class SetupAndConnectTests
{
    private readonly E2ESetupFixture _fixture;

    public SetupAndConnectTests(E2ESetupFixture fixture)
    {
        _fixture = fixture;

        // Fail fast if the fixture didn't initialize cleanly
        if (_fixture.SetupError is not null)
            throw new InvalidOperationException($"E2E setup failed: {_fixture.SetupError}");
        if (_fixture.Client is null)
            throw new InvalidOperationException("E2E fixture MCP client not initialized");
    }

    [Fact]
    public async Task FullSetup_TrayConnects_OperatorAndNode()
    {
        // Call app.status and verify the tray is fully connected
        using var doc = await _fixture.Client!.CallToolExpectSuccessAsync("app.status");
        var root = doc.RootElement;

        // Log full response for debugging
        var rawJson = root.GetRawText();
        Console.WriteLine($"[E2E] app.status response: {rawJson}");

        var connectionStatus = root.GetProperty("connectionStatus").GetString();
        Assert.True(connectionStatus is "Ready" or "Connected",
            $"connectionStatus should be Ready or Connected, got '{connectionStatus}'; full status: {rawJson}");

        var nodeConnected = root.GetProperty("nodeConnected").GetBoolean();
        Assert.True(nodeConnected, $"nodeConnected should be true; full status: {rawJson}");

        var nodePaired = root.GetProperty("nodePaired").GetBoolean();
        Assert.True(nodePaired, $"nodePaired should be true; full status: {rawJson}");
    }

    [Fact]
    public async Task FullSetup_NodeCapabilities_Propagated()
    {
        // Call app.nodes and verify at least one node with capabilities
        using var doc = await _fixture.Client!.CallToolExpectSuccessAsync("app.nodes");
        var root = doc.RootElement;

        // Log full response for debugging
        var rawJson = root.GetRawText();
        Console.WriteLine($"[E2E] app.nodes response: {rawJson}");

        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.True(root.GetArrayLength() >= 1,
            $"Expected at least 1 node, got {root.GetArrayLength()}; response: {rawJson}");

        var firstNode = root[0];
        var capCount = firstNode.GetProperty("CapabilityCount").GetInt32();
        Assert.True(capCount > 0,
            $"Expected CapabilityCount > 0, got {capCount}; node: {firstNode.GetRawText()}");

        var isOnline = firstNode.GetProperty("IsOnline").GetBoolean();
        Assert.True(isOnline,
            $"Expected node IsOnline=true; node: {firstNode.GetRawText()}");
    }
}
