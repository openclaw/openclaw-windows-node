// Body of the conversation side panel when it is opened with no tabs, matching the web
// Control UI's panel launcher. The chat root builds it with the bound panel admission:
//   ReactorChatPanelLauncher.Render(panels, panels.Revision, dashboardTarget)
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared.Dashboards;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

/// <summary>Lists the panels the current conversation can open; each entry opens its tab.</summary>
internal static class ReactorChatPanelLauncher
{
    /// <param name="revision">Bound admission revision; a stale click is rejected by the panel state.</param>
    /// <param name="dashboard">Session dashboard target, or null when no dashboard can open.</param>
    internal static Element Render(ChatConversationPanelState panels, long revision, BoardSessionTarget? dashboard)
    {
        var entries = new List<Element>();
        if (panels.AreSubagentsReady)
        {
            entries.Add(Entry(FluentIconCatalog.Folder, L("Chat_Files_Title", "Files"),
                L("Chat_Panel_LauncherFiles", "Files this session edited or read."),
                "ConversationPanelLauncherFiles", () => panels.OpenFiles(revision)));
            entries.Add(Entry(FluentIconCatalog.Subagent, L("Chat_Subagents_Title", "Subagents"),
                L("Chat_Panel_LauncherSubagents", "Follow delegated work from this conversation."),
                "ConversationPanelLauncherSubagents", () => panels.OpenSubagents(revision)));
        }
        if (dashboard is not null)
        {
            entries.Add(Entry(FluentIconCatalog.DashboardView, L("Chat_Panel_Dashboard", "Dashboard"),
                L("Chat_Panel_LauncherDashboard", "Widgets and tabs this session published."),
                "ConversationPanelLauncherDashboard", () => panels.OpenDashboard(dashboard)));
        }
        var empty = entries.Count == 0;
        if (empty)
        {
            entries.Add(TextBlock(L("Chat_Panel_LauncherEmpty", "No panels are available for this conversation yet."))
                .TextWrapping(TextWrapping.Wrap).Set(ChatVisuals.StyleSecondaryText)
                .Margin(ChatVisuals.BlockGap, 0)
                .AutomationId("ConversationPanelLauncherEmpty"));
        }
        // With no entries the launcher itself takes focus, so Escape and F6 still reach the panel.
        return ScrollViewer(VStack(ChatVisuals.CompactGap, entries.ToArray()).Margin(ChatVisuals.CompactGap, ChatVisuals.BlockGap))
            .Set(scroll => scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled)
            .IsTabStop(empty)
            .AutomationName(L("Chat_Panel_Launcher", "Explore panels"))
            .AutomationId("ConversationPanelLauncher");
    }

    private static Element Entry(string glyph, string title, string description, string automationId, Action open) =>
        Button(Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
                    TextBlock(glyph).FontFamily(FluentIconCatalog.SymbolThemeFontFamily).FontSize(16)
                        .VAlign(VerticalAlignment.Top).Margin(0, 2, 0, 0)
                        .AccessibilityView(AccessibilityView.Raw).Grid(column: 0),
                    VStack(2,
                            TextBlock(title).FontSize(ChatVisuals.BodySize),
                            TextBlock(description).FontSize(12).TextWrapping(TextWrapping.Wrap)
                                .Set(ChatVisuals.StyleSecondaryText))
                        .Margin(ChatVisuals.BlockGap + ChatVisuals.CompactGap, 0, 0, 0).Grid(column: 1)),
                () => open())
            .Resources(ChatVisuals.ToolbarButtonResources)
            .HAlign(HorizontalAlignment.Stretch)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .Padding(ChatVisuals.BlockGap)
            .AutomationName(title)
            .HelpText(description)
            .AutomationId(automationId)
            .WithKey(automationId);

    private static string L(string key, string fallback) => ReactorQuestionPanel.L(key, fallback);
}
