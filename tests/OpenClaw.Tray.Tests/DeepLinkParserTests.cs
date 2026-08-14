using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class DeepLinkParserTests
{
    #region ParseDeepLink

    [Fact]
    public void ParseDeepLink_Settings()
    {
        var result = DeepLinkParser.ParseDeepLink("openclaw://settings");
        Assert.NotNull(result);
        Assert.Equal("settings", result.Path);
        Assert.Empty(result.Query);
        Assert.Empty(result.Parameters);
    }

    [Fact]
    public void ParseDeepLink_Dashboard()
    {
        var result = DeepLinkParser.ParseDeepLink("openclaw://dashboard");
        Assert.NotNull(result);
        Assert.Equal("dashboard", result.Path);
    }

    [Fact]
    public void ParseDeepLink_DashboardSubpath()
    {
        var result = DeepLinkParser.ParseDeepLink("openclaw://dashboard/sessions");
        Assert.NotNull(result);
        Assert.Equal("dashboard/sessions", result.Path);
    }

    [Theory]
    [InlineData("openclaw://dashboard/channels", "dashboard/channels")]
    [InlineData("openclaw://dashboard/skills", "dashboard/skills")]
    [InlineData("openclaw://dashboard/cron", "dashboard/cron")]
    public void ParseDeepLink_DashboardKnownSubpaths(string uri, string expectedPath)
    {
        var result = DeepLinkParser.ParseDeepLink(uri);
        Assert.NotNull(result);
        Assert.Equal(expectedPath, result.Path);
    }

    [Fact]
    public void ParseDeepLink_SendWithMessage()
    {
        var result = DeepLinkParser.ParseDeepLink("openclaw://send?message=hello");
        Assert.NotNull(result);
        Assert.Equal("send", result.Path);
        Assert.Equal("hello", result.Parameters["message"]);
    }

    [Fact]
    public void ParseDeepLink_SendWithEncodedMessage()
    {
        var result = DeepLinkParser.ParseDeepLink("openclaw://send?message=hello%20world");
        Assert.NotNull(result);
        Assert.Equal("hello world", result.Parameters["message"]);
    }

    [Fact]
    public void ParseDeepLink_MultipleQueryParams()
    {
        var result = DeepLinkParser.ParseDeepLink("openclaw://agent?message=hi&key=abc");
        Assert.NotNull(result);
        Assert.Equal("agent", result.Path);
        Assert.Equal("hi", result.Parameters["message"]);
        Assert.Equal("abc", result.Parameters["key"]);
    }

    [Fact]
    public void ParseDeepLink_ActivityWithFilter()
    {
        var result = DeepLinkParser.ParseDeepLink("openclaw://activity?filter=nodes");
        Assert.NotNull(result);
        Assert.Equal("activity", result.Path);
        Assert.Equal("nodes", result.Parameters["filter"]);
    }

    [Fact]
    public void ParseDeepLink_History()
    {
        var result = DeepLinkParser.ParseDeepLink("openclaw://history");
        Assert.NotNull(result);
        Assert.Equal("history", result.Path);
    }

    [Theory]
    [InlineData("openclaw://setup", "setup")]
    [InlineData("openclaw://healthcheck", "healthcheck")]
    [InlineData("openclaw://check-updates", "check-updates")]
    [InlineData("openclaw://logs", "logs")]
    [InlineData("openclaw://log-folder", "log-folder")]
    [InlineData("openclaw://config", "config")]
    [InlineData("openclaw://diagnostics", "diagnostics")]
    [InlineData("openclaw://support-context", "support-context")]
    [InlineData("openclaw://debug-bundle", "debug-bundle")]
    [InlineData("openclaw://browser-setup", "browser-setup")]
    [InlineData("openclaw://port-diagnostics", "port-diagnostics")]
    [InlineData("openclaw://capability-diagnostics", "capability-diagnostics")]
    [InlineData("openclaw://node-inventory", "node-inventory")]
    [InlineData("openclaw://channel-summary", "channel-summary")]
    [InlineData("openclaw://activity-summary", "activity-summary")]
    [InlineData("openclaw://extensibility-summary", "extensibility-summary")]
    [InlineData("openclaw://restart-ssh-tunnel", "restart-ssh-tunnel")]
    public void ParseDeepLink_TrayUtilityEntrypoints(string uri, string expectedPath)
    {
        var result = DeepLinkParser.ParseDeepLink(uri);
        Assert.NotNull(result);
        Assert.Equal(expectedPath, result.Path);
    }

    [Fact]
    public void ParseDeepLink_TrailingSlash_IsStripped()
    {
        var result = DeepLinkParser.ParseDeepLink("openclaw://settings/");
        Assert.NotNull(result);
        Assert.Equal("settings", result.Path);
    }

    [Theory]
    [InlineData("openclaw://send/?message=hello", "send")]
    [InlineData("openclaw://agent/?message=hi&key=abc", "agent")]
    [InlineData("openclaw://activity/?filter=nodes", "activity")]
    public void ParseDeepLink_TrailingSlashBeforeQuery_IsStripped(string uri, string expectedPath)
    {
        // Windows canonicalizes openclaw://send?... to openclaw://send/?...
        // before handing it to us. The slash sits before the `?`, so a naïve
        // TrimEnd before query split fails to strip it. Regression test for
        // the off-by-one fix in DeepLinkParser.ParseDeepLink.
        var result = DeepLinkParser.ParseDeepLink(uri);
        Assert.NotNull(result);
        Assert.Equal(expectedPath, result!.Path);
    }

    [Fact]
    public void ParseDeepLink_CaseInsensitiveScheme()
    {
        var result = DeepLinkParser.ParseDeepLink("OPENCLAW://dashboard");
        Assert.NotNull(result);
        Assert.Equal("dashboard", result.Path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseDeepLink_NullOrEmpty_ReturnsNull(string? uri)
    {
        Assert.Null(DeepLinkParser.ParseDeepLink(uri));
    }

    [Fact]
    public void ParseDeepLink_NoProtocol_ReturnsNull()
    {
        Assert.Null(DeepLinkParser.ParseDeepLink("settings"));
    }

    [Fact]
    public void ParseDeepLink_WrongProtocol_ReturnsNull()
    {
        Assert.Null(DeepLinkParser.ParseDeepLink("https://settings"));
    }

    [Fact]
    public void ParseDeepLink_EmptyPath()
    {
        var result = DeepLinkParser.ParseDeepLink("openclaw://");
        Assert.NotNull(result);
        Assert.Equal("", result.Path);
    }

    [Fact]
    public void ParseDeepLink_MalformedQuery_IgnoresKeyOnly()
    {
        var result = DeepLinkParser.ParseDeepLink("openclaw://send?message");
        Assert.NotNull(result);
        Assert.Empty(result.Parameters);
    }

    #endregion

    #region GetQueryParam

    [Fact]
    public void GetQueryParam_ExtractsValue()
    {
        Assert.Equal("hello", DeepLinkParser.GetQueryParam("message=hello", "message"));
    }

    [Fact]
    public void GetQueryParam_CaseInsensitiveKey()
    {
        Assert.Equal("hello", DeepLinkParser.GetQueryParam("MESSAGE=hello", "message"));
    }

    [Fact]
    public void GetQueryParam_UrlDecodes()
    {
        Assert.Equal("hello world", DeepLinkParser.GetQueryParam("msg=hello%20world", "msg"));
    }

    [Fact]
    public void GetQueryParam_MissingKey_ReturnsNull()
    {
        Assert.Null(DeepLinkParser.GetQueryParam("message=hello", "missing"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetQueryParam_NullOrEmptyQuery_ReturnsNull(string? query)
    {
        Assert.Null(DeepLinkParser.GetQueryParam(query, "key"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetQueryParam_NullOrEmptyKey_ReturnsNull(string? key)
    {
        Assert.Null(DeepLinkParser.GetQueryParam("message=hello", key!));
    }

    [Fact]
    public void GetQueryParam_MultipleParams_FindsCorrect()
    {
        Assert.Equal("bar", DeepLinkParser.GetQueryParam("foo=baz&key=bar&x=1", "key"));
    }

    [Fact]
    public void GetQueryParam_ValueWithEquals()
    {
        Assert.Equal("a=b", DeepLinkParser.GetQueryParam("token=a=b", "token"));
    }

    #endregion

    #region DeepLinkHandler

    [Theory]
    [InlineData("openclaw://settings", "hub:settings")]
    [InlineData("openclaw://setup", "setup")]
    [InlineData("openclaw://chat", "hub:chat")]
    [InlineData("openclaw://commandcenter", "hub:connection")]
    [InlineData("openclaw://history", "hub:channels")]
    [InlineData("openclaw://logs", "log-file")]
    [InlineData("openclaw://log-folder", "log-folder")]
    [InlineData("openclaw://config", "config-folder")]
    [InlineData("openclaw://diagnostics", "diagnostics-folder")]
    [InlineData("openclaw://support-context", "copy:SupportContext")]
    [InlineData("openclaw://debug-bundle", "copy:DebugBundle")]
    [InlineData("openclaw://browser-setup", "copy:BrowserSetupGuidance")]
    [InlineData("openclaw://port-diagnostics", "copy:PortDiagnostics")]
    [InlineData("openclaw://capability-diagnostics", "copy:CapabilityDiagnostics")]
    [InlineData("openclaw://node-inventory", "copy:NodeInventory")]
    [InlineData("openclaw://channel-summary", "copy:ChannelSummary")]
    [InlineData("openclaw://activity-summary", "copy:ActivitySummary")]
    [InlineData("openclaw://extensibility-summary", "copy:ExtensibilitySummary")]
    [InlineData("openclaw://check-updates", "check-updates")]
    [InlineData("openclaw://restart-ssh-tunnel", "restart-ssh")]
    public void PlanRoute_ReturnsExpectedRoute(string uri, string expected)
    {
        var route = DeepLinkHandler.PlanRoute(uri, "openclaw");
        var actual = route switch
        {
            ActivationRoute.OpenHub r => $"hub:{r.Page}",
            ActivationRoute.OpenSetup => "setup",
            ActivationRoute.OpenLogFile => "log-file",
            ActivationRoute.OpenLogFolder => "log-folder",
            ActivationRoute.OpenConfigFolder => "config-folder",
            ActivationRoute.OpenDiagnosticsFolder => "diagnostics-folder",
            ActivationRoute.CopyDiagnostics r => $"copy:{r.Kind}",
            ActivationRoute.CheckForUpdates => "check-updates",
            ActivationRoute.RestartSshTunnel => "restart-ssh",
            _ => route?.GetType().Name,
        };

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("openclaw://activity", "channels")]
    [InlineData("openclaw://activity?filter=usage", "usage")]
    [InlineData("openclaw://activity?filter=session", "sessions")]
    [InlineData("openclaw://activity?filter=node", "instances")]
    [InlineData("openclaw://history", "channels")]
    [InlineData("openclaw://notification-history", "channels")]
    [InlineData("openclaw://commandcenter", "connection")]
    [InlineData("openclaw://command-center", "connection")]
    public void PlanRoute_HubAliases_ReturnExpectedPage(string uri, string expectedHubTag)
    {
        var route = Assert.IsType<ActivationRoute.OpenHub>(
            DeepLinkHandler.PlanRoute(uri, "openclaw"));
        Assert.Equal(expectedHubTag, route.Page);
    }

    [Fact]
    public void PlanRoute_DashboardSubpath_PreservesPath()
    {
        var route = Assert.IsType<ActivationRoute.OpenDashboard>(
            DeepLinkHandler.PlanRoute("openclaw://dashboard/skills", "openclaw"));
        Assert.Equal("skills", route.Path);
    }

    [Fact]
    public void PlanRoute_Activity_RedirectsByFilter()
    {
        Assert.Equal("instances", PlanHubPage("openclaw://activity?filter=node"));
        Assert.Equal("sessions", PlanHubPage("openclaw://activity?filter=session"));
        Assert.Equal("channels", PlanHubPage("openclaw://activity"));
    }

    [Fact]
    public void PlanRoute_Agent_PreservesMessage()
    {
        var route = Assert.IsType<ActivationRoute.SendMessage>(
            DeepLinkHandler.PlanRoute("openclaw://agent?message=ping", "openclaw"));
        Assert.Equal("ping", route.Message);
    }

    [Fact]
    public void PlanRoute_HealthCheck_ReturnsHealthRoute()
    {
        Assert.IsType<ActivationRoute.RunHealthCheck>(
            DeepLinkHandler.PlanRoute("openclaw://healthcheck", "openclaw"));
    }

    private static string? PlanHubPage(string uri) =>
        Assert.IsType<ActivationRoute.OpenHub>(
            DeepLinkHandler.PlanRoute(uri, "openclaw")).Page;

    #endregion
}
