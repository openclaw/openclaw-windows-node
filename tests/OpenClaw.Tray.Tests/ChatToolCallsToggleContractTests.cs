using System.Text.RegularExpressions;

namespace OpenClaw.Tray.Tests;

public sealed class ChatToolCallsToggleContractTests
{
    [Fact]
    public void ProductionTimeline_HonorsComposerToolCallVisibilityToggle()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "OpenClawChatTimeline.cs"));

        Assert.Matches(
            new Regex(@"var\s+showToolCalls\s*=\s*ChatVisualResolver\.ShowToolCalls\(\)\s*;"),
            source);
        Assert.DoesNotContain(
            "var showToolCalls    = !Props.EnableExplorationControls || ChatVisualResolver.ShowToolCalls();",
            source);
        Assert.Matches(
            new Regex(@"var\s+collapseToolChipsVersion\s*=\s*ChatExplorationState\.CollapseToolChipsVersion\s*;"),
            source);
    }

    [Fact]
    public void TaskListToolBurst_LabelsHeaderAndExpandedBodyForScreenReaders()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "OpenClawChatTimeline.cs"));

        Assert.Matches(
            new Regex(@"var\s+taskListHeaderAutomationName\s*=\s*\$""\{summaryLine\},\s*\{stepCountLabel\},\s*\{taskStatusText\},\s*\{"),
            source);
        Assert.Matches(
            new Regex(@"var\s+headerButton\s*=\s*Button\(headerContent,\s*toggleTaskList\)\s*\.AutomationName\(taskListHeaderAutomationName\)", RegexOptions.Singleline),
            source);
        Assert.Matches(
            new Regex(@"\.AutomationName\(\$""Tool steps for:\s*\{summaryLine\}\.\s*\{string\.Join\("";\s*"",\s*stepAutomationSummaries\)\}""\)", RegexOptions.Singleline),
            source);
    }

}
