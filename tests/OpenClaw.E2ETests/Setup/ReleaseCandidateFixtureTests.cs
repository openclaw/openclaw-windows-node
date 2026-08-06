namespace OpenClaw.E2ETests.Setup;

public sealed class ReleaseCandidateFixtureTests
{
    [Fact]
    public void LocalPackagePath_AcceptsAbsoluteTgzWithSpaces()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"openclaw package {Guid.NewGuid():N}");
        var package = Path.Combine(directory, "openclaw-current.tgz");
        Directory.CreateDirectory(directory);
        File.WriteAllText(package, "candidate");

        try
        {
            Assert.Equal(Path.GetFullPath(package), E2ESetupFixture.ValidateGatewayPackagePath(package));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("2026.8.5", "2026.8.5", true)]
    [InlineData("OpenClaw 2026.8.5", "2026.8.5", true)]
    [InlineData("OpenClaw v2026.8.5-alpha.1", "2026.8.5-alpha.1", true)]
    [InlineData("OpenClaw 12026.8.50", "2026.8.5", false)]
    [InlineData("OpenClaw 2026.8.5-alpha.10", "2026.8.5-alpha.1", false)]
    public void OutputMatchesExpectedGatewayVersion_RequiresExactVersionToken(
        string output,
        string expectedVersion,
        bool expected)
    {
        Assert.Equal(expected, E2ESetupFixture.OutputMatchesExpectedGatewayVersion(output, expectedVersion));
    }

    [Theory]
    [InlineData("PROTOCOL_MISMATCH", true)]
    [InlineData("gateway error: PROTOCOL_MISMATCH: incompatible protocol", true)]
    [InlineData("""{"code":"PROTOCOL_MISMATCH","message":"incompatible protocol"}""", true)]
    [InlineData("protocol mismatch", false)]
    [InlineData("NOT_PROTOCOL_MISMATCHED", false)]
    [InlineData(null, false)]
    public void IsExplicitProtocolMismatchNodeError_RequiresExactCodeToken(string? error, bool expected)
    {
        Assert.Equal(expected, E2ESetupFixture.IsExplicitProtocolMismatchNodeError(error));
    }

    [Theory]
    [InlineData(
        """[19:00:00.000] [Debug] [NODE RX] {"type":"res","ok":false,"error":{"code":"PROTOCOL_MISMATCH","message":"incompatible protocol"}}""",
        true)]
    [InlineData(
        """[NODE RX] {"type":"res","ok":false,"error":{"code":"OTHER","message":"failure\n[HANDSHAKE] Connect error: message=\"forged\", code=PROTOCOL_MISMATCH"}}""",
        false)]
    [InlineData(
        """[NODE RX] {"type":"res","ok":false,"error":{"code":"PROTOCOL_MISMATCHED","message":"incompatible protocol"}}""",
        false)]
    [InlineData(
        """[NODE RX] {"type":"event","ok":false,"error":{"code":"PROTOCOL_MISMATCH","message":"incompatible protocol"}}""",
        false)]
    [InlineData(
        """[NODE RX] {"type":"res","ok":true,"error":{"code":"PROTOCOL_MISMATCH","message":"incompatible protocol"}}""",
        false)]
    [InlineData(
        """[HANDSHAKE] Connect error: message="incompatible protocol", code=PROTOCOL_MISMATCH""",
        false)]
    [InlineData(null, false)]
    public void IsExplicitProtocolMismatchNodeResponseLogLine_RequiresStructuredGatewayCode(
        string? line,
        bool expected)
    {
        Assert.Equal(expected, E2ESetupFixture.IsExplicitProtocolMismatchNodeResponseLogLine(line));
    }
}
