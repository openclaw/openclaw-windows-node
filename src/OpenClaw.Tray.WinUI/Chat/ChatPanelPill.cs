// Shared pill button above the composer that opens a conversation-panel tab. Pill owners keep
// their own visibility, counts and high-contrast watch; the chat root lays the pills out in a row:
//   ChatPanelPill.Render(icon, label, count, countId, name, "ChatFilesPill", highContrast, open);
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal static class ChatPanelPill
{
    /// <summary>Label plus a lighter count; text inherits the Button's state foreground for hover and High Contrast.</summary>
    internal static ButtonElement Render(Element icon, string label, string count, string countAutomationId,
        string accessibleName, string automationId, bool highContrast, Action open) =>
        Button(HStack(6, icon.VAlign(VerticalAlignment.Center),
                    TextBlock(label).FontSize(ChatVisuals.ToolRowFontSize).VAlign(VerticalAlignment.Center),
                    Muted(TextBlock(count).FontSize(ChatVisuals.ToolRowFontSize), highContrast)
                        .VAlign(VerticalAlignment.Center).AutomationId(countAutomationId))
                .WithKey(highContrast ? "pill-hc" : "pill"),
                open)
            .MinHeight(ChatVisuals.SubagentPillHeight).Padding(10, 2)
            .CornerRadius(ChatVisuals.SubagentPillHeight / 2)
            .Background(Theme.Ref("ChatCardBrush"))
            .BorderBrush(Theme.Ref("ChatStrokeBrush"))
            .BorderThickness(1)
            .Margin(0, ChatVisuals.CompactGap, 0, 0)
            .HAlign(HorizontalAlignment.Left)
            .ToolTip(accessibleName)
            .AutomationName(accessibleName)
            .AutomationId(automationId);

    /// <summary>Decorative de-emphasis that High Contrast drops.</summary>
    internal static TextBlockElement Muted(TextBlockElement text, bool highContrast) =>
        highContrast ? text : text.Foreground(Theme.Ref("ChatSecondaryTextBrush"));
}
