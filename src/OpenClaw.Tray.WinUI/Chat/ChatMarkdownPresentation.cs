using System.Runtime.CompilerServices;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Markdown;
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
        IReadOnlyList<ChatMarkdownPart> parts, Func<string, Element> renderMarkdown,
        MarkdownTextSelectionOptions selectionOptions)
    {
        if (parts is [ChatMarkdownText markdown])
            return renderMarkdown(markdown.Text);
        return VStack(ChatVisuals.BlockGap, parts.Select((part, index) => part switch
        {
            ChatMarkdownText text => renderMarkdown(text.Text),
            ChatMarkdownDisclosure disclosure =>
                Component<ChatMarkdownDisclosureView, ChatMarkdownDisclosureProps>(
                    new(disclosure, renderMarkdown, selectionOptions)).WithKey($"disclosure:{index}"),
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

    private static void StyleInlines(DependencyObject root, bool compactSecondaryReading)
    {
        if (root is RichTextBlock rich)
        {
            // Fenced code owns its syntax highlighters; see ApplyCodeSyntax.
            if (IsCodeText(rich))
                return;
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
            StyleRichText(rich, compactSecondaryReading);
            return;
        }
        if (root is TextBlock text && compactSecondaryReading && !IsCodeText(text))
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

    private static void StyleRichText(RichTextBlock rich, bool compactSecondaryReading)
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
                            run.ReadLocalValue(Microsoft.UI.Xaml.Documents.TextElement.FontFamilyProperty) is FontFamily { Source: ChatVisuals.CodeFontFamily })
                            (strongRun ? strongCodeRanges : codeRanges).Add(new() { StartIndex = offset, Length = length });
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
        // code keeps its surrounding tone on a subtle chip.
        var isDark = rich.ActualTheme == ElementTheme.Dark;
        if (ThemeRef.Resolve("ControlFillColorSecondaryBrush", isDark) is not Brush chip ||
            ThemeRef.Resolve("SubtleFillColorTransparentBrush", isDark) is not Brush clear ||
            ThemeRef.Resolve("TextFillColorPrimaryBrush", isDark) is not Brush strongText ||
            ThemeRef.Resolve("TextFillColorPrimaryBrush", isDark) is not Brush proseText)
            return;
        AddHighlighter(strongRanges, clear, strongText);
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
                    .Select((child, index) => ResetBlockEdges(child, index == 0 || compactSecondaryReading))
                    .ToArray(),
            }
            : element;

    // The first and last blocks add no outer gap; only later headings keep their extra lead-in.
    private static Element ResetBlockEdges(Element element, bool dropTop)
    {
        var margin = element.Modifiers?.Margin ?? new Thickness(0);
        var top = !dropTop && IsHeading(element) ? margin.Top : 0;
        return element.Margin(margin.Left, top, margin.Right, 0);
    }

    // Text selection wraps each block after its hook and keeps only layout values on the wrapper,
    // so headings also carry a neutral layout marker: MaxHeight at its native default.
    private static bool IsHeading(Element element) =>
        s_headings.TryGetValue(element, out _) ||
        element.Modifiers?.Layout?.MaxHeight is double.PositiveInfinity;

    private static readonly ConditionalWeakTable<Element, object> s_headings = new();

    internal static Element Paragraph(Element element) => Format(element, ChatVisuals.BodySize, false);

    internal static Element Heading(int level, Element element)
    {
        var heading = Format(element, level switch { 1 => 24, 2 => 20, _ => 16 }, true)
            .MaxHeight(double.PositiveInfinity);
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
        if (row is not GridElement { Children: [var markerElement, var content] } grid)
            return row;
        if (markerElement is not TextBlockElement marker)
        {
            // Text selection wraps the marker and content before this hook. Size the wrapper's
            // column here and style the mounted marker before it first renders. In-place updates
            // (streamed item text, a changed marker) do not reload the row, so every render also
            // queues a restyle after its children reconcile.
            return (grid with { Children = [markerElement.MinWidth(ChatVisuals.ListMarkerColumn), content] })
                .Set(control => QueueListRowStyling(control, compactSecondaryReading))
                .OnMountAdd(control => ((FrameworkElement)control).Loaded +=
                    compactSecondaryReading ? OnCompactListRowLoaded : OnListRowLoaded)
                .OnUnmountAdd(control =>
                {
                    ((FrameworkElement)control).Loaded -= OnCompactListRowLoaded;
                    ((FrameworkElement)control).Loaded -= OnListRowLoaded;
                    s_listRowStyling.Remove((Microsoft.UI.Xaml.Controls.Grid)control);
                });
        }
        var column = (marker with { TextAlignment = TextAlignment.Right })
            .MinWidth(ChatVisuals.ListMarkerColumn)
            .Set(text => StyleListMarker(text, compactSecondaryReading));
        if (!compactSecondaryReading && content is RichTextBlockElement rich && !IsHeading(content))
        {
            var prose = (Style)Application.Current.Resources["ChatProseRichTextStyle"];
            content = rich.Set(text => text.Style = prose);
        }
        return grid with { Children = [column, content] };
    }

    private static void OnListRowLoaded(object sender, RoutedEventArgs args) =>
        StyleListRow((Microsoft.UI.Xaml.Controls.Grid)sender, false);

    private static void OnCompactListRowLoaded(object sender, RoutedEventArgs args) =>
        StyleListRow((Microsoft.UI.Xaml.Controls.Grid)sender, true);

    // A fresh token per render; a queued pass from an older render, or for a row Reactor has
    // since unmounted and pooled, no-ops.
    private static readonly ConditionalWeakTable<Microsoft.UI.Xaml.Controls.Grid, object> s_listRowStyling = new();

    private static void QueueListRowStyling(Microsoft.UI.Xaml.Controls.Grid row, bool compactSecondaryReading)
    {
        var token = new object();
        s_listRowStyling.AddOrUpdate(row, token);
        row.DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            if (s_listRowStyling.TryGetValue(row, out var current) && ReferenceEquals(current, token))
                StyleListRow(row, compactSecondaryReading);
        });
    }

    private static void StyleListRow(Microsoft.UI.Xaml.Controls.Grid row, bool compactSecondaryReading)
    {
        foreach (var child in row.Children.OfType<FrameworkElement>())
        {
            if (Microsoft.UI.Xaml.Controls.Grid.GetColumn(child) == 0)
            {
                if (FirstDescendant<TextBlock>(child) is { } marker)
                {
                    marker.TextAlignment = TextAlignment.Right;
                    marker.MinWidth = ChatVisuals.ListMarkerColumn;
                    StyleListMarker(marker, compactSecondaryReading);
                }
            }
            else if (!compactSecondaryReading && FirstDescendant<RichTextBlock>(child) is { Style: null } text && !IsCodeText(text))
            {
                // Tight-list text bypasses the Paragraph hook; give it the prose tone before first render.
                text.Style = (Style)Application.Current.Resources["ChatProseRichTextStyle"];
                text.ClearValue(Microsoft.UI.Xaml.Controls.RichTextBlock.ForegroundProperty);
            }
        }
    }

    private static void StyleListMarker(TextBlock text, bool compactSecondaryReading)
    {
        var style = compactSecondaryReading || !(text.Text ?? string.Empty).StartsWith('\u2022')
            ? "ChatSecondaryTextStyle"
            : "ChatMutedTextStyle";
        text.Style = (Style)Application.Current.Resources[style];
        text.ClearValue(Microsoft.UI.Xaml.Controls.TextBlock.ForegroundProperty);
    }

    private static T? FirstDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                return match;
            if (FirstDescendant<T>(child) is { } nested)
                return nested;
        }
        return null;
    }

    internal static Element ThematicBreak() =>
        Border(Empty())
            .Height(1)
            .Background(Theme.Ref("DividerStrokeColorDefaultBrush"))
            .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

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
                .Set(text => text.Style = (Style)Application.Current.Resources[
                    heading ? "ChatRichTextStyle" : "ChatProseRichTextStyle"])
                .Margin(0, heading ? ChatVisuals.HeadingTopInset : 0, 0, 0);
        }
        return element.Margin(0, 0, 0, 0);
    }

    internal static Element CodeBlock(string code, string? language, Func<string, bool>? tryCopy = null,
        MarkdownTextSelectionOptions? selectionOptions = null)
    {
        var copyLabel = Localized("Chat_Code_Copy", "Copy code");
        var label = string.IsNullOrWhiteSpace(language)
            ? Localized("Chat_Code_LanguageFallback", "code")
            : language.ToLowerInvariant();
        Element codeBody = (RichTextBlock(code) with { FontSize = ChatVisuals.CodeSize })
            .Set(text =>
            {
                text.Style = (Style)Application.Current.Resources["ChatCodeTextStyle"];
                text.FontFamily = new FontFamily(ChatVisuals.CodeFontFamily);
                text.LineHeight = ChatVisuals.CodeLineHeight;
                text.LineStackingStrategy = LineStackingStrategy.MaxHeight;
                text.IsTextSelectionEnabled = selectionOptions is null;
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
            })
            .Margin(ChatVisuals.CodeInset - 6, 0, ChatVisuals.CodeInset - 6, ChatVisuals.CodeInset - 6);
        if (selectionOptions is not null)
            codeBody = MarkdownTextSelection.Surface(code, codeBody);
        return Border(VStack(
                0,
                Grid(
                    [GridSize.Star(), GridSize.Auto],
                    [GridSize.Auto],
                    TextBlock(label)
                        .FontSize(ChatVisuals.CodeLabelSize)
                        .FontFamily(ChatVisuals.CodeFontFamily)
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

internal sealed record ChatMarkdownDisclosureProps(
    ChatMarkdownDisclosure Disclosure, Func<string, Element> RenderMarkdown,
    MarkdownTextSelectionOptions SelectionOptions);

internal sealed class ChatMarkdownDisclosureView : Component<ChatMarkdownDisclosureProps>
{
    public override Element Render()
    {
        var disclosure = Props.Disclosure;
        var (expanded, setExpanded) = UseState(disclosure.InitiallyExpanded);
        return (Expander(
                disclosure.Summary,
                MarkdownTextSelection.Visibility(expanded,
                    ChatMarkdownPresentation.Disclosures(
                        disclosure.Body, Props.RenderMarkdown, Props.SelectionOptions)),
                expanded,
                setExpanded) with
            {
                HeaderContent = MarkdownTextSelection.Surface(disclosure.Summary,
                    TextBlock(disclosure.Summary).TextWrapping(TextWrapping.Wrap)),
            })
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .HAlign(HorizontalAlignment.Stretch)
            .AutomationName(disclosure.Summary);
    }
}
