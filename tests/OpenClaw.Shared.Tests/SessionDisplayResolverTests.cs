using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public sealed class SessionDisplayResolverTests
{
    [Fact]
    public void Resolve_UsesFlatFactsAndDoesNotExposeDirectPeerDisplayName()
    {
        var resolved = SessionDisplayResolver.Resolve(new SessionInfo
        {
            Key = "agent:main:telegram:main:direct:491234567890",
            DisplayName = "Telegram:491234567890",
            Classification = "direct",
            AgentId = "main",
            AccountId = "main",
            PeerKind = "direct",
        });

        Assert.Equal("Telegram direct message", resolved.Title);
        Assert.Equal("direct", resolved.Classification);
        Assert.Equal("main", resolved.AccountId);
        Assert.DoesNotContain("491234567890", resolved.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_DoesNotUseRawKeyEchoAsDisplayName()
    {
        const string key = "agent:main:dashboard:new";

        var resolved = SessionDisplayResolver.Resolve(new SessionInfo
        {
            Key = key,
            DisplayName = key,
        });

        Assert.Equal("New session", resolved.Title);
        Assert.Equal("generated", resolved.TitleSource);
    }

    [Theory]
    [InlineData("agent:main:main", true, "main", "Main session", false)]
    [InlineData("agent:main:subagent:child", false, "subagent", "Subagent", true)]
    [InlineData("agent:main:cron:job", false, "cron", "Scheduled task", true)]
    public void Resolve_FallsBackForOlderGateways(string key, bool isMain, string classification, string title, bool isBackground)
    {
        var resolved = SessionDisplayResolver.Resolve(new SessionInfo { Key = key, IsMain = isMain });
        Assert.Equal(classification, resolved.Classification);
        Assert.Equal(title, resolved.Title);
        Assert.Equal(isBackground, resolved.IsBackground);
    }

    [Fact]
    public void IsVisible_HidesBackgroundUnlessUserEnablesIt()
    {
        var session = new SessionInfo { Key = "agent:main:subagent:child", Classification = "subagent", IsBackground = true };
        Assert.False(SessionDisplayResolver.IsVisible(session, showBackground: false));
        Assert.True(SessionDisplayResolver.IsVisible(session, showBackground: true));
    }
}
