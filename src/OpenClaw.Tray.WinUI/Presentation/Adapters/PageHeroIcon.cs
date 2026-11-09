using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Presentation;

internal static class PageHeroIcon
{
    public static Element Marker(
        string automationId,
        string glyph,
        string? pageTag,
        bool highContrast,
        string? glyphAutomationId)
    {
        var page = highContrast ? null : PinnedPages.Find(pageTag);
        Element content;
        if (page is null)
        {
            content = TextBlock(glyph).FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
                .FontSize(24).Foreground(Theme.Ref("TextOnAccentFillColorPrimaryBrush"))
                .AccessibilityView(AccessibilityView.Raw).VAlign(VerticalAlignment.Center);
        }
        else
        {
            content = Image(page.IconUri).Size(48, 48).AccessibilityHidden();
        }

        var host = Border(content).Size(48, 48).CornerRadius(8);
        if (page is null)
            host = host.Background(Theme.Ref("AccentFillColorDefaultBrush"));

        return host
            .AutomationId(glyphAutomationId ?? $"{automationId}:Glyph")
            .Grid(column: 0).VAlign(VerticalAlignment.Top);
    }
}
