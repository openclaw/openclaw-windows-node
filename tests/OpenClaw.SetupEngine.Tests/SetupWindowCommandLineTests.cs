namespace OpenClaw.SetupEngine.Tests;

public sealed class SetupWindowCommandLineTests
{
    [Theory]
    [InlineData("--config", "custom.json")]
    [InlineData("--CONFIG", "custom.json")]
    public void TryParse_AcceptsSeparatedConfig(string option, string path)
    {
        var parsed = SetupWindowCommandLine.TryParse(
            [option, path],
            out var arguments,
            out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal(path, arguments.ConfigPath);
        Assert.True(arguments.RollbackOnFailure);
    }

    [Fact]
    public void TryParse_AcceptsEqualsConfig()
    {
        var parsed = SetupWindowCommandLine.TryParse(
            ["--config=custom.json"],
            out var arguments,
            out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("custom.json", arguments.ConfigPath);
    }

    [Fact]
    public void TryParse_AcceptsRollbackOptOut()
    {
        var parsed = SetupWindowCommandLine.TryParse(
            ["--no-rollback-on-failure"],
            out var arguments,
            out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.False(arguments.RollbackOnFailure);
    }

    [Theory]
    [InlineData("--config", "--config requires a value.")]
    [InlineData("--config=", "--config requires a value.")]
    [InlineData("--confg=custom.json", "Unknown option '--confg=custom.json'.")]
    [InlineData("custom.json", "Unexpected argument 'custom.json'.")]
    [InlineData("--", "Unknown option '--'.")]
    public void TryParse_RejectsMalformedInput(string token, string expectedError)
    {
        var parsed = SetupWindowCommandLine.TryParse([token], out _, out var error);

        Assert.False(parsed);
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void TryParse_RejectsAnotherOptionAsConfigValue()
    {
        var parsed = SetupWindowCommandLine.TryParse(
            ["--config", "--no-rollback-on-failure"],
            out _,
            out var error);

        Assert.False(parsed);
        Assert.Equal("--config requires a value.", error);
    }

    [Fact]
    public void TryParse_RejectsMisspelledSeparatedConfig()
    {
        var parsed = SetupWindowCommandLine.TryParse(
            ["--confg", "custom.json"],
            out _,
            out var error);

        Assert.False(parsed);
        Assert.Equal("Unknown option '--confg'.", error);
    }

    [Fact]
    public void TryParse_RejectsDuplicateConfig()
    {
        var parsed = SetupWindowCommandLine.TryParse(
            ["--config", "first.json", "--config=second.json"],
            out _,
            out var error);

        Assert.False(parsed);
        Assert.Equal("--config may only be specified once.", error);
    }
}
