using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class SshForwardPortsTests
{
    [Theory]
    [InlineData("", "18790", "remote")]
    [InlineData(null, "18790", "remote")]
    [InlineData(" ", "18790", "remote")]
    [InlineData("abc", "18790", "remote")]
    [InlineData("-1", "18790", "remote")]
    [InlineData("65536", "18790", "remote")]
    [InlineData("2147483648", "18790", "remote")]
    [InlineData("0", "18790", "remote")]
    [InlineData("70000", "18790", "remote")]
    [InlineData("18789", "", "local")]
    [InlineData("18789", null, "local")]
    [InlineData("18789", " ", "local")]
    [InlineData("18789", "abc", "local")]
    [InlineData("18789", "-1", "local")]
    [InlineData("18789", "65536", "local")]
    [InlineData("18789", "2147483648", "local")]
    [InlineData("", "", "remote")]
    [InlineData("18789", "0", "local")]
    [InlineData("18789", "70000", "local")]
    [InlineData("18789", "18790", "ok")]
    public void TryParse_RejectsBlankAndOutOfRangePorts(
        string? remoteText,
        string? localText,
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

    [Theory]
    [InlineData("1", "65535", 1, 65535)]
    [InlineData("65535", "1", 65535, 1)]
    [InlineData("18789", "18790", 18789, 18790)]
    [InlineData(" 18789 ", " 18790 ", 18789, 18790)]
    public void TryParse_AcceptsBoundariesAndPreservesPorts(
        string remoteText, string localText, int expectedRemote, int expectedLocal)
    {
        Assert.Equal(SshForwardPortParseResult.Ok,
            SshForwardPorts.TryParse(remoteText, localText, out var remotePort, out var localPort));
        Assert.Equal(expectedRemote, remotePort);
        Assert.Equal(expectedLocal, localPort);
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
        var method = ExtractMethod(source, "private async Task OnDirectConnectAsync()");

        Assert.Contains("SshForwardPorts.TryParse(", method);
        Assert.Contains("ConnectionPage_SshRemotePortInvalid", method);
        Assert.Contains("ConnectionPage_SshLocalPortInvalid", method);
        Assert.DoesNotContain("remotePort = 18789", method);
        Assert.DoesNotContain("localPort = 18790", method);
        var parse = method.IndexOf("SshForwardPorts.TryParse(", StringComparison.Ordinal);
        var connect = method.IndexOf("directConnectService.ConnectAsync(", StringComparison.Ordinal);
        Assert.True(parse >= 0 && connect > parse);
        var rejection = ExtractMethod(method, "if (forwardPorts != SshForwardPortParseResult.Ok)");
        Assert.Contains("DirectConnectResult.Text =", rejection);
        Assert.Contains("return;", rejection);
        Assert.True(method.IndexOf(rejection, StringComparison.Ordinal) < connect);
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
