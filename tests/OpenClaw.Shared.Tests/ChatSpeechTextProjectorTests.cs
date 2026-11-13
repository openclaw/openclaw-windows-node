using OpenClaw.Shared.Markdown;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Tests for <see cref="ChatSpeechTextProjector"/>: the text handed to TTS must
/// keep the message's structure as pauses instead of flattening it, and must
/// never read a URL or a run-together identifier.
/// </summary>
public class ChatSpeechTextProjectorTests
{
    private static string Project(string markdown) => ChatSpeechTextProjector.Project(markdown);

    [Fact]
    public void EmptyInput_ProducesNoSpeech()
    {
        Assert.Equal(string.Empty, Project(string.Empty));
        Assert.Equal(string.Empty, Project("   \n\n "));
        Assert.Equal(string.Empty, ChatSpeechTextProjector.Project(null));
    }

    [Fact]
    public void HeadingListAndParagraphs_EachEndWithABoundary()
    {
        var spoken = Project("## Summary\nThe build passed.\n\n- First item\n- Second item\n\nDone!");

        Assert.Equal("Summary. The build passed. First item. Second item. Done!", spoken);
    }

    [Fact]
    public void ParagraphBreak_BecomesASentenceBoundary_NotJustASpace()
    {
        Assert.Equal("Line one. Line two.", Project("Line one\n\nLine two"));
    }

    [Fact]
    public void SnakeCaseIdentifiers_KeepWordSeparation()
    {
        var spoken = Project("Because user_id is required, max_retry_count changes.");

        // No underscore survives: `user_id and max_retry_count` used to collapse
        // into `userid and maxretry_count` because `_..._` looked like italics.
        Assert.Equal("Because user id is required, max retry count changes.", spoken);
    }

    [Fact]
    public void InlineCode_IsSpokenWithoutOrphanedPunctuation()
    {
        var spoken = Project("Set the `file_name` option, then run `build.ps1`.");

        Assert.Equal("Set the file name option, then run build ps1.", spoken);
        Assert.DoesNotContain(" ,", spoken);
        Assert.DoesNotContain(" .", spoken);
    }

    [Fact]
    public void InlineCode_KeepsDecimalPoints()
    {
        Assert.Equal("Raise maxTokens to 1.5 times the default.", Project("Raise `maxTokens` to `1.5` times the default."));
    }

    [Fact]
    public void FencedCodeBlock_IsSilentButKeepsBothSentencesIntact()
    {
        var spoken = Project("Before the sample.\n\n```csharp\nvar x = 1;\n```\n\nAfter the sample.");

        Assert.Equal("Before the sample. After the sample.", spoken);
        Assert.DoesNotContain("var x", spoken);
    }

    [Fact]
    public void Link_SpeaksDisplayTextOnly()
    {
        Assert.Equal("Read the docs first.", Project("Read [the docs](https://example.com/docs) first."));
    }

    [Fact]
    public void BareUrl_IsSilentAndTheSentenceSurvives()
    {
        var spoken = Project("Read https://example.com/changelog for details.");

        Assert.Equal("Read for details.", spoken);
        Assert.DoesNotContain("http", spoken);
    }

    [Fact]
    public void UrlOnlyParagraph_AddsNothingToSpeech()
    {
        Assert.Equal("The changelog.", Project("The changelog.\n\nhttps://example.com/changelog"));
    }

    [Fact]
    public void Image_SpeaksAltText()
    {
        Assert.Equal("A puppy. Done.", Project("![A puppy](https://example.com/p.png)\n\nDone."));
    }

    [Fact]
    public void SentencePunctuation_IsPreservedVerbatim()
    {
        Assert.Equal(
            "Wait... really? Yes — it's done; ok: next (maybe).",
            Project("Wait... really? Yes — it's done; ok: next (maybe)."));
    }

    [Theory]
    [InlineData("Done!", "Done!")]
    [InlineData("Hecho.", "Hecho.")]
    [InlineData("¿Listo?", "¿Listo?")]
    [InlineData("Why?", "Why?")]
    public void ExistingTerminator_IsNotDoubled(string markdown, string expected)
    {
        Assert.Equal(expected, Project(markdown));
    }

    [Fact]
    public void Cjk_UsesFullWidthTerminators()
    {
        Assert.Equal("标题。内容。", Project("## 标题\n\n内容"));
    }

    [Fact]
    public void Emoji_IsDroppedWithoutMergingNeighbouringWords()
    {
        Assert.Equal("Great! Next step.", Project("Great!🎉Next step."));
    }

    [Fact]
    public void Table_RowsBecomeSentencesWithoutPipes()
    {
        var spoken = Project("| Name | Value |\n|---|---|\n| alpha | 1 |\n| beta | 2 |");

        Assert.Equal("Name, Value. alpha, 1. beta, 2.", spoken);
        Assert.DoesNotContain("|", spoken);
    }

    [Fact]
    public void WrappedLine_BecomesAPauseInsteadOfRunOn()
    {
        var spoken = Project("The gateway reconnects\nafter the transport retries\nand the client rejoins.");

        Assert.Equal("The gateway reconnects, after the transport retries, and the client rejoins.", spoken);
    }

    [Fact]
    public void LineBreakAfterPunctuation_NeedsNoComma()
    {
        Assert.Equal("It stopped. Then it resumed.", Project("It stopped.\nThen it resumed."));
    }

    [Fact]
    public void MessageWithOnlyCodeAndUrl_ProducesNoSpeech()
    {
        Assert.Equal(string.Empty, Project("```\nvar x = 1;\n```\n\nhttps://example.com"));
    }
}
