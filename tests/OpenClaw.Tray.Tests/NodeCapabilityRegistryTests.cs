using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.Shared.Codex;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class NodeCapabilityRegistryTests
{
    private static readonly string[] ExpectedReadCommands =
    [
        "codex.appServer.threads.list.v1",
        "codex.appServer.thread.turns.list.v1",
    ];

    [Fact]
    public void Rebuild_Off_DoesNotAdvertiseCodexCommands()
    {
        var registry = CreateRegistry(clientAvailable: true);

        var snapshot = registry.Rebuild([], CodexSessionAccessMode.Off);

        Assert.Empty(CodexCommands(snapshot));
    }

    [Fact]
    public void Rebuild_ReadOnlyWithAvailableClient_AdvertisesExactlyTwoReadCommands()
    {
        var registry = CreateRegistry(clientAvailable: true);

        var snapshot = registry.Rebuild([], CodexSessionAccessMode.ReadOnly);

        Assert.Equal(ExpectedReadCommands, CodexCommands(snapshot));
    }

    [Fact]
    public void Rebuild_ReadOnlyWithUnavailableClient_DoesNotAdvertiseCodexCommands()
    {
        var registry = CreateRegistry(clientAvailable: false);

        var snapshot = registry.Rebuild([], CodexSessionAccessMode.ReadOnly);

        Assert.Empty(CodexCommands(snapshot));
    }

    [Fact]
    public void Rebuild_ReadAndSteerWithoutOwnerEndpoint_StillAdvertisesOnlyTwoReads()
    {
        var registry = CreateRegistry(clientAvailable: true);

        var snapshot = registry.Rebuild([], CodexSessionAccessMode.ReadAndSteer);

        Assert.Equal(ExpectedReadCommands, CodexCommands(snapshot));
        Assert.DoesNotContain(snapshot.SelectMany(capability => capability.Commands), command =>
            command.Contains("steer", StringComparison.OrdinalIgnoreCase)
            || command.Contains("interrupt", StringComparison.OrdinalIgnoreCase)
            || command.Contains("resume", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SharedSnapshot_McpAndGatewayReceiveSameImmutableRegistrySnapshot()
    {
        var baseline = new StubCapability("system", ["system.notify"]);
        var registry = CreateRegistry(clientAvailable: true);

        var rebuilt = registry.Rebuild([baseline], CodexSessionAccessMode.ReadOnly);
        var gateway = registry.GetGatewaySnapshot();
        var mcp = registry.GetMcpSnapshot();

        Assert.Same(rebuilt, gateway);
        Assert.Same(gateway, mcp);
        var collection = Assert.IsAssignableFrom<ICollection<INodeCapability>>(rebuilt);
        Assert.True(collection.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => collection.Add(new StubCapability("write", ["thread.resume"])));
    }

    [Fact]
    public void NodeService_DoesNotOwnCapabilityRegistryStorageOrRegistration()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "NodeService.cs"));

        Assert.Contains("NodeCapabilityRegistry", source);
        Assert.DoesNotContain("List<INodeCapability> _capabilities", source);
        Assert.DoesNotContain("void Register(INodeCapability capability)", source);
    }

    private static NodeCapabilityRegistry CreateRegistry(bool clientAvailable) =>
        new(() => clientAvailable ? new StubCapability("codex-app-server-threads", ExpectedReadCommands) : null);

    private static string[] CodexCommands(IReadOnlyList<INodeCapability> snapshot) =>
        snapshot
            .Where(capability => string.Equals(
                capability.Category,
                "codex-app-server-threads",
                StringComparison.Ordinal))
            .SelectMany(capability => capability.Commands)
            .ToArray();

    private sealed class StubCapability(string category, IReadOnlyList<string> commands) : INodeCapability
    {
        public string Category { get; } = category;

        public IReadOnlyList<string> Commands { get; } = commands;

        public bool CanHandle(string command) =>
            Commands.Contains(command, StringComparer.OrdinalIgnoreCase);

        public Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request) =>
            Task.FromResult(new NodeInvokeResponse
            {
                Ok = true,
                Payload = JsonSerializer.SerializeToElement(new { }),
            });
    }
}
