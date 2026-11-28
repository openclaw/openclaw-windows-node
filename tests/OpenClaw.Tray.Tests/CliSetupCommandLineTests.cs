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

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void BuildEngineArguments_PreservesExistingStateUnlessReplacementAuthorized(
        bool replaceExisting, bool expectPreserve)
    {
        var arguments = CliSetupCommandLine.BuildEngineArguments(
            new CliSetupRequest(null, Force: false, ReplaceExisting: replaceExisting), "d", "l", "Distro", 1);

        Assert.Equal(expectPreserve, arguments.Contains("--preserve-existing"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void GetReplacementRefusal_UnprovenOwnership_RefusesEvenWithReplaceExisting(
        bool replaceExisting, bool hasDistro)
    {
        var refusal = CliSetupCommandLine.GetReplacementRefusal(
            new CliSetupRequest(null, Force: true, ReplaceExisting: replaceExisting),
            hasExistingState: true,
            ownershipUnproven: true,
            hasDistro,
            "OpenClawGateway",
            @"C:\local\wsl\OpenClawGateway",
            "• summary line");

        Assert.NotNull(refusal);
        Assert.Contains("• summary line", refusal);
        Assert.Contains("not proven to be managed by OpenClaw and was preserved", refusal);
        Assert.Contains("run OpenClaw setup interactively", refusal);
        Assert.DoesNotContain("--replace-existing", refusal);
        Assert.Contains(hasDistro ? "wsl --unregister OpenClawGateway" : @"C:\local\wsl\OpenClawGateway", refusal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetReplacementRefusal_AppOwnedState_RequiresReplaceExisting(bool replaceExisting)
    {
        var refusal = CliSetupCommandLine.GetReplacementRefusal(
            new CliSetupRequest(null, Force: true, ReplaceExisting: replaceExisting),
            hasExistingState: true,
            ownershipUnproven: false,
            hasDistro: true,
            "OpenClawGateway",
            @"C:\local\wsl\OpenClawGateway",
            "• summary line");

        if (replaceExisting)
            Assert.Null(refusal);
        else
            Assert.Contains("Pass --replace-existing to continue.", refusal);
    }

    [Fact]
    public void GetReplacementRefusal_NoExistingState_Admits()
    {
        Assert.Null(CliSetupCommandLine.GetReplacementRefusal(
            new CliSetupRequest(null, Force: true, ReplaceExisting: false),
            hasExistingState: false,
            ownershipUnproven: false,
            hasDistro: false,
            "OpenClawGateway",
            @"C:\local\wsl\OpenClawGateway",
            "• summary line"));
    }
}
