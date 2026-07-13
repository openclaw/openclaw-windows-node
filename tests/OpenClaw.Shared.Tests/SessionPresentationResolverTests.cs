using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public sealed class SessionPresentationResolverTests
{
    [Fact]
    public void Resolve_PrefersGatewayTitleOverLegacyDisplayName()
    {
        var presentation = SessionPresentationResolver.Resolve(new SessionInfo
        {
            Key = "agent:main:telegram:main:direct:491234567890",
            DisplayName = "Telegram:491234567890",
            Presentation = new SessionPresentationInfo
            {
                Title = "Family chat",
                Family = "direct",
            },
        });

        Assert.Equal("Family chat", presentation.Title);
        Assert.DoesNotContain("491234567890", presentation.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_PrefersGatewayPresentationAndProjectsContext()
    {
        var session = new SessionInfo
        {
            Key = "agent:main:telegram:main:direct:491234567890",
            DisplayName = "agent:main:telegram:main:direct:491234567890",
            Presentation = new SessionPresentationInfo
            {
                Title = "Telegram direct message",
                Subtitle = "Telegram · account main · agent main",
                Family = "direct",
                AgentId = "main",
                Channel = "telegram",
                AccountId = "main",
                PeerKind = "direct",
            },
        };

        var resolved = SessionPresentationResolver.Resolve(session);

        Assert.Equal("Telegram direct message", resolved.Title);
        Assert.Equal("Telegram · account main · agent main", resolved.Subtitle);
        Assert.Equal("direct", resolved.Family);
        Assert.Equal("main", resolved.AgentId);
        Assert.Equal("telegram", resolved.Channel);
        Assert.Equal("main", resolved.AccountId);
        Assert.DoesNotContain("491234567890", resolved.Title);
    }

    [Theory]
    [InlineData("agent:main:main", true, "main", "Main session", false)]
    [InlineData("agent:main:dashboard:01234567-89ab-cdef-0123-456789abcdef", false, "dashboard", "New session", false)]
    [InlineData("agent:main:tui-01234567-89ab-cdef-0123-456789abcdef", false, "tui", "Terminal session", false)]
    [InlineData("agent:main:tui-01234567-89ab-cdef-0123-456789abcdef:heartbeat", false, "heartbeat", "Heartbeat", true)]
    [InlineData("agent:main:subagent:child", false, "subagent", "Subagent", true)]
    [InlineData("agent:main:acp:child", false, "acp", "ACP session", true)]
    [InlineData("agent:main:cron:job:run:run-1", false, "cron", "Scheduled task", true)]
    [InlineData("agent:main:dreaming-narrative-rem-workspace", false, "dreaming", "Dreaming", true)]
    [InlineData("agent:main:internal-session-effects:run", false, "system", "Background task", true)]
    public void Resolve_FallsBackForOlderGateways(
        string key,
        bool isMain,
        string family,
        string title,
        bool isBackground)
    {
        var resolved = SessionPresentationResolver.Resolve(new SessionInfo { Key = key, IsMain = isMain });

        Assert.Equal(family, resolved.Family);
        Assert.Equal(title, resolved.Title);
        Assert.Equal(isBackground, resolved.IsBackground);
    }

    [Fact]
    public void Resolve_TreatsOnlyTheAgentWrapperAsStructured()
    {
        var resolved = SessionPresentationResolver.Resolve(new SessionInfo
        {
            Key = "agent:ops:explicit:model-run-01234567-89ab-cdef-0123-456789abcdef",
        });

        Assert.Equal("model-run-…cdef", resolved.Title);
        Assert.Equal("ops", resolved.AgentId);
        Assert.DoesNotContain("01234567-89ab-cdef-0123-456789abcdef", resolved.Title);
    }

    [Theory]
    [InlineData("agent:ops:discord:work:group:dev", "group")]
    [InlineData("agent:ops:discord:work:channel:dev", "channel")]
    [InlineData("agent:ops:discord:work:room:dev", "group")]
    public void Resolve_RecognizesAccountQualifiedGroupRoutes(string key, string family)
    {
        var resolved = SessionPresentationResolver.Resolve(new SessionInfo { Key = key });

        Assert.Equal(family, resolved.Family);
        Assert.Equal("discord", resolved.Channel);
        Assert.Equal("work", resolved.AccountId);
    }

    [Fact]
    public void Resolve_RejectsRawKeyDisplayNames()
    {
        const string key = "agent:main:telegram:main:direct:491234567890";
        var resolved = SessionPresentationResolver.Resolve(new SessionInfo
        {
            Key = key,
            DisplayName = key,
        });

        Assert.Equal("Telegram direct message", resolved.Title);
        Assert.DoesNotContain("491234567890", resolved.Title);
    }

    [Fact]
    public void Resolve_DoesNotGuessHeartbeatFromAmbiguousSuffixes()
    {
        var explicitSession = SessionPresentationResolver.Resolve(new SessionInfo
        {
            Key = "agent:ops:explicit:heartbeat",
        });
        var routedSession = SessionPresentationResolver.Resolve(new SessionInfo
        {
            Key = "agent:ops:telegram:group:heartbeat",
        });

        Assert.Equal("explicit", explicitSession.Family);
        Assert.False(explicitSession.IsBackground);
        Assert.Equal("group", routedSession.Family);
        Assert.False(routedSession.IsBackground);
    }

    [Fact]
    public void Resolve_RejectsLegacyDisplayNamesContainingOpaqueIds()
    {
        var resolved = SessionPresentationResolver.Resolve(new SessionInfo
        {
            Key = "agent:main:telegram:main:direct:491234567890",
            DisplayName = "Telegram:491234567890",
        });

        Assert.Equal("Telegram direct message", resolved.Title);
        Assert.DoesNotContain("491234567890", resolved.Title);
    }

    [Fact]
    public void Resolve_AssignsCanonicalGlobalSessionToMainAgent()
    {
        var resolved = SessionPresentationResolver.Resolve(new SessionInfo { Key = "global", IsMain = true });

        Assert.Equal("main", resolved.AgentId);
        Assert.True(resolved.IsMain);
    }

    [Fact]
    public void IsVisible_HidesBackgroundUnlessUserEnablesIt()
    {
        var session = new SessionInfo
        {
            Key = "agent:main:subagent:child",
            Presentation = new SessionPresentationInfo
            {
                Title = "Research",
                Family = "subagent",
                IsBackground = true,
            },
        };

        Assert.False(SessionPresentationResolver.IsVisible(session, showBackground: false));
        Assert.True(SessionPresentationResolver.IsVisible(session, showBackground: true));
    }
}
