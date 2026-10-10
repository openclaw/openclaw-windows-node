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
            "Factories.Markdown(ChatMarkdownSanitizer.Sanitize(text), options)",
            timeline,
            StringComparison.Ordinal);
        Assert.Contains("Image = (alt, _) => Text(", timeline, StringComparison.Ordinal);
        Assert.Contains("LinkBuilder = (children, _) => HStack(children)", timeline, StringComparison.Ordinal);
        Assert.Contains("HtmlBlock = raw => Text(", timeline, StringComparison.Ordinal);
        Assert.Contains("ListItem = BuildWrappingMarkdownListItem", timeline, StringComparison.Ordinal);
        Assert.Contains("Heading = ChatMarkdownPresentation.Heading", timeline, StringComparison.Ordinal);
        Assert.Contains("Paragraph = ChatMarkdownPresentation.Paragraph", timeline, StringComparison.Ordinal);
        Assert.Contains("CodeBlock = (code, language) => ChatMarkdownPresentation.CodeBlock(code, language, tryCopy: tryCopy)", timeline, StringComparison.Ordinal);
        Assert.Contains(
            "BuildAccessibleAssistantText(entry.Text, metadata?.AssistantContent)",
            timeline,
            StringComparison.Ordinal);
    }
}
