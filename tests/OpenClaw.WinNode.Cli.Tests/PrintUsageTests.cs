using OpenClaw.WinNode.Cli;

namespace OpenClaw.WinNode.Cli.Tests;

public class PrintUsageTests
{
    [Fact]
    public void Prints_every_supported_flag()
    {
        var w = new StringWriter();
        CliRunner.PrintUsage(w);
        var text = w.ToString();

        Assert.Contains("--node", text);
        Assert.Contains("--command", text);
        Assert.Contains("--params", text);
        Assert.Contains("--invoke-timeout", text);
        Assert.Contains("--idempotency-key", text);
        Assert.Contains("--mcp-url", text);
        Assert.Contains("--mcp-port", text);
        Assert.Contains("--mcp-token", text);
        Assert.Contains("--verbose", text);
        Assert.Contains("--help", text);
        Assert.Contains("skill.md", text);
    }
}
