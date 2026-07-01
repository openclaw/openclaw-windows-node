using System.Text.RegularExpressions;

namespace OpenClaw.Tray.Tests;

public sealed class ChatTimelineRenderIdentityContractTests
{
    [Fact]
    public void TimelineRows_UseGenerationQualifiedKindedKeys()
    {
        var timeline = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatTimeline.cs");

        Assert.Contains("long TimelineGeneration = 0", timeline);
        Assert.Contains("string RowKey(ChatTimelineItem entry)", timeline);
        Assert.Contains("Props.TimelineGeneration", timeline);
        Assert.Contains("entry.Kind", timeline);
        Assert.Contains("entry.Id", timeline);
        Assert.DoesNotContain(".WithKey(entry.Id)", timeline);
        Assert.Matches(
            new Regex(@"WithKey\(RowKey\(entry\)\)"),
            timeline);
    }

    [Fact]
    public void ThinkingIndicator_UsesSyntheticGenerationQualifiedKey()
    {
        var timeline = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatTimeline.cs");

        Assert.Contains("string SyntheticRowKey(string id, ChatTimelineItemKind kind)", timeline);
        Assert.Contains("SyntheticRowKey(\"__thinking__\", ChatTimelineItemKind.Assistant)", timeline);
    }

    [Fact]
    public void TimelineGeneration_FlowsFromProviderSnapshotToTimelineProps()
    {
        var models = Read("src", "OpenClaw.Chat", "ChatModels.cs");
        var provider = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatDataProvider.cs");
        var root = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatRoot.cs");

        Assert.Contains("IReadOnlyDictionary<string, long>? TimelineGenerations = null", models);
        Assert.Contains("new Dictionary<string, long>(_resetVersions)", provider);
        Assert.Contains("TimelineGenerations: timelineGenerationsCopy", provider);
        Assert.Contains("snapshot.TimelineGenerations", root);
        Assert.Contains("TimelineGeneration: timelineGeneration", root);
    }

    [Fact]
    public void ResetClearPath_BumpsTimelineGenerationBeforeReusingEntryIds()
    {
        var provider = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatDataProvider.cs");

        Assert.Matches(
            new Regex(@"private\s+ResetClearPersistence\s+ClearThreadHistoryAfterResetLocked\(string\s+threadId\)[\s\S]*_resetVersions\[threadId\]\s*=\s*GetResetVersionLocked\(threadId\)\s*\+\s*1;[\s\S]*_timelines\[threadId\]\s*=\s*ChatTimelineState\.Initial\(\)\s*with\s*\{\s*HistoryLoaded\s*=\s*true\s*\};"),
            provider);
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(parts).ToArray()));
}
