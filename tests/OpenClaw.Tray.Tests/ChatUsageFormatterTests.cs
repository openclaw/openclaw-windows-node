using OpenClaw.Chat;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public class ChatUsageFormatterTests
{
    [Theory]
    [InlineData(0.00006, "$0.0001")]
    [InlineData(0.0005, "$0.0005")]
    [InlineData(0.009, "$0.0090")]
    [InlineData(0.22503, "$0.225")]
    [InlineData(0.5876, "$0.588")]
    [InlineData(1.0, "$1.00")]
    [InlineData(12.345, "$12.35")]
    [InlineData(1234.5, "$1234.50")]
    public void FormatCost_MatchesControlUiPrecision(double cost, string expected)
    {
        Assert.Equal(expected, ChatUsageFormatter.FormatCost(cost));
    }

    [Fact]
    public void FormatCost_ShowsFloorForCostBelowDisplayPrecision()
    {
        Assert.Equal("<$0.0001", ChatUsageFormatter.FormatCost(0.00004));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-0.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void FormatCost_HidesMissingZeroOrInvalidCost(double? cost)
    {
        Assert.Null(ChatUsageFormatter.FormatCost(cost));
    }

    [Fact]
    public void Format_UsesThreadTotals()
    {
        var thread = new ChatThread
        {
            Id = "main",
            Title = "Main",
            TotalTokens = 15300,
            ContextTokens = 1_000_000,
        };

        Assert.Equal("15.3K/1M (1%)", ChatUsageFormatter.Format(thread));
    }

    [Fact]
    public void Format_PrefersTotalTokensForContextUsage()
    {
        var thread = new ChatThread
        {
            Id = "main",
            Title = "Main",
            InputTokens = 20_000,
            OutputTokens = 3_700,
            TotalTokens = 23_500,
            ContextTokens = 400_000,
        };

        Assert.Equal("23.5K/400.0K (5%)", ChatUsageFormatter.Format(thread));
    }

    [Fact]
    public void Format_ComputesSmallPercentAsZero()
    {
        Assert.Equal("22/144.0K (0%)", ChatUsageFormatter.Format(22, 144_000));
    }

    [Fact]
    public void Format_ReturnsNullWhenUsageMissing()
    {
        Assert.Null(ChatUsageFormatter.Format(0, 0));
    }

    [Fact]
    public void Format_UsesLatestAssistantMetadataFallback()
    {
        var entries = new[]
        {
            new ChatTimelineItem("u1", ChatTimelineItemKind.User, "hi"),
            new ChatTimelineItem("a1", ChatTimelineItemKind.Assistant, "hello"),
        };
        var metadata = new Dictionary<string, ChatEntryMetadata>
        {
            ["a1"] = new(
                Timestamp: DateTimeOffset.Now,
                Model: "gpt-5.5",
                ResponseTokens: 15300,
                ContextPercent: 2),
        };

        Assert.Equal("15.3K/765.0K (2%)", ChatUsageFormatter.Format(entries, metadata));
    }

    [Fact]
    public void Format_LatestAssistantMetadataTakesPrecedenceOverLowerThreadTotals()
    {
        var thread = new ChatThread
        {
            Id = "main",
            Title = "Main",
            TotalTokens = 1_000,
            ContextTokens = 400_000,
        };
        var entries = new[]
        {
            new ChatTimelineItem("u1", ChatTimelineItemKind.User, "hi"),
            new ChatTimelineItem("a1", ChatTimelineItemKind.Assistant, "hello"),
        };
        var metadata = new Dictionary<string, ChatEntryMetadata>
        {
            ["a1"] = new(
                Timestamp: DateTimeOffset.Now,
                Model: "gpt-5.5",
                ResponseTokens: 68_000,
                ContextTokens: 400_000,
                ContextPercent: 17),
        };

        var displayedUsage = ChatUsageFormatter.Format(entries, metadata)
            ?? ChatUsageFormatter.Format(thread);

        Assert.Equal("68.0K/400.0K (17%)", displayedUsage);
    }

    [Fact]
    public void Format_UsesSingleEntryMetadata()
    {
        var metadata = new ChatEntryMetadata(
            Timestamp: DateTimeOffset.Now,
            Model: "gpt-5.5",
            InputTokens: 200,
            OutputTokens: 50,
            ContextPercent: 5);

        Assert.Equal("250/5.0K (5%)", ChatUsageFormatter.Format(metadata));
    }

    [Fact]
    public void Format_UsesContextTokenFallbackForEntryUsage()
    {
        Assert.Equal("250/5.0K (5%)", ChatUsageFormatter.Format(
            inputTokens: 200,
            outputTokens: 50,
            responseTokens: null,
            contextPercent: null,
            fallbackContextTokens: 5_000));
    }

    [Fact]
    public void Format_UsesContextPercentWhenAvailable()
    {
        Assert.Equal("68.0K/400.0K (17%)", ChatUsageFormatter.Format(
            inputTokens: null,
            outputTokens: null,
            responseTokens: 3_000,
            contextPercent: 17,
            fallbackContextTokens: 400_000));
    }
}
