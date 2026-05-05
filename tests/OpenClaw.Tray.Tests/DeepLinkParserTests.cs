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
    [InlineData("openclaw://settings", nameof(DeepLinkActions.OpenHub))]
    [InlineData("openclaw://setup", nameof(DeepLinkActions.OpenSetup))]
    [InlineData("openclaw://chat", nameof(DeepLinkActions.OpenHub))]
    [InlineData("openclaw://commandcenter", nameof(DeepLinkActions.OpenHub))]
    [InlineData("openclaw://history", nameof(DeepLinkActions.OpenActivityStream))]
    [InlineData("openclaw://logs", nameof(DeepLinkActions.OpenLogFile))]
    [InlineData("openclaw://log-folder", nameof(DeepLinkActions.OpenLogFolder))]
    [InlineData("openclaw://config", nameof(DeepLinkActions.OpenConfigFolder))]
    [InlineData("openclaw://diagnostics", nameof(DeepLinkActions.OpenDiagnosticsFolder))]
    [InlineData("openclaw://support-context", nameof(DeepLinkActions.CopySupportContext))]
    [InlineData("openclaw://debug-bundle", nameof(DeepLinkActions.CopyDebugBundle))]
    [InlineData("openclaw://browser-setup", nameof(DeepLinkActions.CopyBrowserSetupGuidance))]
    [InlineData("openclaw://port-diagnostics", nameof(DeepLinkActions.CopyPortDiagnostics))]
    [InlineData("openclaw://capability-diagnostics", nameof(DeepLinkActions.CopyCapabilityDiagnostics))]
    [InlineData("openclaw://node-inventory", nameof(DeepLinkActions.CopyNodeInventory))]
    [InlineData("openclaw://channel-summary", nameof(DeepLinkActions.CopyChannelSummary))]
    [InlineData("openclaw://activity-summary", nameof(DeepLinkActions.CopyActivitySummary))]
    [InlineData("openclaw://extensibility-summary", nameof(DeepLinkActions.CopyExtensibilitySummary))]
    [InlineData("openclaw://check-updates", nameof(DeepLinkActions.CheckForUpdates))]
    [InlineData("openclaw://restart-ssh-tunnel", nameof(DeepLinkActions.RestartSshTunnel))]
    public void Handle_InvokesExpectedAction(string uri, string expectedAction)
    {
        var invoked = "";
        var actions = new DeepLinkActions
        {
            OpenHub = _ => invoked = nameof(DeepLinkActions.OpenHub),
            OpenSetup = () => invoked = nameof(DeepLinkActions.OpenSetup),
            OpenLogFile = () => invoked = nameof(DeepLinkActions.OpenLogFile),
            OpenLogFolder = () => invoked = nameof(DeepLinkActions.OpenLogFolder),
            OpenConfigFolder = () => invoked = nameof(DeepLinkActions.OpenConfigFolder),
            OpenDiagnosticsFolder = () => invoked = nameof(DeepLinkActions.OpenDiagnosticsFolder),
            CopySupportContext = () => invoked = nameof(DeepLinkActions.CopySupportContext),
            CopyDebugBundle = () => invoked = nameof(DeepLinkActions.CopyDebugBundle),
            CopyBrowserSetupGuidance = () => invoked = nameof(DeepLinkActions.CopyBrowserSetupGuidance),
            CopyPortDiagnostics = () => invoked = nameof(DeepLinkActions.CopyPortDiagnostics),
            CopyCapabilityDiagnostics = () => invoked = nameof(DeepLinkActions.CopyCapabilityDiagnostics),
            CopyNodeInventory = () => invoked = nameof(DeepLinkActions.CopyNodeInventory),
            CopyChannelSummary = () => invoked = nameof(DeepLinkActions.CopyChannelSummary),
            CopyActivitySummary = () => invoked = nameof(DeepLinkActions.CopyActivitySummary),
            CopyExtensibilitySummary = () => invoked = nameof(DeepLinkActions.CopyExtensibilitySummary),
            OpenActivityStream = _ => invoked = nameof(DeepLinkActions.OpenActivityStream),
            CheckForUpdates = () =>
            {
                invoked = nameof(DeepLinkActions.CheckForUpdates);
                return Task.CompletedTask;
            },
            RestartSshTunnel = () => invoked = nameof(DeepLinkActions.RestartSshTunnel)
        };

        DeepLinkHandler.Handle(uri, actions);

        Assert.Equal(expectedAction, invoked);
    }

    [Fact]
    public void Handle_DashboardSubpath_PassesPath()
    {
        string? dashboardPath = null;
        var actions = new DeepLinkActions
        {
            OpenDashboard = path => dashboardPath = path
        };

        DeepLinkHandler.Handle("openclaw://dashboard/skills", actions);

        Assert.Equal("skills", dashboardPath);
    }

    [Fact]
    public void Handle_Activity_PassesFilter()
    {
        string? filter = null;
        var actions = new DeepLinkActions
        {
            OpenActivityStream = value => filter = value
        };

        DeepLinkHandler.Handle("openclaw://activity?filter=nodes", actions);

        Assert.Equal("nodes", filter);
    }

    [Fact]
    public void Handle_Send_PassesPrefillMessage()
    {
        string? message = null;
        var actions = new DeepLinkActions
        {
            OpenQuickSend = value => message = value
        };

        DeepLinkHandler.Handle("openclaw://send?message=hello%20world", actions);

        Assert.Equal("hello world", message);
    }

    [Fact]
    public async Task Handle_Agent_SendsMessage()
    {
        var sent = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = new DeepLinkActions
        {
            SendMessage = message =>
            {
                sent.SetResult(message);
                return Task.CompletedTask;
            }
        };

        DeepLinkHandler.Handle("openclaw://agent?message=ping", actions);

        var completed = await Task.WhenAny(sent.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(sent.Task, completed);
        Assert.Equal("ping", await sent.Task);
    }

    [Fact]
    public async Task Handle_HealthCheck_RunsAction()
    {
        var ran = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = new DeepLinkActions
        {
            RunHealthCheck = () =>
            {
                ran.SetResult(true);
                return Task.CompletedTask;
            }
        };

        DeepLinkHandler.Handle("openclaw://healthcheck", actions);

        var completed = await Task.WhenAny(ran.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(ran.Task, completed);
    }

    #endregion
}
