namespace OpenClaw.MSIXHost.Tests;

public sealed class HostOptionsTests
{
    [Fact]
    public void ParseSeparatesHostOptionsFromOpenClawArguments()
    {
        HostOptions options = HostOptions.Parse(
        [
            "--host-payload", "payload.tar.gz",
            "--host-node", "test-node.exe",
            "gateway", "run", "--port", "12345"
        ]);

        Assert.Equal("payload.tar.gz", options.PayloadPath);
        Assert.Equal("test-node.exe", options.NodePath);
        Assert.False(options.VerifyInstalledPayload);
        Assert.Equal(
            ["gateway", "run", "--port", "12345"],
            options.OpenClawArguments);
    }

    [Fact]
    public void ParseEnablesFullInstalledPayloadVerification()
    {
        HostOptions options = HostOptions.Parse(
            ["--host-verify-installed-payload", "setup"]);

        Assert.True(options.VerifyInstalledPayload);
        Assert.Equal(["setup"], options.OpenClawArguments);
    }

    [Fact]
    public void ParseStopsHostOptionProcessingAfterSeparator()
    {
        HostOptions options = HostOptions.Parse(
            ["--", "--host-node", "forwarded"]);

        Assert.Equal(["--host-node", "forwarded"], options.OpenClawArguments);
    }

    [Fact]
    public void ParseRejectsHostOptionWithoutValue()
    {
        Assert.Throws<HostUsageException>(
            () => HostOptions.Parse(["--host-payload"]));
    }
}
