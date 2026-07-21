using System.Text.RegularExpressions;

namespace OpenClaw.Tray.Tests;

public sealed class ChatToolCallsToggleContractTests
{
    [Fact]
    public void ProductionTimeline_HonorsSettingsToolCallVisibilityToggle()
    {
        var root = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatRoot.cs");
        var composer = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawComposer.cs");
        var timeline = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatTimeline.cs");
        var app = Read("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");

        // Root still owns the shared tool-call visibility state and feeds it to
        // the timeline (independent of the composer).
        Assert.Contains("ShowToolCalls: showToolCalls.Value", root);
        Assert.Contains("ToolCallsCollapseVersion: toolCallsCollapseVersion.Value", root);
        Assert.Contains("UseState(s_showToolCalls", root);
        Assert.Contains("UseState(s_toolCallsCollapseVersion", root);
        Assert.Contains("ToolCallsVisibilityChanged", root);

        // The single writer now lives on the root as a public static, invoked by
        // Settings and by startup seeding — no longer a composer callback.
        Assert.Contains("public static void SetToolCallsVisible(bool", root);
        Assert.Contains("s_showToolCalls = visible", root);
        Assert.DoesNotContain("OnShowToolCallsChanged", root);

        // The composer no longer hosts the tool-call toggle at all.
        Assert.DoesNotContain("ShowToolCalls", composer);
        Assert.DoesNotContain("OnShowToolCallsChanged", composer);

        // Settings drives it: the settings view model persists the setting and pushes it into
        // the live timeline through IAppCommands, which App forwards to the static writer.
        var settingsVm = Read("src", "OpenClaw.Tray.WinUI", "Presentation", "SettingsPageViewModel.cs");
        Assert.Contains("SetChatToolCallsVisible", settingsVm);
        Assert.Contains("ShowChatToolCalls", settingsVm);
        Assert.Contains("OpenClawTray.Chat.OpenClawChatRoot.SetToolCallsVisible", app);

        // Startup seeds visibility from the persisted setting.
        Assert.Contains("SetToolCallsVisible(_settings.ShowChatToolCalls)", app);

        // Timeline still consumes the props from the root.
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
