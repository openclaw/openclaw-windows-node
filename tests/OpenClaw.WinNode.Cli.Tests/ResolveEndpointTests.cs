using OpenClaw.WinNode.Cli;

namespace OpenClaw.WinNode.Cli.Tests;

public class ResolveEndpointTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
    {
        var dict = pairs.ToDictionary(p => p.Key, p => p.Value);
        return key => dict.TryGetValue(key, out var v) ? v : null;
    }

    private static string Resolve(WinNodeOptions opts, Func<string, string?> env)
        => CliRunner.ResolveEndpoint(opts, env, TextWriter.Null);

    [Fact]
    public void Default_port_when_nothing_set()
    {
        var endpoint = Resolve(new WinNodeOptions(), Env());
        Assert.Equal("http://127.0.0.1:8765/", endpoint);
    }

    [Fact]
    public void Honors_OPENCLAW_MCP_PORT_env()
    {
        var endpoint = Resolve(
            new WinNodeOptions(),
            Env(("OPENCLAW_MCP_PORT", "9100")));
        Assert.Equal("http://127.0.0.1:9100/", endpoint);
    }

    [Fact]
    public void Mcp_port_flag_wins_over_env()
    {
        var endpoint = Resolve(
            new WinNodeOptions { McpPortOverride = 9200 },
            Env(("OPENCLAW_MCP_PORT", "9100")));
        Assert.Equal("http://127.0.0.1:9200/", endpoint);
    }

    [Fact]
    public void Mcp_url_flag_wins_over_everything()
    {
        var endpoint = Resolve(
            new WinNodeOptions
            {
                McpUrlOverride = "http://example.test:1234/mcp",
                McpPortOverride = 9999,
            },
            Env(("OPENCLAW_MCP_PORT", "9100")));
        Assert.Equal("http://example.test:1234/mcp", endpoint);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("65536")]   // F-19: out of range -> default
    [InlineData("999999")]  // F-19: out of range -> default
    public void Invalid_env_falls_back_to_default(string envValue)
    {
        var endpoint = Resolve(
            new WinNodeOptions(),
            Env(("OPENCLAW_MCP_PORT", envValue)));
        Assert.Equal("http://127.0.0.1:8765/", endpoint);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("65535")]
    public void Boundary_env_ports_accepted(string envValue)
    {
        var endpoint = Resolve(
            new WinNodeOptions(),
            Env(("OPENCLAW_MCP_PORT", envValue)));
        Assert.Equal($"http://127.0.0.1:{envValue}/", endpoint);
    }

    [Fact]
    public void Verbose_warns_when_env_port_out_of_range()
    {
        var stderr = new StringWriter();
        var endpoint = CliRunner.ResolveEndpoint(
            new WinNodeOptions { Verbose = true },
            Env(("OPENCLAW_MCP_PORT", "70000")),
            stderr);
        Assert.Equal("http://127.0.0.1:8765/", endpoint);
        Assert.Contains("OPENCLAW_MCP_PORT", stderr.ToString());
        Assert.Contains("out of range", stderr.ToString());
    }

    [Fact]
    public void Whitespace_url_override_falls_through_to_port_resolution()
    {
        var endpoint = Resolve(
            new WinNodeOptions { McpUrlOverride = "   " },
            Env());
        Assert.Equal("http://127.0.0.1:8765/", endpoint);
    }
}
