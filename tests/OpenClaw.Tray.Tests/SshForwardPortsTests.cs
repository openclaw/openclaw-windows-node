using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class SshForwardPortsTests
{
    [Theory]
    [InlineData("", "18790", "remote")]
    [InlineData("0", "18790", "remote")]
    [InlineData("70000", "18790", "remote")]
    [InlineData("18789", "", "local")]
    [InlineData("18789", "0", "local")]
    [InlineData("18789", "70000", "local")]
    [InlineData("18789", "18790", "ok")]
    public void TryParse_RejectsBlankAndOutOfRangePorts(
        string remoteText,
        string localText,
        string expected)
    {
        var result = SshForwardPorts.TryParse(remoteText, localText, out var remotePort, out var localPort);
        var actual = result switch
        {
            SshForwardPortParseResult.RemoteInvalid => "remote",
            SshForwardPortParseResult.LocalInvalid => "local",
            _ => "ok",
        };

        Assert.Equal(expected, actual);
        if (expected == "ok")
        {
            Assert.Equal(18789, remotePort);
            Assert.Equal(18790, localPort);
        }
    }

    [Fact]
    public void StatusWindowDirectConnect_RejectsForwardPortsBeforeConnect()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "ConnectionStatusWindow.xaml.cs"));
        var method = ExtractMethod(source, "OnDirectConnectAsync");

        Assert.Contains("SshForwardPorts.TryParse(", method);
        Assert.Contains("ConnectionPage_SshRemotePortInvalid", method);
        Assert.Contains("ConnectionPage_SshLocalPortInvalid", method);
        Assert.DoesNotContain("remotePort = 18789", method);
        Assert.DoesNotContain("localPort = 18790", method);
        var parse = method.IndexOf("SshForwardPorts.TryParse(", StringComparison.Ordinal);
        var connect = method.IndexOf("directConnectService.ConnectAsync(", StringComparison.Ordinal);
        Assert.True(parse >= 0 && connect > parse);
    }

    private static string ExtractMethod(string source, string name)
    {
        var start = source.IndexOf(name, StringComparison.Ordinal);
        Assert.True(start >= 0, name);
        var brace = source.IndexOf('{', start);
        var depth = 0;
        for (var i = brace; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return source[start..(i + 1)];
            }
        }

        return source[start..];
    }
}
