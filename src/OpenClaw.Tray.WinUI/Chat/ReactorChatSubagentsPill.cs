// Subagents pill above the composer. It summarizes the selected chat's direct subagents and
// opens the shared Subagents tab: Component<ReactorChatSubagentsPill, ReactorChatSubagentsPillProps>(new(panels, children)).
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using OpenClaw.Chat;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ReactorChatSubagentsPillProps(
    ChatConversationPanelState Panels, IReadOnlyList<ChatThread> Children);

/// <summary>Shown while subagents run or the Subagents tab is open; shows a live count and selects the tab.</summary>
internal sealed class ReactorChatSubagentsPill : Component<ReactorChatSubagentsPillProps>
{
    public override Element Render()
    {
        var panels = Props.Panels;
        var (_, setRevision) = UseState(0L, threadSafe: true);
        var (_, setThemeRevision) = UseState(0L, threadSafe: true);
        var themeRevision = UseRef(0L);
        // Read at render so a mode change while the pill was hidden is not stale; the watcher only re-renders.
        var highContrast = ChatThemeChanges.IsHighContrast();
        UseEffect((Func<Action>)(() =>
        {
            long revision = 0;
            void Changed() => setRevision(++revision);
            panels.Changed += Changed;
            return () => panels.Changed -= Changed;
        }), panels);

        if (Props.Children.Count == 0 || !panels.AreSubagentsReady)
            return Empty();

        var summary = ChatSubagentPresentation.Project(Props.Children, DateTimeOffset.UtcNow);
        // Settled subagents linger in the session list; show the pill only for live work or while
        // its tab is open, so a finished batch does not leave a permanent control above the composer.
        if (summary.RunningCount == 0 && !panels.Tabs.Contains(ChatConversationPanelState.SubagentsSlot))
            return Empty();
        var running = summary.RunningCount;
        // GitHub-style pill: label plus a lighter total; the spinner alone signals running work.
        var label = L("Chat_Subagents_Title", "Subagents");
        var count = summary.Total.ToString(System.Globalization.CultureInfo.CurrentCulture);
        var accessibleName = string.Format(L("Chat_Subagents_PillName", "Subagents: {0} running, {1} finished"),
            running, summary.Finished.Count);
        Element icon = running > 0
            ? ChatVisuals.SubagentSpinner(ChatVisuals.SubagentPillIconSize)
                .AccessibilityView(AccessibilityView.Raw).AutomationId("ChatSubagentsPillSpinner")
            : ChatPanelPill.Muted(TextBlock(FluentIconCatalog.Subagent).FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
                .FontSize(ChatVisuals.SubagentPillIconSize), highContrast)
                .AccessibilityView(AccessibilityView.Raw);
        var revisionAtRender = panels.Revision;
        // The chat root places this pill in the reading-width pill row.
        return ChatPanelPill.Render(icon, label, count, "ChatSubagentsPillCount", accessibleName, "ChatSubagentsPill",
                highContrast, () => panels.OpenSubagents(revisionAtRender))
            .OnMount(control => ChatThemeChanges.Watch((FrameworkElement)control,
                () => setThemeRevision(++themeRevision.Current)))
            .OnUnmount(control => ChatThemeChanges.Unwatch((FrameworkElement)control));
    }

    private static string L(string key, string fallback) => ReactorQuestionPanel.L(key, fallback);
}
