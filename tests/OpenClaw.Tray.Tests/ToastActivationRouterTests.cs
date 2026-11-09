using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class ToastActivationRouterTests
{
    [Theory]
    [InlineData("open_dashboard", typeof(ActivationRoute.OpenDashboard))]
    [InlineData("open_settings", typeof(ActivationRoute.OpenHub))]
    [InlineData("open_chat", typeof(ActivationRoute.OpenChat))]
    [InlineData("open_activity", typeof(ActivationRoute.OpenHub))]
    [InlineData("review_pairing", typeof(ActivationRoute.ReviewPairing))]
    public void PlanRoute_ReturnsExpectedRouteType(string action, Type expectedType)
    {
        var route = ToastActivationRouter.PlanRoute(action, _ => null);
        Assert.IsType(expectedType, route);
    }

    [Fact]
    public void PlanRoute_OpenUrl_RequiresUrlArgument()
    {
        var route = Assert.IsType<ActivationRoute.OpenUrl>(
            ToastActivationRouter.PlanRoute(
                "open_url",
                key => key == "url" ? "https://example.test/" : null));

        Assert.Equal("https://example.test/", route.Uri);
        Assert.Null(ToastActivationRouter.PlanRoute("open_url", _ => null));
    }

    [Fact]
    public void PlanRoute_CopyPairingCommand_RequiresCommandArgument()
    {
        var route = Assert.IsType<ActivationRoute.CopyPairingCommand>(
            ToastActivationRouter.PlanRoute(
                "copy_pairing_command",
                key => key == "command" ? "openclaw pair approve abc" : null));

        Assert.Equal("openclaw pair approve abc", route.Command);
        Assert.Null(ToastActivationRouter.PlanRoute("copy_pairing_command", _ => null));
    }

    [Fact]
    public void PlanRoute_OpenChat_PreservesSessionKey()
    {
        var route = Assert.IsType<ActivationRoute.OpenChat>(
            ToastActivationRouter.PlanRoute(
                "open_chat",
                key => key == "sessionKey" ? "agent:main:scratch" : null));

        Assert.Equal("agent:main:scratch", route.SessionKey);
    }

    [Fact]
    public void PlanRoute_UnknownAction_ReturnsNullWithoutReadingArguments()
    {
        Assert.Null(ToastActivationRouter.PlanRoute(
            "unknown",
            _ => throw new InvalidOperationException("arguments should not be read")));
    }

    [Fact]
    public void PlanRoute_ApprovalDecision_CarriesOnlyIdDecisionAndToken()
    {
        var args = new Dictionary<string, string> { ["approvalId"] = "a1", ["decision"] = "deny", ["token"] = "T" };

        var route = Assert.IsType<ActivationRoute.DecideApproval>(
            ToastActivationRouter.PlanRoute("approval_decision", key => args.GetValueOrDefault(key)));

        Assert.Equal(("a1", "deny", "T"), (route.ApprovalId, route.Decision, route.Token));
    }

    [Theory]
    [InlineData(null, "deny", "T")]
    [InlineData("a1", "approve", "T")]
    [InlineData("a1", "allow-once", null)]
    public void PlanRoute_IncompleteApprovalDecision_OpensTheInboxInstead(string? id, string? decision, string? token)
    {
        var args = new Dictionary<string, string?> { ["approvalId"] = id, ["decision"] = decision, ["token"] = token };

        var route = Assert.IsType<ActivationRoute.OpenHub>(
            ToastActivationRouter.PlanRoute("approval_decision", key => args.GetValueOrDefault(key)));

        Assert.Equal("notifications", route.Page);
        Assert.Equal("notifications", Assert.IsType<ActivationRoute.OpenHub>(
            ToastActivationRouter.PlanRoute("open_inbox", _ => null)).Page);
    }
}
