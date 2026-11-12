using OpenClaw.Connection;
using OpenClaw.Tray.Tests.Presentation;
using OpenClawTray.Presentation.Usage;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Usage;

/// <summary>
/// The Usage page hands off through app commands: the browser action asks for the Gateway's Control UI usage route,
/// and a session opens in native chat as the pending chat session. The browser path is then driven through the real
/// Dashboard launcher and URL builder with an intercepted browser, so the mount path and the absence of any range or
/// session context are checked on the URL Windows would open.
/// </summary>
public sealed class UsageHandoffTests
{
    [Theory]
    [InlineData("wss://gateway.example:18789/control/", "https://gateway.example:18789/control/usage#token=synthetic%20shared")]
    [InlineData("ws://gateway.example/mounted/gw", "http://gateway.example/mounted/gw/usage#token=synthetic%20shared")]
    public async Task OpenInBrowser_OpensTheMountedUsageRouteWithoutRangeOrSessionContext(string gatewayUrl, string expected)
    {
        var commands = new FakeAppCommands();
        var (openWebUsage, _) = UsageHandoffs.For(commands, _ => throw new InvalidOperationException("No chat handoff expected."));

        openWebUsage();

        var path = Assert.Single(commands.DashboardPaths);
        Assert.Equal("usage", path);
        Assert.Empty(commands.Navigations);

        string? launched = null;
        var launcher = new GatewayDashboardLauncher(() => true,
            () => new(gatewayUrl, "synthetic shared", false, CredentialResolver.SourceSharedGatewayToken),
            url => { launched = url; return Task.FromResult(true); },
            () => throw new InvalidOperationException("Unexpected launch failure."));
        Assert.True(await launcher.OpenAsync(path));
        Assert.Equal(expected, launched);
        Assert.DoesNotContain("session", launched, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("?", launched, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenConversation_HandsTheSelectedKeyToNativeChatBeforeNavigating()
    {
        var commands = new FakeAppCommands();
        var pending = new List<(string Key, int NavigationsAtHandoff)>();
        var (_, openConversation) = UsageHandoffs.For(commands, key => pending.Add((key, commands.Navigations.Count)));

        openConversation("agent:research:discord:42");

        // Chat selects the pending session once it is ready, so the key must be in place before navigation.
        Assert.Equal([("agent:research:discord:42", 0)], pending);
        Assert.Equal(["chat"], commands.Navigations);
        Assert.Empty(commands.DashboardPaths);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void OpenConversation_IgnoresABlankKey(string key)
    {
        var commands = new FakeAppCommands();
        var (_, openConversation) = UsageHandoffs.For(commands, _ => throw new InvalidOperationException("No handoff expected."));

        openConversation(key);

        Assert.Empty(commands.Navigations);
    }
}
