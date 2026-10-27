using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class CliSetupCommandLineTests
{
    [Fact]
    public void TryParse_AcceptsConfigForceAndReplaceExisting()
    {
        Assert.True(CliSetupCommandLine.TryParse(
            ["app.exe", "--headless-setup", "--config", "a.json", "--force-setup", "--replace-existing"],
            out var request,
            out var error));

        Assert.Null(error);
        Assert.Equal(new CliSetupRequest("a.json", Force: true, ReplaceExisting: true), request);
    }

    [Fact]
    public void TryParse_ConfigIsOptionalAndFlagsDefaultOff()
    {
        Assert.True(CliSetupCommandLine.TryParse(["app.exe", "--HEADLESS-SETUP"], out var request, out _));

        Assert.Equal(new CliSetupRequest(null, Force: false, ReplaceExisting: false), request);
    }

    [Theory]
    [InlineData(new[] { "app.exe", "--headless-setup", "--config" }, "--config requires a value.")]
    [InlineData(new[] { "app.exe", "--headless-setup", "--config", "--force-setup" }, "--config requires a value.")]
    [InlineData(new[] { "app.exe", "--headless-setup", "--config", "a", "--config", "b" }, "--config may only be specified once.")]
    [InlineData(new[] { "app.exe", "--headless-setup", "--confg", "a" }, "Unknown option '--confg'.")]
    [InlineData(new[] { "app.exe", "--headless-setup", "--config=a.json" }, "Unknown option '--config=a.json'.")]
    public void TryParse_RejectsMalformedArguments(string[] args, string expectedError)
    {
        Assert.False(CliSetupCommandLine.TryParse(args, out var request, out var error));

        Assert.Equal(expectedError, error);
        Assert.Equal(new CliSetupRequest(null, Force: false, ReplaceExisting: false), request);
    }
}
