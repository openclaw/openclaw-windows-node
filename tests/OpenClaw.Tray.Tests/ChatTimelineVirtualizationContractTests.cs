namespace OpenClaw.Tray.Tests;

public sealed class ChatTimelineVirtualizationContractTests
{
    [Fact]
    public void ProductionTimeline_UsesVirtualizedItemsRepeaterRows()
    {
        var timeline = Read("src", "OpenClaw.Tray.WinUI", "Chat", "ChatTimeline.cs");
        var virtualizedView = Read("src", "OpenClaw.Tray.WinUI", "Chat", "Controls", "VirtualizedChatView.cs");
        var functionalUi = Read("src", "OpenClawTray.FunctionalUI", "FunctionalUI.cs");

        Assert.Contains("new VirtualizedChatView()", timeline);
        Assert.DoesNotContain("                        VStack(2, timelineRows)", timeline);
        Assert.Contains("ItemsRepeater", virtualizedView);
        Assert.Contains("StackLayout", virtualizedView);

        Assert.Contains("NativeElement", functionalUi);
        Assert.Contains("ConfigureNative", functionalUi);
        Assert.Contains("NativeIdentityProperty", functionalUi);
    }

    [Fact]
    public void ProductionTimeline_StabilizesFollowToBottomForVirtualizedRows()
    {
        var virtualizedView = Read("src", "OpenClaw.Tray.WinUI", "Chat", "Controls", "VirtualizedChatView.cs");

        Assert.Contains("RequiredStableRestorePasses", virtualizedView);
        Assert.Contains("ApplyPendingRestoreIfReady", virtualizedView);
        Assert.Contains("QueueScrollToBottom", virtualizedView);
        Assert.Contains("EnqueueOnView", virtualizedView);
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
        Assert.Contains("RemoveCachedSubtree(item.Path)", functionalUi);
        Assert.Contains("item.RealizedContainer = null", functionalUi);
        Assert.Contains("PruneUnvisitedCachedSubtree(item.Path)", functionalUi);
        Assert.Contains("PruneUnvisitedPaths();", functionalUi);
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(parts).ToArray()));
}
