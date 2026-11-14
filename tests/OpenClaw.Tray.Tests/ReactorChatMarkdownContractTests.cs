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
            "var sanitized = ChatMarkdownSanitizer.Sanitize(text, preserveWebLinks: true);",
            timeline,
            StringComparison.Ordinal);
        Assert.Contains("ChatMarkdownDisclosures.Parse(sanitized)", timeline, StringComparison.Ordinal);
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
        Assert.Contains("tryCopy: tryCopy, selectionOptions: selectionOptions)", timeline, StringComparison.Ordinal);
        // List items keep Reactor's Auto/Star wrapping row; the hook only places the marker.
        Assert.Contains("row is not GridElement { Children: [var markerElement, var content] } grid", presentation, StringComparison.Ordinal);
        // Web-parity list rhythm and divider come from presentation hooks.
        Assert.Contains("UnorderedList = items => ChatMarkdownPresentation.List(items, compactSecondaryReading)", timeline, StringComparison.Ordinal);
        Assert.Contains("OrderedList = (_, items) => ChatMarkdownPresentation.List(items, compactSecondaryReading)", timeline, StringComparison.Ordinal);
        Assert.Contains("ThematicBreak = ChatMarkdownPresentation.ThematicBreak", timeline, StringComparison.Ordinal);
        // Fenced code keeps Reactor's single literal run (so reconcile never rebuilds it and
        // selection survives) and is colored by theme-resolved highlighters.
        Assert.Contains("ApplyCodeSyntax(text, code, language);", presentation, StringComparison.Ordinal);
        Assert.Contains("ClearCodeSyntax((RichTextBlock)control);", presentation, StringComparison.Ordinal);
        Assert.DoesNotContain("XamlReader", presentation, StringComparison.Ordinal);
        Assert.Contains("TextSelection = selectionOptions", timeline, StringComparison.Ordinal);
        Assert.Contains("MarkdownTextSelection.Surface(label, Text(", timeline, StringComparison.Ordinal);
        Assert.Contains(
            "BuildAccessibleAssistantText(entry.Text, metadata?.AssistantContent)",
            timeline,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownSelection_UsesOneReactorScopeAndFeedbackOnlyCopyResults()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var message = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI",
            "Chat", "ChatMarkdownMessageView.cs"));
        var presentation = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI",
            "Chat", "ChatMarkdownPresentation.cs"));

        var timeline = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI",
            "Chat", "ReactorChatTimeline.cs"));
        Assert.Matches(
            @"BuildSafeMarkdown\(\s*string\? text, Func<string, bool>\? tryCopy = null, " +
            @"bool compactSecondaryReading = false\)\s*=>\s*" +
            @"Component<ChatMarkdownMessageView, ChatMarkdownMessageProps>\(\s*new\(\s*" +
            @"text \?\? string.Empty,\s*tryCopy,\s*compactSecondaryReading\)\);",
            timeline);
        Assert.Matches(
            @"MarkdownTextSelection\.Scope\(options,\s*ReactorChatTimeline\.BuildMessageMarkdown\(" +
            @"\s*props\.Text, props\.TryCopyCode, options, props\.CompactSecondaryReading\)\)",
            message);
        Assert.Equal(1, message.Split("MarkdownTextSelection.Scope(", StringSplitOptions.None).Length - 1);
        Assert.Contains("OnCopyResult = (_, success) =>", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Clipboard.", message, StringComparison.Ordinal);
        Assert.DoesNotContain("TryCopyText", message, StringComparison.Ordinal);
        Assert.Contains("Chat_Copy_Copied", message, StringComparison.Ordinal);
        Assert.Contains("Chat_Copy_Failed", message, StringComparison.Ordinal);
        Assert.Contains("MarkdownTextSelection.Surface(code, codeBody)", presentation, StringComparison.Ordinal);
        Assert.Contains("MarkdownTextSelection.Visibility(expanded,", presentation, StringComparison.Ordinal);
        Assert.Contains("HeaderContent = MarkdownTextSelection.Surface(disclosure.Summary,", presentation, StringComparison.Ordinal);
    }
}
