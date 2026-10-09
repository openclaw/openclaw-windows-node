namespace OpenClaw.Tray.Tests;

public sealed class ChatCostFooterContractTests
{
    [Fact]
    public void AssistantFooter_RendersCostThroughSharedFormatter()
    {
        var timeline = Read("src", "OpenClaw.Tray.WinUI", "Chat", "ReactorChatTimeline.cs");
        var start = timeline.IndexOf("private static string FooterText(", StringComparison.Ordinal);
        var end = timeline.IndexOf("private static Element UserMetadata(", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "FooterText must precede UserMetadata in ReactorChatTimeline.cs");
        var footer = timeline[start..end];

        // The footer gets its cost text from the shared formatter so the
        // precision rule is decided in one tested place.
        Assert.Contains("ChatUsageFormatter.FormatCost(", footer);
        Assert.Contains("metadata?.CostUsd", footer);
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(parts).ToArray()));
}
