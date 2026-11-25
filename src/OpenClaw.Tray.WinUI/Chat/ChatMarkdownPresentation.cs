using System.Runtime.CompilerServices;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Markdown;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Helpers;
using FunctionalTheme = OpenClawTray.FunctionalUI.Core.Theme;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

/// <summary>Presentation hooks only. Parsing and sanitization remain in the timeline.</summary>
internal static class ChatMarkdownPresentation
{
    /// <summary>
    /// Web-parity inline styling for rendered assistant Markdown: hyperlinks keep the
    /// Windows accent color without an underline, inline code gets a subtle chip
    /// background, and prose and bold runs take the web's text tones. Reactor rebuilds
    /// inlines during reconcile, so the pass runs after it on the dispatcher, and again
    /// when the element's theme changes because highlighter brushes are theme-resolved.
    /// High contrast keeps the system's own link and text styles.
    /// </summary>
    internal static Element WithInlineStyling(Element element, bool compactSecondaryReading = false) =>
        Border(element)
            .Set(border =>
            {
                // A fresh state per render; queued passes from older renders or mounts no-op.
                var state = new InlineStylingState(compactSecondaryReading);
                s_inlineStyling.AddOrUpdate(border, state);
                QueueInlineStyling(border, state);
            })
            .OnMountAdd(control =>
            {
                control.ActualThemeChanged += OnInlineStylingThemeChanged;
                WatchContrast(control);
            })
            .OnUnmountAdd(control =>
            {
                control.ActualThemeChanged -= OnInlineStylingThemeChanged;
                UnwatchContrast(control);
                s_inlineStyling.Remove((Border)control);
            });

    internal static Element Disclosures(
        IReadOnlyList<ChatMarkdownPart> parts, Func<string, Element> renderMarkdown)
    {
        if (parts is [ChatMarkdownText markdown])
            return renderMarkdown(markdown.Text);
        return VStack(ChatVisuals.BlockGap, parts.Select((part, index) => part switch
        {
            ChatMarkdownText text => renderMarkdown(text.Text),
            ChatMarkdownDisclosure disclosure =>
                Component<ChatMarkdownDisclosureView, ChatMarkdownDisclosureProps>(
                    new(disclosure, renderMarkdown)).WithKey($"disclosure:{index}"),
            _ => throw new InvalidOperationException("Unknown chat Markdown part."),
        }).ToArray());
    }

    private sealed record InlineStylingState(bool Compact);

    private static readonly ConditionalWeakTable<Border, InlineStylingState> s_inlineStyling = new();

    private static void OnInlineStylingThemeChanged(FrameworkElement sender, object args) =>
        RequeueInlineStyling((Border)sender);

    private static void RequeueInlineStyling(Border border)
    {
        if (s_inlineStyling.TryGetValue(border, out var state))
            QueueInlineStyling(border, state);
    }

    private static void QueueInlineStyling(Border border, InlineStylingState state) =>
        border.DispatcherQueue?.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (s_inlineStyling.TryGetValue(border, out var current) && ReferenceEquals(current, state))
                    StyleInlines(border, state.Compact);
            });

    /// <summary>High Contrast probe; tests may substitute it to exercise contrast transitions.</summary>
    internal static Func<bool> IsHighContrast
    {
        get => ChatThemeChanges.IsHighContrast;
        set => ChatThemeChanges.IsHighContrast = value;
    }

    private static void WatchContrast(FrameworkElement target) =>
        ChatThemeChanges.Watch(target, () =>
        {
            if (target is Border border)
                RequeueInlineStyling(border);
            else if (target is RichTextBlock code)
                RefreshCodeSyntax(code);
        });

    private static void UnwatchContrast(FrameworkElement target) => ChatThemeChanges.Unwatch(target);

    /// <summary>Restyles every mounted Markdown and code surface on its own UI thread.</summary>
    internal static void RefreshAfterContrastChange() => ChatThemeChanges.Refresh();

    private static void StyleInlines(DependencyObject root, bool compactSecondaryReading, bool quoted = false)
    {
        quoted |= root is FrameworkElement element && s_quotes.TryGetValue(element, out _);
        if (root is RichTextBlock rich)
        {
            // Fenced code owns its syntax highlighters; see ApplyCodeSyntax. Only its font follows the theme here.
            if (IsCodeText(rich))
            {
                ChatVisuals.ApplyCodeFont(rich);
                return;
            }
            ChatVisuals.ApplyBodyFont(rich);
            // Tight list items and table cells bypass the Paragraph hook; give them the prose tone.
            if (!compactSecondaryReading && rich.Style is null)
            {
                rich.Style = (Style)Application.Current.Resources["ChatProseRichTextStyle"];
                rich.ClearValue(Microsoft.UI.Xaml.Controls.RichTextBlock.ForegroundProperty);
            }
            if (compactSecondaryReading)
            {
                rich.Style = (Style)Application.Current.Resources["ChatSecondaryRichTextStyle"];
                rich.ClearValue(Microsoft.UI.Xaml.Controls.RichTextBlock.ForegroundProperty);
                rich.FontSize = ChatVisuals.ReasoningSize;
                rich.LineHeight = ChatVisuals.ReasoningLineHeight;
                rich.LineStackingStrategy = LineStackingStrategy.MaxHeight;
            }
            var muted = quoted && !IsHighContrast()
                ? ThemeRef.Resolve("ChatMutedTextBrush", rich.ActualTheme == ElementTheme.Dark) as Brush
                : null;
            if (muted is not null)
            {
                rich.Foreground = muted;
                s_quotedText.AddOrUpdate(rich, s_quotedText);
            }
            else if (s_quotedText.TryGetValue(rich, out _))
            {
                // Pooled controls can move out of a quote; drop the quote tone we set.
                rich.ClearValue(Microsoft.UI.Xaml.Controls.RichTextBlock.ForegroundProperty);
                s_quotedText.Remove(rich);
            }
            StyleRichText(rich, compactSecondaryReading, muted);
            return;
        }
        // Code labels carry the code font, and list markers, disclosure headers and inert labels the body font,
        // locally; both follow theme switches.
        // A body font is still required locally, so a pooled TextBlock reused for an icon glyph keeps its icon font.
        if (root is TextBlock codeText && IsCodeText(codeText))
            ChatVisuals.ApplyCodeFont(codeText);
        else if (root is TextBlock bodyText &&
            ChatVisuals.IsBodyFont(bodyText.ReadLocalValue(Microsoft.UI.Xaml.Controls.TextBlock.FontFamilyProperty) as FontFamily))
            ChatVisuals.ApplyBodyFont(bodyText);
        if (root is TextBlock text && compactSecondaryReading && !IsCodeText(text))
        {
            ChatVisuals.StyleSecondaryText(text);
            text.FontSize = ChatVisuals.ReasoningSize;
            text.LineHeight = ChatVisuals.ReasoningLineHeight;
            text.LineStackingStrategy = LineStackingStrategy.MaxHeight;
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            StyleInlines(VisualTreeHelper.GetChild(root, index), compactSecondaryReading, quoted);
    }

    // Text given the code or body font here keeps that role, because a plugin or imported theme may use one font
    // stack for both. Other text falls back to its local font (prose may inherit a monospace theme font, so only the
    // local value counts), and a font that served as both is prose.
    private static bool IsCodeText(FrameworkElement element)
    {
        if (ChatVisuals.HasCodeRole(element) is { } code)
            return code;
        var local = element switch
        {
            TextBlock text => text.ReadLocalValue(Microsoft.UI.Xaml.Controls.TextBlock.FontFamilyProperty),
            RichTextBlock rich => rich.ReadLocalValue(Microsoft.UI.Xaml.Controls.RichTextBlock.FontFamilyProperty),
            _ => null,
        } as FontFamily;
        return ChatVisuals.IsCodeFont(local) && !ChatVisuals.IsBodyFont(local);
    }

    private static void StyleRichText(RichTextBlock rich, bool compactSecondaryReading, Brush? quoteText)
    {
        var codeRanges = new List<Microsoft.UI.Xaml.Documents.TextRange>();
        var strongCodeRanges = new List<Microsoft.UI.Xaml.Documents.TextRange>();
        var strongRanges = new List<Microsoft.UI.Xaml.Documents.TextRange>();
        var offset = 0;
        var first = true;
        foreach (var block in rich.Blocks)
        {
            if (block is not Microsoft.UI.Xaml.Documents.Paragraph paragraph)
                continue;
            if (!first)
                offset++;
            first = false;
            Walk(paragraph.Inlines, strong: false, link: false);
        }

        void Walk(Microsoft.UI.Xaml.Documents.InlineCollection inlines, bool strong, bool link)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case Microsoft.UI.Xaml.Documents.Hyperlink hyperlink:
                        hyperlink.UnderlineStyle = Microsoft.UI.Xaml.Documents.UnderlineStyle.None;
                        Walk(hyperlink.Inlines, strong, link: true);
                        break;
                    case Microsoft.UI.Xaml.Documents.Span span:
                        Walk(span.Inlines,
                            strong || span is Microsoft.UI.Xaml.Documents.Bold || span.FontWeight.Weight >= FontWeights.SemiBold.Weight,
                            link);
                        break;
                    case Microsoft.UI.Xaml.Documents.Run run:
                        var length = run.Text?.Length ?? 0;
                        var strongRun = strong || run.FontWeight.Weight >= FontWeights.SemiBold.Weight;
                        // Only Reactor's inline-code runs set the code font locally;
                        // fenced code blocks inherit it from their own text block.
                        if (length > 0 &&
                            ChatVisuals.IsCodeFont(run.ReadLocalValue(Microsoft.UI.Xaml.Documents.TextElement.FontFamilyProperty) as FontFamily))
                        {
                            ChatVisuals.ApplyCodeFont(run);
                            (strongRun ? strongCodeRanges : codeRanges).Add(new() { StartIndex = offset, Length = length });
                        }
                        else if (length > 0 && strongRun && !link && !compactSecondaryReading)
                            strongRanges.Add(new() { StartIndex = offset, Length = length });
                        offset += length;
                        break;
                    case Microsoft.UI.Xaml.Documents.LineBreak:
                        offset++;
                        break;
                }
            }
        }

        rich.TextHighlighters.Clear();
        if (codeRanges.Count + strongCodeRanges.Count + strongRanges.Count == 0 || IsHighContrast())
            return;
        // Prose and strong text share the primary tone (strong keeps its weight); inline
        // code keeps its surrounding tone on a subtle chip, which a Gateway theme paints with its InlineCode color.
        var isDark = rich.ActualTheme == ElementTheme.Dark;
        if (ThemeRef.Resolve("SubtleFillColorTransparentBrush", isDark) is not Brush clear ||
            ThemeRef.Resolve("TextFillColorPrimaryBrush", isDark) is not Brush strongText ||
            ThemeRef.Resolve("TextFillColorPrimaryBrush", isDark) is not Brush proseText)
            return;
        // Quoted strong text and code keep the quote's muted tone, as on the web: strong text
        // inherits the quote foreground directly, so it gets no tone highlighter of its own.
        if (quoteText is not null)
        {
            strongText = proseText = quoteText;
            strongRanges.Clear();
        }
        AddHighlighter(strongRanges, clear, strongText);
        if (codeRanges.Count == 0 && strongCodeRanges.Count == 0)
            return;

        // A Gateway theme's InlineCode color wins; otherwise resolve the Fluent chip for the mounted chat's own mode,
        // because the application brush can carry the opposite palette.
        var chip = new SolidColorBrush(ChatVisuals.ThemeInlineCode(isDark) is { } themed
            ? themed
            : FunctionalTheme.ResolveColor("ControlFillColorSecondary", rich.ActualTheme));
        AddHighlighter(codeRanges, chip, proseText);
        AddHighlighter(strongCodeRanges, chip, strongText);

        void AddHighlighter(List<Microsoft.UI.Xaml.Documents.TextRange> ranges, Brush background, Brush foreground)
        {
            if (ranges.Count == 0)
                return;
            var highlighter = new Microsoft.UI.Xaml.Documents.TextHighlighter
            {
                Background = background,
                Foreground = foreground,
            };
            foreach (var range in ranges)
                highlighter.Ranges.Add(range);
            rich.TextHighlighters.Add(highlighter);
        }
    }

    /// <summary>Only direct message blocks share rhythm; list and quote interiors remain untouched.</summary>
    internal static Element MessageBlocks(Element element, bool compactSecondaryReading = false) =>
        element is StackElement stack
            ? stack with
            {
                // Compact thinking text keeps its tighter pre-web rhythm.
                Spacing = compactSecondaryReading ? ChatVisuals.BlockGap : ChatVisuals.MessageBlockGap,
                Children = stack.Children
                    .Select((child, index) => ResetBlockEdges(StyleNestedTables(child), index == 0 || compactSecondaryReading))
                    .ToArray(),
            }
            : element;

    private static Element StyleNestedTables(Element element)
    {
        switch (element)
        {
            case StackElement stack:
                var stackChildren = StyleNestedTables(stack.Children);
                return stackChildren is null ? stack : stack with { Children = stackChildren };
            case GridElement grid:
                var gridChildren = StyleNestedTables(grid.Children);
                return Table(gridChildren is null ? grid : grid with { Children = gridChildren });
            case BorderElement { Child: { } child } border:
                var styledChild = StyleNestedTables(child);
                return ReferenceEquals(child, styledChild) ? border : border with { Child = styledChild };
            default:
                return element;
        }
    }

    private static Element[]? StyleNestedTables(IReadOnlyList<Element> children)
    {
        Element[]? styledChildren = null;
        for (var index = 0; index < children.Count; index++)
        {
            var child = children[index];
            var styledChild = StyleNestedTables(child);
            if (ReferenceEquals(child, styledChild))
                continue;
            styledChildren ??= children.ToArray();
            styledChildren[index] = styledChild;
        }
        return styledChildren;
    }

    // The first and last blocks add no outer gap; only later headings keep their extra lead-in.
    private static Element ResetBlockEdges(Element element, bool dropTop)
    {
        var margin = element.Modifiers?.Margin ?? new Thickness(0);
        var top = !dropTop && s_headings.TryGetValue(element, out _) ? margin.Top : 0;
        return element.Margin(margin.Left, top, margin.Right, 0);
    }

    private static readonly ConditionalWeakTable<Element, object> s_headings = new();

    internal static Element Paragraph(Element element) => Format(element, ChatVisuals.BodySize, false);

    internal static Element Heading(int level, Element element)
    {
        var heading = Format(element, level switch { 1 => 24, 2 => 20, _ => 16 }, true);
        s_headings.AddOrUpdate(heading, s_headings);
        return heading;
    }

    internal static Element List(Element[] items, bool compactSecondaryReading = false) =>
        VStack(compactSecondaryReading ? ChatVisuals.CompactListItemGap : ChatVisuals.ListItemGap, items);

    /// <summary>
    /// Keeps Reactor's Auto/Star list row (so content still wraps) and only places its marker in
    /// the web's right-aligned marker column: muted bullets, prose-toned numbers and task boxes.
    /// Tight-list text takes the prose tone here so it never flashes the primary tone.
    /// </summary>
    internal static Element ListItem(Element row, bool compactSecondaryReading = false)
    {
        if (row is not GridElement { Children: [TextBlockElement marker, var content] } grid)
            return row;
        var column = (marker with { TextAlignment = TextAlignment.Right })
            .FontFamily(ChatVisuals.BodyFontFamily)
            .MinWidth(ChatVisuals.ListMarkerColumn)
            .Set(text => StyleListMarker(text, compactSecondaryReading))
            .OnUnmountAdd(ChatVisuals.ClearFontRole);
        if (!compactSecondaryReading && content is RichTextBlockElement rich && !s_headings.TryGetValue(content, out _))
        {
            var prose = (Style)Application.Current.Resources["ChatProseRichTextStyle"];
            content = rich.Set(text => text.Style = prose);
        }
        return grid with { Children = [column, content] };
    }

    private static void StyleListMarker(TextBlock text, bool compactSecondaryReading)
    {
        var marker = text.Text ?? string.Empty;
        ChatVisuals.ApplyBodyFont(text);
        var style = compactSecondaryReading || !marker.StartsWith('\u2022')
            ? "ChatSecondaryTextStyle"
            : "ChatMutedTextStyle";
        text.Style = (Style)Application.Current.Resources[style];
        text.ClearValue(Microsoft.UI.Xaml.Controls.TextBlock.ForegroundProperty);
        // Task boxes: the body font lacks U+2610/U+2611, and its fallback draws the checked box
        // as a color emoji. Segoe UI Symbol keeps both states monochrome and the same size.
        if (marker.StartsWith('\u2610') || marker.StartsWith('\u2611'))
            text.FontFamily = new FontFamily(ChatVisuals.TaskMarkerFontFamily);
    }

    internal static Element ThematicBreak() =>
        Border(Empty())
            .Height(1)
            .Background(Theme.Ref("DividerStrokeColorDefaultBrush"))
            .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

    // Quote containers mounted by BlockQuote, and quoted text that StyleInlines toned.
    private static readonly ConditionalWeakTable<FrameworkElement, object> s_quotes = new();
    private static readonly ConditionalWeakTable<RichTextBlock, object> s_quotedText = new();

    /// <summary>
    /// Web-parity block quote: Reactor's filled box becomes unfilled content beside a rounded
    /// strong-stroke bar. The inline styling pass gives the quoted text its muted tone.
    /// </summary>
    internal static Element BlockQuote(Element quote)
    {
        if (quote is not BorderElement { Child: { } content })
            return quote;
        return Grid(
                [GridSize.Auto, GridSize.Star()],
                [GridSize.Auto],
                Border(Empty())
                    .Width(ChatVisuals.QuoteBarWidth)
                    .CornerRadius(ChatVisuals.QuoteBarWidth / 2)
                    .Margin(0, ChatVisuals.QuoteBarInset, 0, ChatVisuals.QuoteBarInset)
                    .Background(Theme.Ref("ControlStrongStrokeColorDefaultBrush"))
                    .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw)
                    .Grid(column: 0),
                content
                    .Margin(ChatVisuals.QuoteHorizontalInset - ChatVisuals.QuoteBarWidth,
                        ChatVisuals.QuoteVerticalInset, ChatVisuals.QuoteHorizontalInset,
                        ChatVisuals.QuoteVerticalInset)
                    .Grid(column: 1))
            .Set(grid => s_quotes.AddOrUpdate(grid, s_quotes))
            .OnUnmountAdd(control => s_quotes.Remove((FrameworkElement)control));
    }

    /// <summary>
    /// Web-parity table: Reactor's message-level table grid (Auto rows, Star columns, so long cells
    /// keep wrapping at narrow widths) gets a rounded frame, 1px cell rules and a tinted header row.
    /// Reactor's Table hook only receives rows with TextSelection enabled, so the built grid is
    /// restyled instead; the recursive element pass applies this to tables nested in lists or quotes.
    /// </summary>
    private static Element Table(Element block)
    {
        if (block is not GridElement { Definition: { Rows.Length: > 0 } definition } grid ||
            definition.Columns.Any(column => column.Type != GridUnitType.Star))
            return block;
        var columns = definition.Columns.Length;
        var rows = definition.Rows.Length;
        var stroke = Theme.Ref("ChatStrokeBrush");
        var inner = ChatVisuals.TableRadius - 1;
        // GFM tables always start with exactly one header row.
        Element header = Border(Empty())
            .Background(Theme.Ref("SubtleFillColorSecondaryBrush"))
            .CornerRadius(inner, inner, 0, 0)
            .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw)
            .Grid(row: 0, column: 0, columnSpan: columns);
        var cells = grid.Children.Select(cell =>
        {
            var at = cell.Attached?.GetValueOrDefault(typeof(GridAttached)) as GridAttached ?? new GridAttached();
            return (Element)Border(cell)
                .BorderBrush(stroke)
                .BorderThickness(0, 0, at.Column == columns - 1 ? 0 : 1, at.Row == rows - 1 ? 0 : 1)
                .Grid(row: at.Row, column: at.Column);
        });
        var frame = (BorderElement)Border(grid with { Children = [header, .. cells] })
            .CornerRadius(ChatVisuals.TableRadius)
            .BorderThickness(1)
            .BorderBrush(stroke)
            .Background(Theme.Ref("ChatCardBrush"));
        return frame with { Attached = grid.Attached };
    }

    private static Element Format(Element element, double size, bool heading)
    {
        if (element is RichTextBlockElement rich)
        {
            return (rich with
            {
                FontFamily = new FontFamily(ChatVisuals.BodyFontFamily),
                FontSize = size,
                FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
                LineHeight = heading ? size * 1.4 : ChatVisuals.BodyLineHeight,
                LineStackingStrategy = LineStackingStrategy.MaxHeight,
            })
                .Set(text =>
                {
                    text.Style = (Style)Application.Current.Resources[heading ? "ChatRichTextStyle" : "ChatProseRichTextStyle"];
                    ChatVisuals.ApplyBodyFont(text);
                })
                .OnUnmountAdd(ChatVisuals.ClearFontRole)
                .Margin(0, heading ? ChatVisuals.HeadingTopInset : 0, 0, 0);
        }
        return element.Margin(0, 0, 0, 0);
    }

    internal static Element CodeBlock(string code, string? language, Func<string, bool>? tryCopy = null)
    {
        var copyLabel = Localized("Chat_Code_Copy", "Copy code");
        var label = string.IsNullOrWhiteSpace(language)
            ? Localized("Chat_Code_LanguageFallback", "code")
            : language.ToLowerInvariant();
        var codeBody = (RichTextBlock(code) with { FontSize = ChatVisuals.CodeSize })
            .Set(text =>
            {
                text.Style = (Style)Application.Current.Resources["ChatCodeTextStyle"];
                ChatVisuals.ApplyCodeFont(text);
                text.LineHeight = ChatVisuals.CodeLineHeight;
                text.LineStackingStrategy = LineStackingStrategy.MaxHeight;
                text.IsTextSelectionEnabled = true;
                text.TextWrapping = TextWrapping.NoWrap;
                ApplyCodeSyntax(text, code, language);
            })
            .OnMountAdd(control =>
            {
                control.Loaded += OnCodeLoaded;
                control.ActualThemeChanged += OnCodeThemeChanged;
                WatchContrast(control);
            })
            .OnUnmountAdd(control =>
            {
                control.Loaded -= OnCodeLoaded;
                control.ActualThemeChanged -= OnCodeThemeChanged;
                UnwatchContrast(control);
                ClearCodeSyntax((RichTextBlock)control);
                ChatVisuals.ClearFontRole(control);
            })
            .Margin(ChatVisuals.CodeInset - 6, 0, ChatVisuals.CodeInset - 6, ChatVisuals.CodeInset - 6);
        return Border(VStack(
                0,
                Grid(
                    [GridSize.Star(), GridSize.Auto],
                    [GridSize.Auto],
                    TextBlock(label)
                        .FontSize(ChatVisuals.CodeLabelSize)
                        .FontFamily(ChatVisuals.CodeFontFamily)
                        .Set(text => ChatVisuals.ApplyCodeFont(text))
                        .OnUnmountAdd(ChatVisuals.ClearFontRole)
                        .Foreground(Theme.Ref("ChatSecondaryTextBrush"))
                        .TextTrimming(TextTrimming.CharacterEllipsis)
                        .VAlign(VerticalAlignment.Center)
                        .Grid(column: 0),
                    Component<ChatCopyButton, ChatCopyButtonProps>(new(
                            "code", code, copyLabel,
                            tryCopy, UseInstanceAutomationId: true))
                        .Grid(column: 1))
                    .Margin(ChatVisuals.CodeInset, ChatVisuals.CodeHeaderTopInset,
                        ChatVisuals.CodeHeaderTrailingInset, ChatVisuals.CodeHeaderBottomInset),
                ScrollViewer(codeBody)
                    .MaxHeight(320)
                    .Margin(6, 0, 6, 6)
                    .Set(scroll =>
                    {
                        scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                        scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                        scroll.HorizontalContentAlignment = HorizontalAlignment.Left;
                    })))
            .CornerRadius(ChatVisuals.CodeRadius)
            .BorderThickness(1)
            .BorderBrush(Theme.Ref("ChatStrokeBrush"))
            .Background(Theme.Ref("ChatCardBrush"))
            .AutomationName(language ?? Localized("Chat_Code_Label", "Code block"));
    }

    private sealed class CodeSyntaxState
    {
        internal bool Active;
        internal string Code = string.Empty;
        internal string? Language;
        internal bool? IsDark;
        internal bool HighContrast;
        internal List<Microsoft.UI.Xaml.Documents.TextHighlighter> Highlighters = [];
    }

    private static readonly ConditionalWeakTable<RichTextBlock, CodeSyntaxState> s_codeSyntax = new();

    /// <summary>
    /// Colors fenced code with theme-resolved text highlighters over Reactor's single literal
    /// run. Reactor keeps owning that run, so unchanged code keeps its paragraph and selection
    /// across re-renders, and selection and copy always see the exact source. Unknown
    /// languages, oversized or overly dense blocks, and High Contrast stay plain.
    /// </summary>
    internal static void ApplyCodeSyntax(RichTextBlock target, string code, string? language)
    {
        if (!s_codeSyntax.TryGetValue(target, out var state))
        {
            state = new CodeSyntaxState();
            s_codeSyntax.Add(target, state);
        }
        var isDark = target.ActualTheme == ElementTheme.Dark;
        var highContrast = IsHighContrast();
        if (state.Active && state.Code == code && state.Language == language && state.IsDark == isDark &&
            state.HighContrast == highContrast &&
            target.TextHighlighters.Count == state.Highlighters.Count &&
            state.Highlighters.All(target.TextHighlighters.Contains))
            return;

        // A pooled control may carry another surface's highlighters; code owns all of them.
        target.TextHighlighters.Clear();
        state.Highlighters.Clear();
        state.Active = true;
        state.Code = code;
        state.Language = language;
        state.HighContrast = highContrast;
        // Before load, ActualTheme reports the app theme rather than this subtree's; Loaded
        // colors the block once the element theme is known.
        state.IsDark = target.IsLoaded ? isDark : null;
        if (state.IsDark is null || highContrast ||
            ThemeRef.Resolve("SubtleFillColorTransparentBrush", isDark) is not Brush clear)
            return;

        foreach (var group in ChatCodeSyntax.Classify(code, language).GroupBy(span => span.Role))
        {
            if (ThemeRef.Resolve(SyntaxBrush(group.Key), isDark) is not Brush ink)
                continue;
            var highlighter = new Microsoft.UI.Xaml.Documents.TextHighlighter { Background = clear, Foreground = ink };
            foreach (var span in group)
                highlighter.Ranges.Add(new() { StartIndex = span.Start, Length = span.Length });
            target.TextHighlighters.Add(highlighter);
            state.Highlighters.Add(highlighter);
        }
    }

    // Reactor pools RichTextBlocks without clearing highlighters; drop ours before reuse.
    private static void ClearCodeSyntax(RichTextBlock target)
    {
        if (!s_codeSyntax.TryGetValue(target, out var state))
            return;
        foreach (var highlighter in state.Highlighters)
            target.TextHighlighters.Remove(highlighter);
        state.Highlighters.Clear();
        state.Active = false;
        state.Code = string.Empty;
        state.Language = null;
        state.IsDark = null;
    }

    private static void OnCodeLoaded(object sender, RoutedEventArgs args) => RefreshCodeSyntax((RichTextBlock)sender);

    private static void OnCodeThemeChanged(FrameworkElement sender, object args) => RefreshCodeSyntax((RichTextBlock)sender);

    // Loading into a themed subtree or switching theme changes the resolved palette. Reactor
    // may have pooled the control for other content, so refresh only while it shows this code.
    private static void RefreshCodeSyntax(RichTextBlock target)
    {
        if (s_codeSyntax.TryGetValue(target, out var state) && state.Active &&
            target.Blocks is [Microsoft.UI.Xaml.Documents.Paragraph { Inlines: [Microsoft.UI.Xaml.Documents.Run run] }] &&
            run.Text == state.Code)
            ApplyCodeSyntax(target, state.Code, state.Language);
    }

    private static string SyntaxBrush(ChatCodeRole role) => role switch
    {
        ChatCodeRole.Keyword => "ChatSyntaxKeywordBrush",
        ChatCodeRole.String => "ChatSyntaxStringBrush",
        ChatCodeRole.Number => "ChatSyntaxIdentifierBrush",
        ChatCodeRole.Comment => "ChatSyntaxCommentBrush",
        ChatCodeRole.Title => "ChatSyntaxTitleBrush",
        ChatCodeRole.Type => "ChatSyntaxTypeBrush",
        _ => "ChatSyntaxAttributeBrush",
    };

    internal static string Localized(string key, string fallback)
    {
        var value = LocalizationHelper.GetString(key);
        return string.IsNullOrWhiteSpace(value) || value == key ? fallback : value;
    }
}

internal sealed record ChatMarkdownProps(string? Text, Func<string, bool>? TryCopyCode, bool CompactSecondaryReading);

/// <summary>Mount through ReactorChatTimeline.BuildSafeMarkdown.</summary>
internal sealed class ChatMarkdownView : Component<ChatMarkdownProps>
{
    public override Element Render() =>
        ReactorChatTimeline.RenderSafeMarkdown(Props.Text, Props.TryCopyCode, Props.CompactSecondaryReading);
}

internal sealed record ChatMarkdownDisclosureProps(
    ChatMarkdownDisclosure Disclosure, Func<string, Element> RenderMarkdown);

internal sealed class ChatMarkdownDisclosureView : Component<ChatMarkdownDisclosureProps>
{
    public override Element Render()
    {
        var disclosure = Props.Disclosure;
        var (expanded, setExpanded) = UseState(disclosure.InitiallyExpanded);
        return (Expander(
                disclosure.Summary,
                ChatMarkdownPresentation.Disclosures(disclosure.Body, Props.RenderMarkdown)
                    .Margin(ChatVisuals.ToolRowTitleInset, 4, 0, 8),
                expanded,
                setExpanded) with
            {
                HeaderContent = ToolCallCardRenderer.BuildDisclosureHeader(expanded,
                    TextBlock(disclosure.Summary).TextWrapping(TextWrapping.Wrap)
                        .FontFamily(ChatVisuals.BodyFontFamily)),
            })
            .Padding(0)
            .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
            .BorderBrush(Theme.Ref("SubtleFillColorTransparentBrush"))
            .Resources(ChatVisuals.ToolRowExpanderResources)
            .Set(ChatVisuals.FlushToolRowExpander)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .HAlign(HorizontalAlignment.Stretch)
            .AutomationName(disclosure.Summary);
    }
}
