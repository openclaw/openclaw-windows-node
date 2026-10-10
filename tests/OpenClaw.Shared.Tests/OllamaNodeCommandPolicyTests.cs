using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public sealed class OllamaNodeCommandPolicyTests
{
    [Fact]
    public void ClassifiesCommands()
    {
        Assert.Contains("ollama.models", OllamaNodeCommandPolicy.ReadOnlyCommands);
        Assert.Contains(
            "ollama.models",
            (IReadOnlySet<string>)OllamaNodeCommandPolicy.ReadOnlyCommandSet);
        Assert.Contains("ollama.chat", OllamaNodeCommandPolicy.SensitiveCommands);
        Assert.Contains(
            "ollama.chat",
            (IReadOnlySet<string>)OllamaNodeCommandPolicy.SensitiveCommandSet);
        Assert.Contains("ollama.chat", CommandCenterCommandGroups.DangerousCommands);
        Assert.Contains(
            "ollama.chat",
            (IReadOnlySet<string>)CommandCenterCommandGroups.DangerousCommandSet);
        Assert.DoesNotContain("ollama.models", CommandCenterCommandGroups.SafeCompanionCommands);
        Assert.DoesNotContain("ollama.models", CommandCenterCommandGroups.DangerousCommands);
        Assert.DoesNotContain("ollama.chat", CommandCenterCommandGroups.MacNodeParityCommands);
        Assert.DoesNotContain("ollama.models", CommandCenterCommandGroups.MacNodeParityCommands);
    }

    [Fact]
    public void AllowedModelsCommand_AppearsInSafeApprovedCommands()
    {
        var node = new GatewayNodeInfo
        {
            NodeId = "node-1",
            Platform = "windows",
            Commands = ["ollama.models"],
            Permissions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["ollama.models"] = true,
            },
        };

        var info = NodeCapabilityHealthInfo.FromNode(node);

        Assert.Contains("ollama.models", info.SafeApprovedCommands);
        Assert.DoesNotContain("ollama.models", info.MissingSafeAllowlistCommands);
    }

    [Fact]
    public void BlockedModelsCommand_AppearsInMissingSafeAllowlistCommands()
    {
        var node = new GatewayNodeInfo
        {
            NodeId = "node-1",
            Platform = "windows",
            Commands = ["ollama.models"],
            Permissions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["ollama.models"] = false,
            },
        };

        var info = NodeCapabilityHealthInfo.FromNode(node);

        Assert.Contains("ollama.models", info.MissingSafeAllowlistCommands);
    }
}
