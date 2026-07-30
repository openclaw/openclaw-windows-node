namespace OpenClaw.Tray.Tests;

public sealed class ChatToolCallsToggleContractTests
{
    [Fact]
    public void ProductionTimeline_HonorsSettingsToolCallVisibilityToggle()
    {
        var root = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawReactorChatRoot.cs");
        var timeline = Read("src", "OpenClaw.Tray.WinUI", "Chat", "ReactorChatTimeline.cs");
        var app = Read("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");
        var settingsVm = Read("src", "OpenClaw.Tray.WinUI", "Presentation", "SettingsPageViewModel.cs");

        // Root still owns the shared tool-call visibility state and feeds it to
        // the timeline (independent of the composer).
        Assert.Contains("showToolCalls", root);
        Assert.Contains("toolCallsCollapseVersion", root);
        Assert.Contains("UseState(s_showToolCalls", root);
        Assert.Contains("UseState(s_toolCallsCollapseVersion", root);
        Assert.Contains("ToolCallsVisibilityChanged", root);

        // The single writer now lives on the root as a public static, invoked by
        // Settings and by startup seeding — no longer a composer callback.
        Assert.Contains("public static void SetToolCallsVisible(bool", root);
        Assert.Contains("s_showToolCalls = visible", root);
        Assert.DoesNotContain("OnShowToolCallsChanged", root);

        // Settings persists the preference and App applies every settings save to the
        // live timeline through the static writer.
        Assert.Contains("ShowChatToolCalls", settingsVm);
        Assert.Contains("OpenClawTray.Chat.OpenClawReactorChatRoot.SetToolCallsVisible", app);

        // Startup seeds visibility from the persisted setting.
        Assert.Contains("SetToolCallsVisible(_settings.ShowChatToolCalls)", app);

        // Timeline still consumes the props from the root.
        Assert.Contains("props.Timeline.ShowToolCalls", timeline);
        Assert.Contains("ToolCallsCollapseVersion", timeline);
        Assert.Contains("row.Props.Timeline.ShowToolCalls", timeline);
        Assert.Contains("row.IsAssistantRunEnd && row.Props.Timeline.ShowToolCalls", timeline);
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
