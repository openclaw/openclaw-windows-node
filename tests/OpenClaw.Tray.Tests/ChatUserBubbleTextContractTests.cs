namespace OpenClaw.Tray.Tests;

public sealed class ChatUserBubbleTextContractTests
{
    [Fact]
    public void UserPromptText_RendersSelectableRichTextBlockParagraph()
    {
        var timeline = Read("src", "OpenClaw.Tray.WinUI", "Chat", "ReactorChatTimeline.cs");

        Assert.Contains("private static Element BuildUser(", timeline);
        Assert.Contains("content.Add(Text(", timeline);
        Assert.Contains("messageText,", timeline);
        Assert.Contains(".Set(text => text.IsTextSelectionEnabled = true)", timeline);
        Assert.Contains(".AutomationName(entry.Text ?? string.Empty)", timeline);
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(parts).ToArray()));
}
