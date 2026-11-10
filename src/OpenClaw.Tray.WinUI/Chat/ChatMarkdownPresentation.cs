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
    /// <summary>
    /// Web-parity inline styling for rendered assistant Markdown: hyperlinks keep the
    /// Windows accent color without an underline, and inline code gets a subtle chip
    /// background. Reactor rebuilds inlines during reconcile, so the pass runs after
    /// it on the dispatcher. High contrast keeps the system's own link and text styles.
    /// </summary>
    internal static Element WithInlineStyling(Element element, bool compactSecondaryReading = false) =>
        Border(element).Set(border =>
            border.DispatcherQueue?.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => StyleInlines(border, compactSecondaryReading)));

    private static void StyleInlines(DependencyObject root, bool compactSecondaryReading)
    {
        if (root is RichTextBlock rich)
        {
            if (compactSecondaryReading && !IsCodeText(rich))
            {
                rich.Style = (Style)Application.Current.Resources["ChatSecondaryRichTextStyle"];
                rich.ClearValue(Microsoft.UI.Xaml.Controls.RichTextBlock.ForegroundProperty);
                rich.FontSize = ChatVisuals.ReasoningSize;
                rich.LineHeight = ChatVisuals.ReasoningLineHeight;
                rich.LineStackingStrategy = LineStackingStrategy.MaxHeight;
            }
            StyleRichText(rich);
            return;
        }
        if (compactSecondaryReading && root is TextBlock text && !IsCodeText(text))
        {
            ChatVisuals.StyleSecondaryText(text);
            text.FontSize = ChatVisuals.ReasoningSize;
            text.LineHeight = ChatVisuals.ReasoningLineHeight;
            text.LineStackingStrategy = LineStackingStrategy.MaxHeight;
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            StyleInlines(VisualTreeHelper.GetChild(root, index), compactSecondaryReading);
    }

    private static bool IsCodeText(FrameworkElement element) =>
        element switch
        {
            TextBlock text => text.FontFamily?.Source.Equals(ChatVisuals.CodeFontFamily, StringComparison.OrdinalIgnoreCase) == true,
            RichTextBlock rich => rich.FontFamily?.Source.Equals(ChatVisuals.CodeFontFamily, StringComparison.OrdinalIgnoreCase) == true,
            _ => false,
        };

    private static void StyleRichText(RichTextBlock rich)
    {
        var codeRanges = new List<Microsoft.UI.Xaml.Documents.TextRange>();
        var offset = 0;
        var first = true;
        foreach (var block in rich.Blocks)
        {
            if (block is not Microsoft.UI.Xaml.Documents.Paragraph paragraph)
                continue;
            if (!first)
                offset++;
            first = false;
            Walk(paragraph.Inlines);
        }

        void Walk(Microsoft.UI.Xaml.Documents.InlineCollection inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case Microsoft.UI.Xaml.Documents.Hyperlink link:
                        link.UnderlineStyle = Microsoft.UI.Xaml.Documents.UnderlineStyle.None;
                        Walk(link.Inlines);
                        break;
                    case Microsoft.UI.Xaml.Documents.Span span:
                        Walk(span.Inlines);
                        break;
                    case Microsoft.UI.Xaml.Documents.Run run:
                        var length = run.Text?.Length ?? 0;
                        // Only Reactor's inline-code runs set the code font locally;
                        // fenced code blocks inherit it from their own text block.
                        if (length > 0 &&
                            run.ReadLocalValue(Microsoft.UI.Xaml.Documents.TextElement.FontFamilyProperty) is FontFamily { Source: ChatVisuals.CodeFontFamily })
                            codeRanges.Add(new() { StartIndex = offset, Length = length });
                        offset += length;
                        break;
                    case Microsoft.UI.Xaml.Documents.LineBreak:
                        offset++;
                        break;
                }
            }
        }

        rich.TextHighlighters.Clear();
        if (codeRanges.Count == 0 || new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
            return;
        var isDark = rich.ActualTheme == ElementTheme.Dark;
        if (ThemeRef.Resolve("ControlFillColorSecondaryBrush", isDark) is not Brush background ||
            ThemeRef.Resolve("TextFillColorPrimaryBrush", isDark) is not Brush foreground)
            return;
        var highlighter = new Microsoft.UI.Xaml.Documents.TextHighlighter
        {
            Background = background,
            Foreground = foreground,
        };
        foreach (var range in codeRanges)
            highlighter.Ranges.Add(range);
        rich.TextHighlighters.Add(highlighter);
    }
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
