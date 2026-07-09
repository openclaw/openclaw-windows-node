using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class SessionTitleFormatterTests
{
    [Fact]
    public void Format_DistinguishesForkFromCanonicalSessionWithSameDisplayName()
    {
        var canonical = new SessionInfo
        {
            Key = "agent:main:main",
            IsMain = true,
            DisplayName = "OpenClaw Windows Tray",
        };
        var fork = new SessionInfo
        {
            Key = "agent:main:fork",
            DisplayName = "OpenClaw Windows Tray",
        };

        var canonicalTitle = SessionTitleFormatter.Format(canonical);
        var forkTitle = SessionTitleFormatter.Format(fork);

        Assert.Equal("OpenClaw Windows Tray", canonicalTitle);
        Assert.Equal("OpenClaw Windows Tray (main/fork)", forkTitle);
        Assert.NotEqual(canonicalTitle, forkTitle);
        Assert.Equal("agent:main:main", canonical.Key);
        Assert.Equal("agent:main:fork", fork.Key);
    }

    [Theory]
    [InlineData("agent:assistant:assistant", "Research", "Research (assistant)")]
    [InlineData("agent:assistant:review", "Research", "Research (assistant/review)")]
    [InlineData("legacy-session", "Research", "Research")]
    public void Format_UsesStableKeyQualifier(string key, string displayName, string expected)
    {
        var session = new SessionInfo { Key = key, DisplayName = displayName };

        Assert.Equal(expected, SessionTitleFormatter.Format(session));
    }

    [Fact]
    public void Format_PreservesExistingMissingDisplayNameFallbacks()
    {
        var main = new SessionInfo { Key = "agent:main:main", IsMain = true };
        var worker = new SessionInfo { Key = "agent:assistant:review" };

        Assert.Equal("OpenClaw Windows Tray", SessionTitleFormatter.Format(main));
        Assert.Equal("assistant (assistant/review)", SessionTitleFormatter.Format(worker));
    }

    [Fact]
    public void FormatUnique_DistinguishesMultiSegmentKeysWithSamePrefix()
    {
        var sessions = new[]
        {
            new SessionInfo { Key = "agent:main:subagent:uuid-b", DisplayName = "Research" },
            new SessionInfo { Key = "agent:main:subagent:uuid-a", DisplayName = "Research" },
        };

        var titles = SessionTitleFormatter.FormatUnique(sessions);

        Assert.Equal("Research (main/subagent) (2)", titles[0]);
        Assert.Equal("Research (main/subagent)", titles[1]);
        Assert.Equal(2, titles.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void FormatUnique_DistinguishesLegacyKeysWithoutExposingThem()
    {
        var sessions = new[]
        {
            new SessionInfo { Key = "legacy-b", DisplayName = "Research" },
            new SessionInfo { Key = "legacy-a", DisplayName = "Research" },
        };

        var titles = SessionTitleFormatter.FormatUnique(sessions);

        Assert.Equal("Research (2)", titles[0]);
        Assert.Equal("Research", titles[1]);
        Assert.DoesNotContain("legacy", string.Join(" ", titles), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatUnique_DoesNotCollideWithNaturalNumberedTitle()
    {
        var sessions = new[]
        {
            new SessionInfo { Key = "legacy-a", DisplayName = "Research" },
            new SessionInfo { Key = "legacy-b", DisplayName = "Research" },
            new SessionInfo { Key = "legacy-c", DisplayName = "Research (2)" },
        };

        var titles = SessionTitleFormatter.FormatUnique(sessions);

        Assert.Equal(new[] { "Research", "Research (3)", "Research (2)" }, titles);
        Assert.Equal(3, titles.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void FormatUnique_AssignmentRemainsStableWhenCallerFiltersSubset()
    {
        var sessions = new[]
        {
            new SessionInfo { Key = "legacy-a", DisplayName = "Research", Channel = "Slack" },
            new SessionInfo { Key = "legacy-b", DisplayName = "Research", Channel = "WhatsApp" },
        };
        var titles = SessionTitleFormatter.FormatUnique(sessions);
        var titledSessions = sessions
            .Select((session, index) => (Session: session, Title: titles[index]));

        var whatsAppTitle = titledSessions
            .Single(item => item.Session.Channel == "WhatsApp")
            .Title;

        Assert.Equal("Research (2)", whatsAppTitle);
    }

    [Fact]
    public void SessionsPage_UsesSharedSessionTitleFormatter()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "SessionsPage.xaml.cs"));

        var formatIndex = source.IndexOf("SessionTitleFormatter.FormatUnique(activeSessions)", StringComparison.Ordinal);
        var channelFilterIndex = source.IndexOf("if (_activeChannel != \"all\")", StringComparison.Ordinal);

        Assert.True(formatIndex >= 0);
        Assert.True(channelFilterIndex > formatIndex);
        Assert.Contains("ToViewModel(item.Session, item.Title)", source, StringComparison.Ordinal);
        Assert.Contains("DisplayName = displayName", source, StringComparison.Ordinal);
        Assert.Contains("Key = s.Key", source, StringComparison.Ordinal);

        var xaml = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "SessionsPage.xaml"));
        Assert.Contains("Tag=\"{Binding Key}\"", xaml, StringComparison.Ordinal);
    }
}
