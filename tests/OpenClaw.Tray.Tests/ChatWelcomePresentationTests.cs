using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using System.Text.Json;

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
    public void AgentIdentity_CurrentRosterPresentationWinsOverRetainedCatalog()
    {
        var catalog = new ChatPlacementCatalog(1, "main", [], new("default", ""), [],
            "Research assistant", "\U0001F52C", "https://example.com/stale.png");
        var current = new ChatAgentPresentation(
            "main", "Assistant", "https://example.com/current.png", "\U0001F916", Revision: 2);

        var agent = ChatWelcomePresentation.AgentIdentity(catalog, "main", currentRoster: current);
        var other = ChatWelcomePresentation.AgentIdentity(catalog, "other", currentRoster: current);

        Assert.Equal("Assistant", agent.Name);
        Assert.Equal("\U0001F916", agent.Emoji);
        Assert.Equal("https://example.com/current.png", agent.AvatarUrl);
        Assert.Equal("other", other.Name);
        Assert.Null(other.Emoji);
        Assert.Null(other.AvatarUrl);
    }

    [Fact]
    public void AgentIdentity_UsesGatewayDefaultNameAndLeavesDefaultAvatarToGeneratedFace()
    {
        // Unconfigured main: agents.list has no name; agent.identity.get returns the built-in default.
        var catalog = new ChatPlacementCatalog(1, "main", [], new("default", ""), []);
        using var json = JsonDocument.Parse("""{"agentId":"main","name":"Assistant","avatar":"A"}""");
        var hydrated = ChatWelcomePresentation.ParseAgentIdentity("main", json.RootElement);

        var agent = ChatWelcomePresentation.AgentIdentity(catalog, "main", hydrated);

        Assert.Equal("Assistant", agent.Name);
        Assert.Null(agent.Emoji);
        Assert.Null(agent.AvatarUrl);
        Assert.Equal("Roster", ChatWelcomePresentation.AgentIdentity(
            catalog with { AgentName = "Roster" }, "main", hydrated).Name);
        Assert.Equal("other", ChatWelcomePresentation.AgentIdentity(catalog, "other", hydrated).Name);
    }

    [Fact]
    public void AgentIdentity_ResolvedIdentityOwnsImageAbsence()
    {
        var catalog = new ChatPlacementCatalog(1, "main", [], new("default", ""), [],
            "Research assistant", null, "https://example.com/stale.png", "avatars/main.png");

        Assert.Equal("https://example.com/stale.png", ChatWelcomePresentation.AgentIdentity(catalog, "main").AvatarUrl);
        Assert.Null(ChatWelcomePresentation.AgentIdentity(catalog, "main", new("main", null, null, null)).AvatarUrl);
    }

    [Theory]
    [InlineData("data:image/png;base64,AQID", "✨", "data:image/png;base64,AQID", "✨")]
    [InlineData("https://images.example/avatar.png", null, "https://images.example/avatar.png", null)]
    [InlineData("✨", null, null, "✨")]
    [InlineData("http://images.example/avatar.png", "A", null, null)]
    [InlineData("A", null, null, null)]
    [InlineData("avatars/main.png", null, null, null)]
    public void ParseAgentIdentity_SeparatesRenderableImageFromTextFallback(
        string avatar, string? emoji, string? expectedImage, string? expectedFallback)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            agentId = "main",
            name = "Research assistant",
            avatar,
            emoji
        }));

        var identity = ChatWelcomePresentation.ParseAgentIdentity("main", json.RootElement);

        Assert.Equal("main", identity.AgentId);
        Assert.Equal("Research assistant", identity.DisplayName);
        Assert.Equal(expectedImage, identity.AvatarUrl);
        Assert.Equal(expectedFallback, identity.Emoji);
    }

    [Fact]
    public void ParseAgentIdentity_RejectsResponseForDifferentAgent()
    {
        using var json = JsonDocument.Parse("""{"agentId":"other","name":"Other"}""");

        Assert.Throws<InvalidDataException>(() =>
            ChatWelcomePresentation.ParseAgentIdentity("main", json.RootElement));
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
