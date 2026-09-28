namespace OpenClaw.Connection.Tests;

public sealed class GatewayTerminalLaunchCommandBuilderTests
{
    [Fact]
    public void Build_RejectsSshUserThatLooksLikeAnOpenSshOption()
    {
        var access = SshAccess("-oProxyCommand=calc", "gateway.example.test");

        var error = Assert.Throws<ArgumentException>(() =>
            GatewayTerminalLaunchCommandBuilder.Build(access, windowsTerminalPath: null));

        Assert.Equal("user", error.ParamName);
    }

    [Theory]
    [InlineData("alice", "-oProxyCommand=calc")]
    [InlineData("-alice", "gateway.example.test")]
    [InlineData("alice", "-gateway")]
    [InlineData("bad user", "gateway.example.test")]
    [InlineData("alice", "bad host")]
    public void Build_RejectsUnsafeSshUserOrHost(string user, string host)
    {
        var access = SshAccess(user, host);

        Assert.Throws<ArgumentException>(() =>
            GatewayTerminalLaunchCommandBuilder.Build(access, windowsTerminalPath: null));
    }

    [Fact]
    public void Build_AllowsCharsetSafeSshUserAndHost()
    {
        var access = SshAccess("alice-bob", "mac-mini.local");

        var command = GatewayTerminalLaunchCommandBuilder.Build(access, windowsTerminalPath: null);

        Assert.Equal("ssh.exe", command.FileName);
        Assert.Equal(["alice-bob@mac-mini.local"], command.Arguments);
    }

    private static GatewayHostAccessPlan SshAccess(string user, string host) =>
        GatewayHostAccessClassifier.Classify(new GatewayRecord
        {
            Id = "ssh",
            Url = "ws://127.0.0.1:18789",
            SshTunnel = new SshTunnelConfig(user, host, 18789, 18790),
        });
}
