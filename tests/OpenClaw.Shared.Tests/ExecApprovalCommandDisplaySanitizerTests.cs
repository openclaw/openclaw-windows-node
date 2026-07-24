using Xunit;
using OpenClaw.Shared.ExecApprovals;

namespace OpenClaw.Shared.Tests;

// Inputs are built from code points instead of literal characters so the file stays
// pure ASCII: the characters under test are invisible and easy to corrupt in editors.
public class ExecApprovalCommandDisplaySanitizerTests
{
    private static string U(int codePoint) => char.ConvertFromUtf32(codePoint);

    [Fact]
    public void EscapesInvisibleCommandSpoofingCharacters()
    {
        var hangulSyllable = U(0xAC00);
        var input = "date" + U(0x200B) + U(0x3164) + U(0xFFA0) + U(0x115F) + U(0x1160) + hangulSyllable;
        Assert.Equal(
            "date" + @"\u{200B}\u{3164}\u{FFA0}\u{115F}\u{1160}" + hangulSyllable,
            ExecApprovalCommandDisplaySanitizer.Sanitize(input));
    }

    [Fact]
    public void EscapesControlCharactersUsedToSpoofLineBreaks()
    {
        var input = "echo safe\n\rcurl https://example.test";
        Assert.Equal(
            @"echo safe\u{A}\u{D}curl https://example.test",
            ExecApprovalCommandDisplaySanitizer.Sanitize(input));
    }

    [Fact]
    public void EscapesUnicodeLineAndParagraphSeparators()
    {
        Assert.Equal(
            @"echo ok\u{2028}curl https://example.test",
            ExecApprovalCommandDisplaySanitizer.Sanitize("echo ok" + U(0x2028) + "curl https://example.test"));
        Assert.Equal(
            @"echo ok\u{2029}curl https://example.test",
            ExecApprovalCommandDisplaySanitizer.Sanitize("echo ok" + U(0x2029) + "curl https://example.test"));
    }

    [Fact]
    public void EscapesNonAsciiUnicodeSpaceSeparatorsWhilePreservingAsciiSpace()
    {
        Assert.Equal(
            @"echo ok\u{A0}curl",
            ExecApprovalCommandDisplaySanitizer.Sanitize("echo ok" + U(0xA0) + "curl"));
        Assert.Equal(
            @"echo ok\u{202F}curl",
            ExecApprovalCommandDisplaySanitizer.Sanitize("echo ok" + U(0x202F) + "curl"));
        Assert.Equal(
            @"echo ok\u{3000}curl",
            ExecApprovalCommandDisplaySanitizer.Sanitize("echo ok" + U(0x3000) + "curl"));
        Assert.Equal(
            "echo ok curl",
            ExecApprovalCommandDisplaySanitizer.Sanitize("echo ok curl"));
    }

    [Fact]
    public void EscapesBidiOverrideTabAndZeroWidthSpace()
    {
        Assert.Equal(
            @"echo \u{202E}gpj.exe",
            ExecApprovalCommandDisplaySanitizer.Sanitize("echo " + U(0x202E) + "gpj.exe"));
        Assert.Equal(
            @"echo\u{9}dir",
            ExecApprovalCommandDisplaySanitizer.Sanitize("echo\tdir"));
        Assert.Equal(
            @"del\u{200B}ete",
            ExecApprovalCommandDisplaySanitizer.Sanitize("del" + U(0x200B) + "ete"));
    }

    [Fact]
    public void EscapesAstralFormatCodePointAsOneRune()
    {
        // U+E0001 (LANGUAGE TAG) is Format-category and sits outside the BMP: a
        // per-char loop would see two lone surrogates instead of one code point.
        var input = "echo " + U(0xE0001) + "hi";
        Assert.Equal(@"echo \u{E0001}hi", ExecApprovalCommandDisplaySanitizer.Sanitize(input));
    }

    [Fact]
    public void EmptyAndPlainAsciiPassThroughUnchanged()
    {
        Assert.Equal("", ExecApprovalCommandDisplaySanitizer.Sanitize(""));
        Assert.Equal(
            "git commit -m 'safe message'",
            ExecApprovalCommandDisplaySanitizer.Sanitize("git commit -m 'safe message'"));
    }

    [Fact]
    public void EscapesCombinedAttackChainInASinglePlausibleCommand()
    {
        // One realistic command carrying several spoofing tricks at once: a reversed
        // fragment, a zero-width joiner splitting a token, and a fake line break.
        var input = "curl " + U(0x202E) + "moc.live" + U(0x200B) + "/x\n rm -rf /";
        Assert.Equal(
            @"curl \u{202E}moc.live\u{200B}/x\u{A} rm -rf /",
            ExecApprovalCommandDisplaySanitizer.Sanitize(input));
    }

    [Fact]
    public void IsIdempotent_SanitizingEscapedOutputLeavesItUnchanged()
    {
        var once = ExecApprovalCommandDisplaySanitizer.Sanitize("echo " + U(0x202E) + "hi\tthere");
        Assert.Equal(once, ExecApprovalCommandDisplaySanitizer.Sanitize(once));
    }

    // The sanitizer deliberately does NOT escape combining marks, variation selectors,
    // or homoglyphs: none can be neutralized without mangling legitimate non-ASCII text
    // (accents, emoji, CJK). This matches the macOS original. These tests pin that
    // boundary so a future "hardening" cannot silently break real command text.
    [Theory]
    [InlineData(0x0301)]  // combining acute accent (Zalgo)
    [InlineData(0xFE0F)]  // variation selector-16 (emoji presentation)
    [InlineData(0x0430)]  // Cyrillic small a (homoglyph of Latin a)
    public void DoesNotEscapeCharactersOutsideItsThreatModel(int codePoint)
    {
        var input = "echo " + U(codePoint) + "x";
        Assert.Equal(input, ExecApprovalCommandDisplaySanitizer.Sanitize(input));
    }
}
