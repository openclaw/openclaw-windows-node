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
    public void UrlOnlyParagraph_AddsNothingToSpeech()
    {
        Assert.Equal("The changelog.", Project("The changelog.\n\nhttps://example.com/changelog"));
    }

    [Theory]
    // md4c does not autolink any of these, so the builder cannot be the guard.
    [InlineData("Connect to http://localhost:18789/?token=FAKE123 now.", "Connect to now.")]
    [InlineData("Open https://127.0.0.1:18789/#token=FAKE123 please.", "Open please.")]
    [InlineData("Visit www.example.com/x?token=FAKE123 now.", "Visit now.")]
    [InlineData("Mail dev@example.com today.", "Mail today.")]
    [InlineData("Socket ws://example.com/socket here.", "Socket here.")]
    [InlineData("Reset via https://example.com:8443/reset?key=FAKEKEY done.", "Reset via done.")]
    [InlineData("Path https://example.com/a%20b here.", "Path here.")]
    [InlineData("Query https://example.com/x?a=1&amp;b=2 here.", "Query here.")]
    [InlineData("Read https://example.com/changelog for details.", "Read for details.")]
    public void UrlShapes_AreNeverSpoken(string markdown, string expected)
    {
        var spoken = Project(markdown);

        Assert.Equal(expected, spoken);
        Assert.DoesNotContain("http", spoken);
        Assert.DoesNotContain("www.", spoken);
        Assert.DoesNotContain("FAKE", spoken);
        Assert.DoesNotContain("@", spoken);
    }

    [Fact]
    public void InlineCodeUrl_IsNotSpelledOut()
    {
        var spoken = Project("Inline `https://api.example.com/v1?key=FAKE` code.");

        Assert.Equal("Inline code.", spoken);
        Assert.DoesNotContain("FAKE", spoken);
    }

    [Theory]
    [InlineData("Secret `api_key=FAKE-SECRET-1234` here.", "Secret here.")]
    [InlineData("Header `Authorization: Bearer FAKE-TOKEN` here.", "Header here.")]
    [InlineData("Value `password: hunter2` here.", "Value here.")]
    public void InlineCodeCredentialAssignment_IsDroppedWhole(string markdown, string expected)
    {
        var spoken = Project(markdown);

        Assert.Equal(expected, spoken);
        Assert.DoesNotContain("FAKE", spoken);
        Assert.DoesNotContain("hunter2", spoken);
    }

    [Theory]
    [InlineData("Install the .NET SDK.", "Install the .NET SDK.")]
    [InlineData("The app targets .NET 10.", "The app targets .NET 10.")]
    [InlineData("Use C#, .NET, or F# here.", "Use C#, .NET, or F# here.")]
    [InlineData(".NET is installed.", ".NET is installed.")]
    [InlineData("Run ./build now.", "Run ./build now.")]
    [InlineData("It costs .50 today.", "It costs .50 today.")]
    public void DotsThatStartOrJoinAWord_SurviveCleanup(string markdown, string expected)
    {
        Assert.Equal(expected, Project(markdown));
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
    [InlineData("هل انتهيت؟", "هل انتهيت؟")]
    [InlineData("यह पूरा हुआ।", "यह पूरा हुआ।")]
    [InlineData("He said \"stop.\"", "He said \"stop.\"")]
    [InlineData("That is (probably) fine.", "That is (probably) fine.")]
    [InlineData("「終わり。」", "「終わり。」")]
    public void ExistingTerminator_IsNotDoubled(string markdown, string expected)
    {
        Assert.Equal(expected, Project(markdown));
    }

    [Fact]
    public void Cjk_UsesFullWidthTerminators()
    {
        Assert.Equal("标题。内容。", Project("## 标题\n\n内容"));
    }

    [Theory]
    [InlineData("Great!🎉Next step.", "Great! Next step.")]
    [InlineData("Status ✅ done.", "Status done.")]
    [InlineData("Failed ❌ again.", "Failed again.")]
    [InlineData("Careful ⚠️ now.", "Careful now.")]
    public void Emoji_AreDroppedWithoutMergingNeighbouringWords(string markdown, string expected)
    {
        Assert.Equal(expected, Project(markdown));
    }

    [Fact]
    public void ZwjSequence_LeavesNoStrayWords()
    {
        // ZWJ must not become a space: it is a word joiner in Indic scripts and
        // only separates emoji inside a sequence.
        var spoken = Project("Family 👨‍👩‍👧‍👦 done.");

        Assert.Equal("Family done.", spoken);
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

    [Fact]
    public void LoneSurrogate_DoesNotThrowAndKeepsTheRestOfTheSentence()
    {
        // A truncated or malformed reply must not silence the whole message.
        var spoken = Project("Valid words \uD83D then more.");

        Assert.Contains("Valid words", spoken);
        Assert.Contains("then more.", spoken);
    }

    [Fact]
    public void DocumentTheParserRejects_ProducesNoSpeech()
    {
        // 101 nested containers exceed the vendored parser's nesting limit, so
        // the builder returns the verbatim source instead of an AST.
        var markdown = string.Concat(Enumerable.Repeat("> ", 102)) + "`api_key=FAKE`";

        var spoken = Project(markdown);

        Assert.Equal(string.Empty, spoken);
        Assert.DoesNotContain("FAKE", spoken);
    }

    [Fact]
    public void OversizedInput_IsTruncatedForSpeechButNotReplacedByTheFallback()
    {
        // Over the builder cap: the projector must parse its own truncated copy
        // instead of speaking the builder's verbatim fallback paragraph.
        var markdown = new string('a', ChatMarkdownAstBuilder.MaxInputBytes) + " tail.";

        var spoken = Project(markdown);

        Assert.StartsWith("aaa", spoken);
        Assert.DoesNotContain("[truncated", spoken);
        Assert.True(spoken.Length <= ChatMarkdownAstBuilder.MaxInputBytes + 1);
    }
}
