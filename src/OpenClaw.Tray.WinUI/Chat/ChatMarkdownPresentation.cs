using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

/// <summary>Presentation hooks only. Parsing and sanitization remain in the timeline.</summary>
internal static class ChatMarkdownPresentation
{
    /// <summary>Only direct message blocks share rhythm; list and quote interiors remain untouched.</summary>
    internal static Element MessageBlocks(Element element) => element is StackElement stack
        ? stack with
        {
            Spacing = ChatVisuals.BlockGap,
            Children = stack.Children.Select(ResetBlockEdges).ToArray(),
        }
        : element;

    private static Element ResetBlockEdges(Element element)
    {
        var margin = element.Modifiers?.Margin ?? new Thickness(0);
        return element.Margin(margin.Left, 0, margin.Right, 0);
    }

    internal static Element Paragraph(Element element) => Format(element, ChatVisuals.BodySize, false);

    internal static Element Heading(int level, Element element) =>
        Format(element, level switch { 1 => 24, 2 => 20, _ => 16 }, true);

    private static Element Format(Element element, double size, bool heading)
    {
        if (element is RichTextBlockElement rich)
        {
            return (rich with
            {
                FontSize = size,
                FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
                LineHeight = heading ? size * 1.4 : ChatVisuals.BodyLineHeight,
                LineStackingStrategy = LineStackingStrategy.MaxHeight,
            })
                .Set(text => text.Style = (Style)Application.Current.Resources["ChatRichTextStyle"])
                .Margin(0, heading ? ChatVisuals.HeadingTopInset : 0, 0, ChatVisuals.BlockGap - 8);
        }
        return element.Margin(0, 0, 0, ChatVisuals.BlockGap - 8);
    }

    internal static Element CodeBlock(string code, string? language, Func<string, bool>? tryCopy = null)
    {
        var copyLabel = Localized("Chat_Code_Copy", "Copy code");
        return Border(VStack(
                8,
                Grid(
                    [GridSize.Star(), GridSize.Auto],
                    [GridSize.Auto],
                    TextBlock(language ?? string.Empty)
                        .FontSize(12)
                        .FontFamily(ChatVisuals.CodeFontFamily)
                        .Foreground(Theme.Ref("ChatSecondaryTextBrush"))
                        .TextTrimming(TextTrimming.CharacterEllipsis)
                        .VAlign(VerticalAlignment.Center)
                        .Grid(column: 0),
                    Component<ChatCopyButton, ChatCopyButtonProps>(new(
                            "code", code, copyLabel,
                            tryCopy, UseInstanceAutomationId: true))
                        .Grid(column: 1)),
                ScrollViewer((RichTextBlock(code) with { FontSize = ChatVisuals.CodeSize })
                        .Set(text =>
                        {
                            text.Style = (Style)Application.Current.Resources["ChatCodeTextStyle"];
                            text.FontFamily = new FontFamily(ChatVisuals.CodeFontFamily);
                            text.LineHeight = ChatVisuals.CodeLineHeight;
                            text.LineStackingStrategy = LineStackingStrategy.MaxHeight;
                            text.IsTextSelectionEnabled = true;
                            text.TextWrapping = TextWrapping.NoWrap;
                        }))
                    .MaxHeight(320)
                    .Set(scroll =>
                    {
                        scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                        scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                        scroll.HorizontalContentAlignment = HorizontalAlignment.Left;
                    })))
            .Padding(12, 8)
            .CornerRadius(ChatVisuals.SurfaceRadius)
            .BorderThickness(1)
            .BorderBrush(Theme.Ref("ChatStrokeBrush"))
            .Background(Theme.Ref("ChatCardBrush"))
            .Margin(0, 0, 0, ChatVisuals.BlockGap - 8)
            .AutomationName(language ?? Localized("Chat_Code_Label", "Code block"));
    }

    internal static string Localized(string key, string fallback)
    {
        var value = LocalizationHelper.GetString(key);
        return string.IsNullOrWhiteSpace(value) || value == key ? fallback : value;
    }
}
