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
            "MarkdownParserFlags.Tables | MarkdownParserFlags.NoHtml",
            timeline,
            StringComparison.Ordinal);
        Assert.Contains(
            "Microsoft.UI.Reactor.Advanced.Factories.Markdown(ChatMarkdownSanitizer.Sanitize(text), options)",
            timeline,
            StringComparison.Ordinal);
        Assert.Contains("Image = (alt, _) => Text(", timeline, StringComparison.Ordinal);
        // Links render their label only; the validated target is never turned into a navigation control.
        Assert.Contains("LinkBuilder = link => HStack(link.Label)", timeline, StringComparison.Ordinal);
        Assert.DoesNotContain("link.Uri", timeline, StringComparison.Ordinal);
        Assert.Contains("HtmlBlock = raw => Text(", timeline, StringComparison.Ordinal);
        Assert.DoesNotContain("ListItem =", timeline, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildWrappingMarkdownListItem", timeline, StringComparison.Ordinal);
        Assert.Contains("Heading = ChatMarkdownPresentation.Heading", timeline, StringComparison.Ordinal);
        Assert.Contains("Paragraph = ChatMarkdownPresentation.Paragraph", timeline, StringComparison.Ordinal);
        Assert.Contains("CodeBlock = (code, language) => ChatMarkdownPresentation.CodeBlock(code, language, tryCopy: tryCopy)", timeline, StringComparison.Ordinal);
        Assert.Contains(
            "BuildAccessibleAssistantText(entry.Text, metadata?.AssistantContent)",
            timeline,
            StringComparison.Ordinal);
    }
}
