using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class ToastActivationRouterTests
{
    [Theory]
    [InlineData("open_dashboard", "dashboard")]
    [InlineData("open_settings", "settings")]
    [InlineData("open_chat", "chat")]
    [InlineData("open_activity", "activity")]
    public void Route_DispatchesSimpleActions(string action, string expected)
    {
        var calls = new List<string>();

        ToastActivationRouter.Route(
            action,
            _ => null,
            BuildActions(calls));

        Assert.Equal([expected], calls);
    }

    [Fact]
    public void Route_OpenUrl_RequiresUrlArgument()
    {
        var calls = new List<string>();

        ToastActivationRouter.Route(
            "open_url",
            key => key == "url" ? "https://example.test/" : null,
            BuildActions(calls));

        ToastActivationRouter.Route(
            "open_url",
            _ => null,
            BuildActions(calls));

        Assert.Equal(["url:https://example.test/"], calls);
    }

    [Fact]
    public void Route_CopyPairingCommand_RequiresCommandArgument()
    {
        var calls = new List<string>();

        ToastActivationRouter.Route(
            "copy_pairing_command",
            key => key == "command" ? "openclaw pair approve abc" : null,
            BuildActions(calls));

        ToastActivationRouter.Route(
            "copy_pairing_command",
            _ => null,
            BuildActions(calls));

        Assert.Equal(["copy:openclaw pair approve abc"], calls);
    }

    [Fact]
    public void Route_UnknownAction_NoOps()
    {
        var calls = new List<string>();

        ToastActivationRouter.Route(
            "unknown",
            _ => throw new InvalidOperationException("arguments should not be read"),
            BuildActions(calls));

        Assert.Empty(calls);
    }

    private static ToastActivationActions BuildActions(List<string> calls) => new()
    {
        OpenUrl = url => calls.Add($"url:{url}"),
        OpenDashboard = () => calls.Add("dashboard"),
        OpenSettings = () => calls.Add("settings"),
        OpenChat = () => calls.Add("chat"),
        OpenActivity = () => calls.Add("activity"),
        CopyPairingCommand = command => calls.Add($"copy:{command}")
    };
}
