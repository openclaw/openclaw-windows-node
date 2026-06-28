using System.Text.RegularExpressions;

namespace OpenClaw.Tray.Tests;

public sealed class ChatUserBubbleTextContractTests
{
    [Fact]
    public void UserPromptText_ResetsStaleTextBlockVisualStateBeforeApplyingInlines()
    {
        var timeline = Read("src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatTimeline.cs");

        Assert.Contains("private const string ChatTextFontFamilySource", timeline);
        Assert.Contains("TextBlock(string.Empty)", timeline);
        Assert.Contains("t.FontFamily = s_chatTextFontFamily;", timeline);
        Assert.Contains("t.TextTrimming = TextTrimming.None;", timeline);
        Assert.Contains("t.MaxLines = 0;", timeline);
        Assert.Contains("t.Width = double.NaN;", timeline);
        Assert.Contains("t.MaxWidth = double.PositiveInfinity;", timeline);
        Assert.Matches(
            new Regex(@"t\.TextTrimming\s*=\s*TextTrimming\.None;[\s\S]*ApplyPlainSelectableInlines\(t,\s*messageText\);"),
            timeline);
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(parts).ToArray()));
}
