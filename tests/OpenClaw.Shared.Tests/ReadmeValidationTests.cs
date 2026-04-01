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

        var allowCommandsBlocks = matches
            .Select(m => m.Groups[1].Value)
            .Where(json => json.Contains("\"allowCommands\"", StringComparison.Ordinal))
            .ToList();

        Assert.True(allowCommandsBlocks.Count > 0, "No JSON block containing 'allowCommands' found in README.");

        foreach (var allowCommandsJson in allowCommandsBlocks)
        {
            using var doc = JsonDocument.Parse(allowCommandsJson);
            var root = doc.RootElement;

            Assert.True(root.TryGetProperty("gateway", out var gateway), "JSON should have a 'gateway' property.");
            Assert.True(gateway.TryGetProperty("nodes", out var nodes), "gateway should have a 'nodes' property.");
            Assert.True(nodes.TryGetProperty("allowCommands", out var allowCommands), "nodes should have an 'allowCommands' property.");
            Assert.Equal(JsonValueKind.Array, allowCommands.ValueKind);

            foreach (var command in allowCommands.EnumerateArray())
            {
                Assert.Equal(JsonValueKind.String, command.ValueKind);
            }
        }
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
