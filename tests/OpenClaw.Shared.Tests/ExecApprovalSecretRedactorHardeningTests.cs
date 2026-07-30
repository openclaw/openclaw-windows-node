using Xunit;
using OpenClaw.Shared.ExecApprovals;

namespace OpenClaw.Shared.Tests;

// Regressions for review findings B (Unicode word boundary), C (astral surrogate), and
// E (ReDoS / fail-open) in the ported secret redactor.
public class ExecApprovalSecretRedactorHardeningTests
{
    [Fact]
    public void SkToken_AfterNonAsciiLetter_IsRedacted()
    {
        // Finding B: .NET \b is Unicode; a token preceded by a non-ASCII letter must still be
        // redacted (JS ASCII boundary semantics). "é" directly precedes the sk- token.
        var result = ExecApprovalSecretRedactor.Redact("\u00E9sk-abcdefghijk");
        Assert.DoesNotContain("abcdefghijk", result);
        Assert.NotEqual("\u00E9sk-abcdefghijk", result);
    }

    [Fact]
    public void SkToken_PlainAscii_StillRedacted()
    {
        var result = ExecApprovalSecretRedactor.Redact("OPENAI_API_KEY=sk-1234567890abcdef");
        Assert.DoesNotContain("1234567890abcdef", result);
    }

    [Fact]
    public void AstralEmoji_NotStrippedAsInvisible_InFormKey()
    {
        // Finding C: an astral emoji is two UTF-16 surrogates; it must not be treated as an
        // invisible char (which would misread the key as "client_secret" and both strip the
        // emoji and redact an unrelated value).
        const string input = "body: client_se\U0001F600cret=payload";
        var result = ExecApprovalSecretRedactor.Redact(input);
        Assert.Contains("\U0001F600", result); // emoji preserved
        Assert.Contains("payload", result);      // value not redacted (key is not a secret key)
    }

    [Fact]
    public void DiscordToken_StillRedacted_AfterRegexHardening()
    {
        // Finding E: the Discord regex was rewritten to a non-backtracking form; it must still
        // redact a valid token.
        var token = new string('a', 24) + "." + new string('b', 6) + "." + new string('c', 27);
        var result = ExecApprovalSecretRedactor.Redact("discord " + token);
        Assert.DoesNotContain(token, result);
    }
}
