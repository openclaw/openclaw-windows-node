namespace OpenClaw.Tray.Tests;

public sealed class ChatTimelineVirtualizationContractTests
{
    [Fact]
    public void ProductionTimeline_UsesVirtualizedItemsRepeaterRows()
    {
        var timeline = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatTimeline.cs");
        var functionalUi = Read("src", "OpenClawTray.FunctionalUI", "FunctionalUI.cs");

        Assert.Contains("VirtualVStack(2, timelineRows)", timeline);
        Assert.DoesNotContain("                        VStack(2, timelineRows)", timeline);
        Assert.Contains("UseRef<FrameworkElement?>", timeline);
        Assert.Contains("ItemsRepeater repeater", timeline);

        Assert.Contains("VirtualStackElement", functionalUi);
        Assert.Contains("ItemsRepeater", functionalUi);
        Assert.Contains("StackLayout", functionalUi);
        Assert.Contains("IElementFactory", functionalUi);
    }

    [Fact]
    public void ProductionTimeline_StabilizesFollowToBottomForVirtualizedRows()
    {
        var timeline = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatTimeline.cs");

        Assert.Contains("FollowToBottomMaxStabilizationPasses", timeline);
        Assert.Contains("FollowToBottomExtentEpsilon", timeline);
        Assert.Contains("void QueuePass(", timeline);
        Assert.Contains("sv.UpdateLayout();", timeline);
        Assert.Contains("QueuePass(pass + 1", timeline);
    }

    [Fact]
    public void FunctionalUiVirtualStack_IsPruneAwareAndStableAcrossRenderChurn()
    {
        var functionalUi = Read("src", "OpenClawTray.FunctionalUI", "FunctionalUI.cs");

        Assert.Contains("_virtualStackOwnedPathPrefixes", functionalUi);
        Assert.Contains("_visitedVirtualStackPaths", functionalUi);
        Assert.Contains("IsOwnedByVirtualStack", functionalUi);
        Assert.Contains("VirtualStackItemsMatch", functionalUi);
        Assert.Contains("UpdateRealizedVirtualStackItems", functionalUi);
        Assert.Contains("repeater.ItemTemplate is not VirtualStackItemTemplate", functionalUi);
        Assert.Contains("PruneUnvisitedPaths();", functionalUi);
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(parts).ToArray()));
}
