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

        // Follow-to-bottom for the virtualized ItemsRepeater is delivered by three cooperating
        // mechanisms; guard that all three stay present so a refactor can't silently drop back to
        // the old scroll-fighting behavior (PR #1014 / issue #996):
        //   1. WinUI scroll anchoring pins the bottom row pre-paint as the extent grows in place.
        Assert.Contains("sv.VerticalAnchorRatio = 1.0", timeline);
        //   2. A self-terminating settle timer chases LATE extent corrections, converging on being
        //      at the bottom rather than requiring an (never-settling) stable extent estimate.
        Assert.Contains("FollowToBottomMaxSettleTicks", timeline);
        Assert.Contains("FollowToBottomSettleStableTicks", timeline);
        Assert.Contains("scrollSettleTimerRef", timeline);
        //   3. Reactive follows are coalesced and unowned ViewChanging transitions are authoritative
        //      user intent, so follow machinery never clobbers a wheel/drag transition.
        Assert.Contains("scrollPinPendingRef", timeline);
        Assert.Contains("programmaticScrollRef", timeline);
        Assert.Contains("sv.ViewChanging +=", timeline);
        Assert.Contains("CancelAutoFollowForUser", timeline);
        Assert.Contains("TimelineScrollIntent.NotifyUserIntent", timeline);
        Assert.Contains("UIElement.PointerWheelChangedEvent", timeline);
        Assert.Contains("handledEventsToo: true", timeline);
        Assert.Contains("sv.UpdateLayout();", timeline);
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
