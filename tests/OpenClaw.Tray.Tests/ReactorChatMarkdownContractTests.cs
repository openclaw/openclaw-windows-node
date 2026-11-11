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
            "MarkdownParserFlags.Tables | MarkdownParserFlags.NoHtml |\n                MarkdownParserFlags.PermissiveUrlAutolinks | MarkdownParserFlags.PermissiveWwwAutolinks",
            timeline.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains(
            "ChatMarkdownSanitizer.Sanitize(text, preserveWebLinks: true), options)",
            timeline,
            StringComparison.Ordinal);
        Assert.Contains("Image = (alt, _) => Text(", timeline, StringComparison.Ordinal);
        // Web-parity links: Reactor's native hyperlink (http/https/mailto only), no inert override.
        Assert.DoesNotContain("LinkBuilder =", timeline, StringComparison.Ordinal);
        // Underline-free accent links and inline-code chips are applied after reconcile.
        Assert.Contains("ChatMarkdownPresentation.WithInlineStyling(ChatMarkdownPresentation.MessageBlocks(", timeline, StringComparison.Ordinal);
        var presentation = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Chat", "ChatMarkdownPresentation.cs"));
        Assert.Contains("link.UnderlineStyle = Microsoft.UI.Xaml.Documents.UnderlineStyle.None;", presentation, StringComparison.Ordinal);
        Assert.Contains("TextElement.FontFamilyProperty", presentation, StringComparison.Ordinal);
        Assert.Contains("AccessibilitySettings().HighContrast", presentation, StringComparison.Ordinal);
        Assert.Contains("HtmlBlock = raw => Text(", timeline, StringComparison.Ordinal);
        Assert.Contains("ListItem = row => ChatMarkdownPresentation.ListItem(row, compactSecondaryReading)", timeline, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildWrappingMarkdownListItem", timeline, StringComparison.Ordinal);
        Assert.Contains("Heading = ChatMarkdownPresentation.Heading", timeline, StringComparison.Ordinal);
        Assert.Contains("Paragraph = ChatMarkdownPresentation.Paragraph", timeline, StringComparison.Ordinal);
        Assert.Contains("CodeBlock = (code, language) => ChatMarkdownPresentation.CodeBlock(code, language, tryCopy: tryCopy)", timeline, StringComparison.Ordinal);
        // List items keep Reactor's Auto/Star wrapping row; the hook only places the marker.
        Assert.Contains("row is not GridElement { Children: [TextBlockElement marker, var content] } grid", presentation, StringComparison.Ordinal);
        // Web-parity list rhythm and divider come from presentation hooks.
        Assert.Contains("UnorderedList = items => ChatMarkdownPresentation.List(items, compactSecondaryReading)", timeline, StringComparison.Ordinal);
        Assert.Contains("OrderedList = (_, items) => ChatMarkdownPresentation.List(items, compactSecondaryReading)", timeline, StringComparison.Ordinal);
        Assert.Contains("ThematicBreak = ChatMarkdownPresentation.ThematicBreak", timeline, StringComparison.Ordinal);
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
}
