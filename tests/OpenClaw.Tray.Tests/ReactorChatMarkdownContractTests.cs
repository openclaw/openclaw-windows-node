namespace OpenClaw.Tray.Tests;

public sealed class ReactorChatMarkdownContractTests
{
    [Fact]
    public void AssistantMessages_UseSanitizedGitHubFlavoredMarkdown()
    {
        var timeline = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "ReactorChatTimeline.cs"));

        Assert.Contains(
            "MarkdownParserFlags.DialectGitHub | MarkdownParserFlags.NoHtml |\n                MarkdownParserFlags.HardSoftBreaks",
            timeline.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains(
            "var sanitized = ChatMarkdownSanitizer.Sanitize(text ?? string.Empty, preserveWebLinks: true);",
            timeline,
            StringComparison.Ordinal);
        Assert.Contains("ChatMarkdownDisclosures.Parse(sanitized)", timeline, StringComparison.Ordinal);
        // Streaming refreshes every realized row; unchanged messages must stay behind the props gate.
        Assert.Contains("Component<ChatMarkdownView, ChatMarkdownProps>(new(text, tryCopy, compactSecondaryReading))",
            timeline, StringComparison.Ordinal);
        Assert.Contains("Image = (alt, _) =>", timeline, StringComparison.Ordinal);
        // Web-parity links: Reactor's native hyperlink (http/https/mailto only), no inert override.
        Assert.DoesNotContain("LinkBuilder =", timeline, StringComparison.Ordinal);
        // Underline-free accent links and inline-code chips are applied after reconcile.
        // Block rhythm applies to Reactor's root stack before the styling Border wraps it.
        Assert.Matches(
            @"ChatMarkdownPresentation\.WithInlineStyling\(\s*ChatMarkdownPresentation\.MessageBlocks\(\s*" +
            @"Microsoft\.UI\.Reactor\.Advanced\.Factories\.Markdown\(text, options\)",
            timeline);
        var presentation = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Chat", "ChatMarkdownPresentation.cs"));
        Assert.Contains("link.UnderlineStyle = Microsoft.UI.Xaml.Documents.UnderlineStyle.None;", presentation, StringComparison.Ordinal);
        Assert.Contains("TextElement.FontFamilyProperty", presentation, StringComparison.Ordinal);
        Assert.Contains("ChatThemeChanges.IsHighContrast", presentation, StringComparison.Ordinal);
        var themeChanges = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Chat", "ChatThemeChanges.cs"));
        Assert.Contains("AccessibilitySettings().HighContrast", themeChanges, StringComparison.Ordinal);
        Assert.Contains("HtmlBlock = raw =>", timeline, StringComparison.Ordinal);
        Assert.Contains("ListItem = row => ChatMarkdownPresentation.ListItem(row, compactSecondaryReading)", timeline, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildWrappingMarkdownListItem", timeline, StringComparison.Ordinal);
        Assert.Contains("Heading = ChatMarkdownPresentation.Heading", timeline, StringComparison.Ordinal);
        Assert.Contains("Paragraph = ChatMarkdownPresentation.Paragraph", timeline, StringComparison.Ordinal);
        Assert.Contains("ChatMarkdownPresentation.CodeBlock(code, language, tryCopy: tryCopy)", timeline, StringComparison.Ordinal);
        // List items keep Reactor's Auto/Star wrapping row; the hook only places the marker.
        Assert.Contains("row is not GridElement { Children: [TextBlockElement marker, var content] } grid", presentation, StringComparison.Ordinal);
        // Web-parity list rhythm and divider come from presentation hooks.
        Assert.Contains("UnorderedList = items => ChatMarkdownPresentation.List(items, compactSecondaryReading)", timeline, StringComparison.Ordinal);
        Assert.Contains("OrderedList = (_, items) => ChatMarkdownPresentation.List(items, compactSecondaryReading)", timeline, StringComparison.Ordinal);
        Assert.Contains("ThematicBreak = ChatMarkdownPresentation.ThematicBreak", timeline, StringComparison.Ordinal);
        // Web-parity quotes: unfilled content beside a rounded bar, muted by the inline styling pass.
        Assert.Contains("BlockQuote = ChatMarkdownPresentation.BlockQuote", timeline, StringComparison.Ordinal);
        Assert.Contains("quote is not BorderElement { Child: { } content }", presentation, StringComparison.Ordinal);
        Assert.Contains("ThemeRef.Resolve(\"ChatMutedTextBrush\"", presentation, StringComparison.Ordinal);
        // Web-parity tables: Reactor's single table grid is restyled in a rounded frame; the
        // Table hook is not used because it receives no rows without TextSelection.
        Assert.DoesNotContain("Table = ChatMarkdownPresentation.Table", timeline, StringComparison.Ordinal);
        Assert.Contains("TableHeaderBackground = null", timeline, StringComparison.Ordinal);
        Assert.Contains("ResetBlockEdges(Table(child)", presentation, StringComparison.Ordinal);
        // Fenced code keeps Reactor's single literal run (so reconcile never rebuilds it and
        // selection survives) and is colored by theme-resolved highlighters.
        Assert.Contains("ApplyCodeSyntax(text, code, language);", presentation, StringComparison.Ordinal);
        Assert.Contains("ClearCodeSyntax((RichTextBlock)control);", presentation, StringComparison.Ordinal);
        Assert.DoesNotContain("XamlReader", presentation, StringComparison.Ordinal);
        Assert.Contains(
            "BuildAccessibleAssistantText(entry.Text, metadata?.AssistantContent)",
            timeline,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ChatMarkdown_DoesNotUseReactorCrossBlockSelection()
    {
        // Reactor preview.18's MarkdownTextSelection host rescans native text geometry on every
        // LayoutUpdated, which made typing and streaming slow. Re-enable it only with a fixed package.
        var tray = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI");
        foreach (var file in Directory.EnumerateFiles(tray, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            Assert.False(source.Contains("MarkdownTextSelection", StringComparison.Ordinal), file);
            Assert.False(source.Contains("TextSelection =", StringComparison.Ordinal), file);
        }
    }
}
