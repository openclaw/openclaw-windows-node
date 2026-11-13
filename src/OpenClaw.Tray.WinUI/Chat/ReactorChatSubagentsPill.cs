// Subagents pill above the composer. It summarizes the selected chat's direct subagents and
// opens the shared Subagents tab: Component<ReactorChatSubagentsPill, ReactorChatSubagentsPillProps>(new(panels, children, width)).
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using OpenClaw.Chat;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ReactorChatSubagentsPillProps(
    ChatConversationPanelState Panels, IReadOnlyList<ChatThread> Children, double ViewportWidth);

/// <summary>Hidden without subagents; shows a live running count and selects the Subagents tab.</summary>
internal sealed class ReactorChatSubagentsPill : Component<ReactorChatSubagentsPillProps>
{
    public override Element Render()
    {
        var panels = Props.Panels;
        var (_, setRevision) = UseState(0L, threadSafe: true);
        var (highContrast, setHighContrast) = UseState(ChatThemeChanges.IsHighContrast());
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
        var running = summary.RunningCount;
        // GitHub-style pill: label plus a lighter total; the spinner alone signals running work.
        var label = L("Chat_Subagents_Title", "Subagents");
        var count = summary.Total.ToString(System.Globalization.CultureInfo.CurrentCulture);
        var accessibleName = string.Format(L("Chat_Subagents_PillName", "Subagents: {0} running, {1} finished"),
            running, summary.Finished.Count);
        var selected = panels.Selected == ChatConversationPanelState.SubagentsSlot;
        // Text inherits the Button's state foreground so hover and High Contrast stay legible;
        // only the decorative de-emphasis is local, and High Contrast drops it.
        TextBlockElement Muted(TextBlockElement text) =>
            highContrast ? text : text.Foreground(Theme.Ref("ChatSecondaryTextBrush"));
        Element icon = running > 0
            ? ProgressRing().IsActive(true)
                .Size(ChatVisuals.SubagentPillIconSize, ChatVisuals.SubagentPillIconSize)
                .AccessibilityView(AccessibilityView.Raw).AutomationId("ChatSubagentsPillSpinner")
            : Muted(TextBlock(FluentIconCatalog.Subagent).FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
                .FontSize(ChatVisuals.SubagentPillIconSize))
                .AccessibilityView(AccessibilityView.Raw);
        var revisionAtRender = panels.Revision;
        var gutter = ChatVisuals.Gutter(Props.ViewportWidth);
        return Border(
                Button(HStack(6, icon.VAlign(VerticalAlignment.Center),
                        TextBlock(label).FontSize(ChatVisuals.ToolRowFontSize)
                            .VAlign(VerticalAlignment.Center),
                        Muted(TextBlock(count).FontSize(ChatVisuals.ToolRowFontSize))
                            .VAlign(VerticalAlignment.Center).AutomationId("ChatSubagentsPillCount"))
                        .WithKey(highContrast ? "pill-hc" : "pill"),
                    () => panels.OpenSubagents(revisionAtRender))
                    .MinHeight(ChatVisuals.SubagentPillHeight).Padding(10, 2)
                    .CornerRadius(ChatVisuals.SubagentPillHeight / 2)
                    .Background(Theme.Ref("ChatCardBrush"))
                    .BorderBrush(Theme.Ref(selected ? "AccentFillColorDefaultBrush" : "ChatStrokeBrush"))
                    .BorderThickness(1)
                    .HAlign(HorizontalAlignment.Left)
                    .ToolTip(accessibleName)
                    .AutomationName(accessibleName)
                    .AutomationId("ChatSubagentsPill"))
            .MaxWidth(ChatVisuals.ReadingWidth)
            .Margin(gutter, ChatVisuals.CompactGap, gutter, 0)
            .HAlign(HorizontalAlignment.Stretch)
            .OnMount(control => ChatThemeChanges.Watch((FrameworkElement)control,
                () => setHighContrast(ChatThemeChanges.IsHighContrast())))
            .OnUnmount(control => ChatThemeChanges.Unwatch((FrameworkElement)control));
    }

    private static string L(string key, string fallback) => ReactorQuestionPanel.L(key, fallback);
}
