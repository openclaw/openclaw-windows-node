using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatWelcomePresentationTests
{
    [Theory]
    [InlineData(-10, "Just now")]
    [InlineData(0, "Just now")]
    [InlineData(59, "Just now")]
    [InlineData(60, "1m ago")]
    [InlineData(3599, "59m ago")]
    [InlineData(3600, "1h ago")]
    [InlineData(172800, "2d ago")]
    public void LastUsed_UsesLocalizedResourcesAndClampsClockSkew(int seconds, string expected)
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(expected, ChatWelcomePresentation.LastUsed(now.AddSeconds(-seconds), now));
        Assert.Equal("", ChatWelcomePresentation.LastUsed(null, now));
    }

    [Fact]
    public void AgentIdentity_UsesMatchingCatalogAndNeverLeaksPreviousAgent()
    {
        var catalog = new ChatPlacementCatalog(1, "main", [], new("default", ""), [],
            "Research assistant", "\u2728", "https://example.com/agent.png");
        var agent = ChatWelcomePresentation.AgentIdentity(catalog, null);
        Assert.Equal("Research assistant", agent.Name);
        Assert.Equal("\u2728", agent.Emoji);
        Assert.Equal(catalog.AgentAvatarUrl, agent.AvatarUrl);
        var other = ChatWelcomePresentation.AgentIdentity(catalog, "other");
        Assert.Equal("other", other.Name);
        Assert.Null(other.Emoji);
        Assert.Null(other.AvatarUrl);
        Assert.Equal("main", ChatWelcomePresentation.AgentIdentity(null, null).Name);
        Assert.Equal("main", ChatWelcomePresentation.AgentIdentity(catalog with { AgentName = " " }, "main").Name);
    }

    [Fact]
    public void RecentChats_UsesRecencyThenKeyAndCapsAtFive()
    {
        var threads = Enumerable.Range(0, 7)
            .Select(index => Thread($"chat-{index}", index))
            .Append(Thread("chat-a", 6))
            .Reverse();

        Assert.Equal(["chat-6", "chat-a", "chat-5", "chat-4", "chat-3"],
            ChatWelcomePresentation.RecentChats(threads, "new", "main").Select(thread => thread.Id));
    }

    [Fact]
    public void RecentChats_ExcludesCurrentHiddenEmptyAndOtherAgentThreads()
    {
        ChatThread[] threads =
        [
            Thread("current"),
            Thread("background") with { IsBackground = true },
            Thread("archived") with { IsArchived = true },
            Thread("channel") with { IsChannelSession = true },
            Thread("other-agent") with { AgentId = "work" },
            Thread("empty") with { TotalTokens = 0 },
            Thread("untitled") with { Title = " " },
            Thread(" ") with { Title = "Missing key" },
            Thread("visible"),
            Thread("working") with { TotalTokens = 0, Activity = ChatActivity.Working },
        ];

        Assert.Equal(["visible", "working"],
            ChatWelcomePresentation.RecentChats(threads, "current", "main").Select(thread => thread.Id));
    }

    [Fact]
    public void RecentChats_PreservesRawKeysTitlesAndUnknownTimestamps()
    {
        var dated = Thread("agent:work:dashboard:one") with { AgentId = "work", Title = "A readable title" };
        var undated = Thread("agent:work:dashboard:two") with { AgentId = "work", UpdatedAt = null };

        var result = ChatWelcomePresentation.RecentChats([undated, dated], "new", "WORK");

        Assert.Equal([dated, undated], result);
        Assert.Same(dated, result[0]);
        Assert.Empty(ChatWelcomePresentation.RecentChats([dated], dated.Id, "work"));
        Assert.Empty(ChatWelcomePresentation.RecentChats([], null, null));
    }

    private static ChatThread Thread(string id, int age = 0) => new()
    {
        Id = id,
        Title = id,
        TotalTokens = 1,
        UpdatedAt = DateTimeOffset.UnixEpoch.AddMinutes(age),
    };
}
