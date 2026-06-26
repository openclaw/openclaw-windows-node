using System.Text.RegularExpressions;

namespace OpenClaw.Tray.Tests;

public sealed class ChatToolCallsToggleContractTests
{
    [Fact]
    public void ProductionTimeline_HonorsComposerToolCallVisibilityToggle()
    {
        var root = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatRoot.cs");
        var composer = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawComposer.cs");
        var timeline = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatTimeline.cs");

        Assert.Contains("ShowToolCalls: showToolCalls.Value", root);
        Assert.Contains("ToolCallsCollapseVersion: toolCallsCollapseVersion.Value", root);
        Assert.Contains("OnShowToolCallsChanged: visible =>", root);
        Assert.Contains("UseState(s_showToolCalls", root);
        Assert.Contains("UseState(s_toolCallsCollapseVersion", root);
        Assert.Contains("s_showToolCalls = visible", root);
        Assert.Contains("ToolCallsVisibilityChanged", root);
        Assert.Contains("bool ShowToolCalls = true", composer);
        Assert.Contains("Action<bool>? OnShowToolCallsChanged = null", composer);
        Assert.Matches(new Regex(@"var\s+showToolCalls\s*=\s*Props\.ShowToolCalls\s*;"), timeline);
        Assert.Matches(new Regex(@"var\s+collapseToolChipsVersion\s*=\s*Props\.ToolCallsCollapseVersion\s*;"), timeline);
    }

    [Fact]
    public void ChatExplorationDesignSurface_IsRemoved()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var chatRoot = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatRoot.cs");
        var timeline = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatTimeline.cs");

        Assert.False(Directory.Exists(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Chat", "Explorations")));
        Assert.False(File.Exists(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Windows", "ChatExplorationsWindow.cs")));
        Assert.DoesNotContain("ChatExploration", chatRoot);
        Assert.DoesNotContain("ChatExploration", timeline);
        Assert.DoesNotContain("ToolBurstStyle", timeline);
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(parts).ToArray()));
}
