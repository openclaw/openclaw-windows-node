// Renders the catalog icon or a contrast-safe glyph. Used by PageHero.Header.
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Presentation;

/// <summary>Decorative page identity sharing the existing sidebar icon catalog.</summary>
internal static class PageHeroIcon
{
    public static Element Marker(
        string automationId, string glyph, string? pageTag, bool highContrast, string? glyphAutomationId)
    {
        var page = highContrast ? null : PinnedPages.Find(pageTag);
        Element content = page is null
            ? TextBlock(glyph).FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
                .FontSize(24).Foreground(Theme.Ref("TextOnAccentFillColorPrimaryBrush"))
                .AccessibilityView(AccessibilityView.Raw).VAlign(VerticalAlignment.Center)
            : Image(page.IconUri).Size(48, 48).AccessibilityHidden();
        var host = Border(content).Size(48, 48).CornerRadius(8);
        if (page is null)
            host = host.Background(Theme.Ref("AccentFillColorDefaultBrush"));
        return host.AutomationId(glyphAutomationId ?? $"{automationId}:Glyph")
            .Grid(column: 0).VAlign(VerticalAlignment.Top);
    }
}
