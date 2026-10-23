using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatTextSelectionDocumentTests
{
    [Fact]
    public void CrossBlockRange_CopiesVisibleTextInOrderWithAuthoredCodeNewlines()
    {
        var document = new ChatTextSelectionDocument();
        document.Update([
            new("Paragraph."),
            new("First bullet.", "\n\n"),
            new("Second bullet.", "\n"),
            new("code one\ncode two"),
            new("Final paragraph."),
        ]);
        var start = document.Text.IndexOf("bullet.", StringComparison.Ordinal);
        var end = document.Text.IndexOf("Final", StringComparison.Ordinal);
        document.Select(end, start);
        Assert.Equal("bullet.\nSecond bullet.\n\ncode one\ncode two\n\n", document.SelectedText);
        document.MoveHorizontal(-1, word: false, extend: false);
        Assert.False(document.HasSelection);
        Assert.Equal(start, document.Focus);
    }

    [Fact]
    public void StreamingBeforeSelectedSuffix_RebasesWithoutChangingCopiedExcerpt()
    {
        var document = new ChatTextSelectionDocument();
        document.Update([new("Before.\n\nChosen evidence.")]);
        document.Select(9, document.Text.Length);
        Assert.Equal("Chosen evidence.", document.SelectedText);
        document.Update([new("Before, with streamed details.\n\nChosen evidence.")]);
        Assert.Equal("Chosen evidence.", document.SelectedText);
        var anchor = document.Anchor;
        var focus = document.Focus;
        Assert.False(document.Update([new(document.Text)]));
        Assert.Equal(anchor, document.Anchor);
        Assert.Equal(focus, document.Focus);
        document.Update([new(document.Text + "\n\nMore evidence.")]);
        Assert.Equal("Chosen evidence.", document.SelectedText);
        document.Update([new("Before.\n\nChosen evidence.\n\nMore evidence.")]);
        Assert.Equal("Chosen evidence.", document.SelectedText);
    }

    [Fact]
    public void Select_ClampsEndpointsToVisibleDocument()
    {
        var document = new ChatTextSelectionDocument();
        document.Update([new("Visible evidence.")]);
        document.Select(-5, 999);
        Assert.Equal(0, document.Anchor);
        Assert.Equal(document.Text.Length, document.Focus);
        Assert.Equal("Visible evidence.", document.SelectedText);
    }

    [Fact]
    public void SelectWord_AtDocumentEndSelectsLastWord()
    {
        var document = new ChatTextSelectionDocument();
        document.Update([new("First final_word")]);
        document.SelectWord(document.Text.Length);
        Assert.Equal("final_word", document.SelectedText);
    }

    [Fact]
    public void KeyboardSelection_CrossesBlocksWithoutSplittingEmojiOrCombiningText()
    {
        var document = new ChatTextSelectionDocument();
        document.Update([new("e\u0301"), new("\U0001F469\u200D\U0001F4BB"), new("next")]);
        document.MoveHorizontal(1, word: false, extend: true);
        Assert.Equal("e\u0301", document.SelectedText);
        document.MoveHorizontal(1, word: false, extend: true);
        document.MoveHorizontal(1, word: false, extend: true);
        document.MoveHorizontal(1, word: false, extend: true);
        Assert.Equal("e\u0301\n\n\U0001F469\u200D\U0001F4BB", document.SelectedText);
    }

    [Fact]
    public void WordSelection_KeepsIdentifierTogetherAndKeyboardMovesAcrossBlockBoundary()
    {
        var document = new ChatTextSelectionDocument();
        document.Update([new("plugin_purpose and activation."), new("next evidence")]);
        document.SelectWord(5);
        Assert.Equal("plugin_purpose", document.SelectedText);
        document.MoveTo(document.Text.IndexOf("next", StringComparison.Ordinal), extend: false);
        document.MoveHorizontal(-1, word: true, extend: true);
        Assert.Equal(".\n\n", document.SelectedText);
        document.MoveTo(document.Text.Length, extend: true);
        Assert.Equal(document.Text.Length, document.End);
    }

    [Fact]
    public void WordNavigation_HandlesSupplementaryLettersAndCombiningMarksInBothDirections()
    {
        var document = new ChatTextSelectionDocument();
        document.Update([new("A\U00020000e\u0301_suffix next")]);
        document.SelectWord(2);
        Assert.Equal("A\U00020000e\u0301_suffix", document.SelectedText);
        document.MoveTo(0, extend: false);
        document.MoveHorizontal(1, word: true, extend: true);
        Assert.Equal("A\U00020000e\u0301_suffix ", document.SelectedText);
        document.MoveTo(document.Focus, extend: false);
        document.MoveHorizontal(-1, word: true, extend: true);
        Assert.Equal("A\U00020000e\u0301_suffix ", document.SelectedText);
    }
}
