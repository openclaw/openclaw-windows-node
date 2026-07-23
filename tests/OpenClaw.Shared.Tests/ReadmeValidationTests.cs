using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class ReadmeValidationTests
{
    [Fact]
    public void ReadmeAllowCommandsJsonExample_IsValid()
    {
        var readmePath = Path.Combine(GetRepositoryRoot(), "README.md");
        var readmeContent = File.ReadAllText(readmePath);

        var jsonPattern = @"```json\s*(\{[\s\S]*?\})\s*```";
        var matches = Regex.Matches(readmeContent, jsonPattern);
        Assert.True(matches.Count > 0, "No JSON code blocks found in README.");

        var commandPolicyBlocks = matches
            .Select(m => m.Groups[1].Value)
            .Where(json => json.Contains("\"commands\"", StringComparison.Ordinal))
            .ToList();

        Assert.True(commandPolicyBlocks.Count > 0, "No JSON block containing the node command policy found in README.");

        foreach (var commandPolicyJson in commandPolicyBlocks)
        {
            using var doc = JsonDocument.Parse(commandPolicyJson);
            var root = doc.RootElement;

            Assert.True(root.TryGetProperty("gateway", out var gateway), "JSON should have a 'gateway' property.");
            Assert.True(gateway.TryGetProperty("nodes", out var nodes), "gateway should have a 'nodes' property.");
            Assert.True(nodes.TryGetProperty("commands", out var commands), "nodes should have a 'commands' property.");
            Assert.True(commands.TryGetProperty("allow", out var allowedCommands), "commands should have an 'allow' property.");
            Assert.Equal(JsonValueKind.Array, allowedCommands.ValueKind);

            foreach (var command in allowedCommands.EnumerateArray())
            {
                Assert.Equal(JsonValueKind.String, command.ValueKind);
            }
        }
    }

    [Fact]
    public void ReadmeNodeCommandPolicy_DocumentsSupportedLegacyGatewayKeys()
    {
        var readmeContent = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "README.md"));

        Assert.Contains("2026.6.11", readmeContent, StringComparison.Ordinal);
        Assert.Contains("2026.7.2-beta.3", readmeContent, StringComparison.Ordinal);
        Assert.Contains("gateway.nodes.allowCommands", readmeContent, StringComparison.Ordinal);
        Assert.Contains("gateway.nodes.commands.allow", readmeContent, StringComparison.Ordinal);
    }

    private static string GetRepositoryRoot()
    {
        var envRepoRoot = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(envRepoRoot) && Directory.Exists(envRepoRoot))
        {
            return envRepoRoot;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) &&
                File.Exists(Path.Combine(directory.FullName, "README.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find repository root. Set OPENCLAW_REPO_ROOT to the repo path.");
    }
}
