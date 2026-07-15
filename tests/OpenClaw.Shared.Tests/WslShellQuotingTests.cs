using Xunit;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Unit tests for <see cref="WslShellQuoting"/> — POSIX/WSL single-quote quoting.
/// Focus is injection safety: arbitrary values (embedded quotes, shell
/// metacharacters, newlines, URLs, JSON, Windows paths) must survive as a single
/// literal token when wrapped, using the <c>'\''</c> idiom rather than the
/// cmd/PowerShell rules in <see cref="ShellQuoting"/>.
/// </summary>
public class WslShellQuotingTests
{
    // The canonical POSIX escaped single quote: close ' , literal \' , reopen '.
    private const string Q = "'\\''";

    // ── EscapePosixSingleQuoteInner (escape only, no wrapping) ──────

    [Fact]
    public void EscapeInner_Empty_ReturnsEmpty()
    {
        Assert.Equal("", WslShellQuoting.EscapePosixSingleQuoteInner(""));
    }

    [Fact]
    public void EscapeInner_PlainText_Unchanged()
    {
        Assert.Equal("plain-value", WslShellQuoting.EscapePosixSingleQuoteInner("plain-value"));
    }

    [Fact]
    public void EscapeInner_SingleQuote_UsesPosixIdiom()
    {
        Assert.Equal(Q, WslShellQuoting.EscapePosixSingleQuoteInner("'"));
    }

    [Fact]
    public void EscapeInner_EmbeddedQuote_EscapesInPlace()
    {
        Assert.Equal("O" + Q + "Brien", WslShellQuoting.EscapePosixSingleQuoteInner("O'Brien"));
    }

    [Fact]
    public void EscapeInner_MultipleQuotes_EscapesEach()
    {
        Assert.Equal(Q + Q, WslShellQuoting.EscapePosixSingleQuoteInner("''"));
    }

    [Fact]
    public void EscapeInner_AddsNoOuterQuotes()
    {
        var result = WslShellQuoting.EscapePosixSingleQuoteInner("plain");
        Assert.False(result.StartsWith('\''));
        Assert.False(result.EndsWith('\''));
    }

    [Theory]
    [InlineData("$(rm -rf /)")]
    [InlineData("`id`")]
    [InlineData("a\nb")]
    [InlineData("C:\\Users\\name")]
    public void EscapeInner_MetacharactersWithoutQuotes_Unchanged(string value)
    {
        // Only single quotes are special inside POSIX single quotes; everything
        // else is preserved verbatim by the escape-only operation.
        Assert.Equal(value, WslShellQuoting.EscapePosixSingleQuoteInner(value));
    }

    // ── QuotePosixSingleQuote (fully wrapped token) ─────────────────

    [Fact]
    public void Quote_Empty_ReturnsEmptyQuotes()
    {
        // An empty argument must still be emitted, not omitted.
        Assert.Equal("''", WslShellQuoting.QuotePosixSingleQuote(""));
    }

    [Fact]
    public void Quote_Whitespace_IsWrapped()
    {
        Assert.Equal("' '", WslShellQuoting.QuotePosixSingleQuote(" "));
    }

    [Fact]
    public void Quote_PlainText_IsWrapped()
    {
        Assert.Equal("'plain'", WslShellQuoting.QuotePosixSingleQuote("plain"));
    }

    /// <summary>Guard test referenced by the architecture ledger (wsl-posix-quoting).</summary>
    [Fact]
    public void QuotePosixSingleQuote_WrapsAndEscapesEmbeddedQuote()
    {
        Assert.Equal("'O" + Q + "Brien'", WslShellQuoting.QuotePosixSingleQuote("O'Brien"));
    }

    [Fact]
    public void Quote_SingleQuoteOnly_IsEscapedAndWrapped()
    {
        Assert.Equal("'" + Q + "'", WslShellQuoting.QuotePosixSingleQuote("'"));
    }

    [Fact]
    public void Quote_Newline_PreservedInsideQuotes()
    {
        Assert.Equal("'a\nb'", WslShellQuoting.QuotePosixSingleQuote("a\nb"));
    }

    [Fact]
    public void Quote_MultiLineFragment_PreservedVerbatim()
    {
        Assert.Equal("'line1\nline2\n'", WslShellQuoting.QuotePosixSingleQuote("line1\nline2\n"));
    }

    [Fact]
    public void Quote_CommandSubstitution_IsInert()
    {
        Assert.Equal("'$(rm -rf /)'", WslShellQuoting.QuotePosixSingleQuote("$(rm -rf /)"));
    }

    [Fact]
    public void Quote_Backticks_IsInert()
    {
        Assert.Equal("'`id`'", WslShellQuoting.QuotePosixSingleQuote("`id`"));
    }

    [Fact]
    public void Quote_Url_IsWrappedVerbatim()
    {
        const string url = "https://example.com/install?token=a&b=2";
        Assert.Equal("'" + url + "'", WslShellQuoting.QuotePosixSingleQuote(url));
    }

    [Fact]
    public void Quote_JsonArray_IsWrappedVerbatim()
    {
        const string json = """["a","b","c"]""";
        Assert.Equal("'" + json + "'", WslShellQuoting.QuotePosixSingleQuote(json));
    }

    [Fact]
    public void Quote_WindowsPath_BackslashesPreserved()
    {
        const string path = @"C:\Users\Some One\file.txt";
        Assert.Equal("'" + path + "'", WslShellQuoting.QuotePosixSingleQuote(path));
    }

    [Fact]
    public void Quote_AlreadyQuotedLookingInput_EscapesBothQuotes()
    {
        Assert.Equal("'" + Q + "already" + Q + "'", WslShellQuoting.QuotePosixSingleQuote("'already'"));
    }

    [Fact]
    public void Quote_QuoteBreakoutAttempt_IsNeutralized()
    {
        // A classic breakout payload: the embedded single quote must not terminate
        // the quoted token, so the trailing shell command stays inert text.
        var result = WslShellQuoting.QuotePosixSingleQuote("x'; rm -rf / #");
        Assert.Equal("'x" + Q + "; rm -rf / #'", result);
    }

    // ── Cross-cutting invariants ────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData("O'Brien")]
    [InlineData("'already'")]
    [InlineData("$(id)")]
    [InlineData("a\nb")]
    public void Quote_IsInnerEscapeWrappedInQuotes(string value)
    {
        var expected = "'" + WslShellQuoting.EscapePosixSingleQuoteInner(value) + "'";
        Assert.Equal(expected, WslShellQuoting.QuotePosixSingleQuote(value));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("O'Brien")]
    [InlineData("$(id)")]
    public void Quote_ContainsNoBareUnescapedSingleQuote(string value)
    {
        // Every interior single quote of a wrapped token belongs to the '\'' idiom,
        // so replacing that idiom leaves no stray single quotes to break the token.
        var result = WslShellQuoting.QuotePosixSingleQuote(value);
        var inner = result[1..^1]; // strip the outer wrapping quotes
        var withoutIdiom = inner.Replace(Q, "");
        Assert.DoesNotContain('\'', withoutIdiom);
    }

    [Fact]
    public void Quote_DiffersFromPowerShellQuoting_ForEmbeddedQuote()
    {
        // POSIX uses the '\'' idiom; PowerShell doubles the quote. Guards against a
        // future accidental reuse of ShellQuoting for WSL command lines.
        var posix = WslShellQuoting.QuotePosixSingleQuote("it's");
        var powershell = ShellQuoting.QuoteForShell("it's", isCmd: false);
        Assert.Equal("'it" + Q + "s'", posix);
        Assert.Equal("'it''s'", powershell);
        Assert.NotEqual(powershell, posix);
    }

    // ── Null handling ───────────────────────────────────────────────

    [Fact]
    public void EscapeInner_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => WslShellQuoting.EscapePosixSingleQuoteInner(null!));
    }

    [Fact]
    public void Quote_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => WslShellQuoting.QuotePosixSingleQuote(null!));
    }
}
