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

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(parts).ToArray()));
}
