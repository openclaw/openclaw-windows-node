using OpenClawTray.Services;
using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

public sealed class SetupWindowArgumentProjectionTests
{
    [Fact]
    public void Project_StripsNativeRestartHandleButPreservesConfig()
    {
        var handle = "ai-v3:" + new string('a', 64);
        Assert.Equal(["--config", "custom.json"], SetupWindowArgumentProjection.Project(
            ["app.exe", "--post-setup-restart", "--post-setup-launch", handle, "--config", "custom.json"],
            _ => false, 1000));
        Assert.Equal(["--post-setup-launch", "ai-v3:invalid", "--config", "custom.json"],
            SetupWindowArgumentProjection.Project(
                ["app.exe", "--post-setup-launch", "ai-v3:invalid", "--config", "custom.json"], _ => false, 1000));
    }

    [Fact]
    public void Project_RemovesHostArgumentsAndPreservesSetupAndUnknownTokens()
    {
        var projected = SetupWindowArgumentProjection.Project(
            [
                "OpenClaw.Tray.WinUI.exe",
                "--post-setup-restart",
                "--wait-for-pid",
                "42",
                "--post-setup-launch",
                "chat",
                "--config=custom.json",
                "--confg",
                "unexpected.json",
            ],
            _ => false,
            currentProcessId: 1000);

        Assert.Equal(
            ["--config=custom.json", "--confg", "unexpected.json"],
            projected);
    }

    [Fact]
    public void Project_RemovesRecognizedLeadingDeepLink()
    {
        var projected = SetupWindowArgumentProjection.Project(
            ["OpenClaw.Tray.WinUI.exe", "openclaw://setup", "--config=custom.json"],
            value => value.StartsWith("openclaw://", StringComparison.OrdinalIgnoreCase),
            currentProcessId: 1000);

        Assert.Equal(["--config=custom.json"], projected);
    }

    [Fact]
    public void Project_RemovesPackagedToastActivationArgument()
    {
        var manifest = XDocument.Load(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Package.appxmanifest"));
        var activationArgument = Assert.Single(
            manifest.Descendants(),
            element => element.Name.LocalName == "ExeServer" &&
                (string?)element.Attribute("Executable") == "OpenClaw.Tray.WinUI.exe")
            .Attribute("Arguments")?.Value;
        Assert.False(string.IsNullOrWhiteSpace(activationArgument));

        foreach (var argument in new[] { activationArgument!, activationArgument!.ToLowerInvariant() })
        {
            var projected = SetupWindowArgumentProjection.Project(
                ["OpenClaw.Tray.WinUI.exe", argument, "--config=custom.json"],
                _ => false,
                currentProcessId: 1000);

            Assert.Equal(["--config=custom.json"], projected);
        }
    }

    [Fact]
    public void Project_PreservesDeepLinkShapedConfigValue()
    {
        var projected = SetupWindowArgumentProjection.Project(
            ["OpenClaw.Tray.WinUI.exe", "--config", "openclaw://config"],
            value => value.StartsWith("openclaw://", StringComparison.OrdinalIgnoreCase),
            currentProcessId: 1000);

        Assert.Equal(["--config", "openclaw://config"], projected);
    }

    [Theory]
    [InlineData("--wait-for-pid", "--config")]
    [InlineData("--post-setup-launch", "--config")]
    [InlineData("--wait-for-pid", "--confg")]
    [InlineData("--post-setup-launch", "--confg")]
    public void Project_PreservesHostOptionAndFollowingArgumentsWhenHostValueIsMissing(
        string hostOption,
        string followingOption)
    {
        var projected = SetupWindowArgumentProjection.Project(
            ["OpenClaw.Tray.WinUI.exe", hostOption, followingOption, "custom.json"],
            _ => false,
            currentProcessId: 1000);

        Assert.Equal([hostOption, followingOption, "custom.json"], projected);
    }

    [Theory]
    [InlineData("--wait-for-pid")]
    [InlineData("--post-setup-launch")]
    public void Project_PreservesHostOptionAtEndOfInput(string hostOption)
    {
        var projected = SetupWindowArgumentProjection.Project(
            ["OpenClaw.Tray.WinUI.exe", hostOption],
            _ => false,
            currentProcessId: 1000);

        Assert.Equal([hostOption], projected);
    }

    [Fact]
    public void Project_PreservesHostOptionWithWhitespaceValue()
    {
        var projected = SetupWindowArgumentProjection.Project(
            ["OpenClaw.Tray.WinUI.exe", "--wait-for-pid", " "],
            _ => false,
            currentProcessId: 1000);

        Assert.Equal(["--wait-for-pid", " "], projected);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+42")]
    [InlineData(" 42")]
    [InlineData("42 ")]
    [InlineData("1000")]
    [InlineData("999999999999")]
    public void Project_PreservesInvalidWaitForPidValues(string value)
    {
        var projected = SetupWindowArgumentProjection.Project(
            ["OpenClaw.Tray.WinUI.exe", "--wait-for-pid", value, "--config", "custom.json"],
            _ => false,
            currentProcessId: 1000);

        Assert.Equal(
            ["--wait-for-pid", value, "--config", "custom.json"],
            projected);
    }

    [Theory]
    [InlineData("permissions")]
    [InlineData("browser")]
    public void Project_PreservesUnknownPostSetupLaunchTargets(string value)
    {
        var projected = SetupWindowArgumentProjection.Project(
            ["OpenClaw.Tray.WinUI.exe", "--post-setup-launch", value, "--config", "custom.json"],
            _ => false,
            currentProcessId: 1000);

        Assert.Equal(
            ["--post-setup-launch", value, "--config", "custom.json"],
            projected);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("00042")]
    public void Project_RemovesValidWaitForPidValues(string value)
    {
        var projected = SetupWindowArgumentProjection.Project(
            ["OpenClaw.Tray.WinUI.exe", "--wait-for-pid", value, "--config", "custom.json"],
            _ => false,
            currentProcessId: 1000);

        Assert.Equal(["--config", "custom.json"], projected);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("CHAT")]
    [InlineData("settings")]
    [InlineData("SETTINGS")]
    [InlineData("connection")]
    [InlineData("CONNECTION")]
    public void Project_RemovesRecognizedPostSetupLaunchTargets(string value)
    {
        var projected = SetupWindowArgumentProjection.Project(
            ["OpenClaw.Tray.WinUI.exe", "--post-setup-launch", value, "--config", "custom.json"],
            _ => false,
            currentProcessId: 1000);

        Assert.Equal(["--config", "custom.json"], projected);
    }
}
