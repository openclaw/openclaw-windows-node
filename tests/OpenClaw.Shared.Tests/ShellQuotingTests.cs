using Xunit;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Unit tests for ShellQuoting — argument quoting and metacharacter detection.
/// </summary>
public class ShellQuotingTests
{
    // ── NeedsQuoting ────────────────────────────────────────────────

    [Theory]
    [InlineData("hello")]
    [InlineData("--flag")]
    [InlineData("C:\\Windows\\System32")]
    [InlineData("123")]
    public void NeedsQuoting_PlainArgs_ReturnsFalse(string arg)
    {
        Assert.False(ShellQuoting.NeedsQuoting(arg));
    }

    [Theory]
    [InlineData("hello world", ' ')]
    [InlineData("a\tb", '\t')]
    [InlineData("say \"hi\"", '"')]
    [InlineData("it's", '\'')]
    [InlineData("a&b", '&')]
    [InlineData("a|b", '|')]
    [InlineData("a;b", ';')]
    [InlineData("a<b", '<')]
    [InlineData("a>b", '>')]
    [InlineData("(cmd)", '(')]
    [InlineData("a^b", '^')]
    [InlineData("100%", '%')]
    [InlineData("!bang", '!')]
    [InlineData("$var", '$')]
    [InlineData("`tick", '`')]
    [InlineData("*.txt", '*')]
    [InlineData("file?.log", '?')]
    [InlineData("[0]", '[')]
    [InlineData("{a,b}", '{')]
    [InlineData("~user", '~')]
    public void NeedsQuoting_ShellMetachars_ReturnsTrue(string arg, char _)
    {
        Assert.True(ShellQuoting.NeedsQuoting(arg));
    }

    // ── QuoteForShell (cmd.exe) ─────────────────────────────────────

    [Fact]
    public void QuoteForShell_Cmd_EmptyArg_ReturnsEmptyDoubleQuotes()
    {
        Assert.Equal("\"\"", ShellQuoting.QuoteForShell("", isCmd: true));
    }

    [Fact]
    public void QuoteForShell_Cmd_PlainArg_ReturnsUnquoted()
    {
        Assert.Equal("hello", ShellQuoting.QuoteForShell("hello", isCmd: true));
    }

    [Fact]
    public void QuoteForShell_Cmd_ArgWithSpaces_WrapsInDoubleQuotes()
    {
        Assert.Equal("\"hello world\"", ShellQuoting.QuoteForShell("hello world", isCmd: true));
    }

    [Fact]
    public void QuoteForShell_Cmd_ArgWithInnerDoubleQuotes_DoublesQuotes()
    {
        // cmd.exe escapes " by doubling: say "hi" → "say ""hi"""
        Assert.Equal("\"say \"\"hi\"\"\"", ShellQuoting.QuoteForShell("say \"hi\"", isCmd: true));
    }

    [Fact]
    public void QuoteForShell_Cmd_ArgWithPipe_WrapsInDoubleQuotes()
    {
        Assert.Equal("\"a|b\"", ShellQuoting.QuoteForShell("a|b", isCmd: true));
    }

    [Fact]
    public void QuoteForShell_Cmd_ArgWithAmpersand_WrapsInDoubleQuotes()
    {
        Assert.Equal("\"a&b\"", ShellQuoting.QuoteForShell("a&b", isCmd: true));
    }

    // ── QuoteForShell (PowerShell) ──────────────────────────────────

    [Fact]
    public void QuoteForShell_PS_EmptyArg_ReturnsEmptySingleQuotes()
    {
        Assert.Equal("''", ShellQuoting.QuoteForShell("", isCmd: false));
    }

    [Fact]
    public void QuoteForShell_PS_PlainArg_ReturnsUnquoted()
    {
        Assert.Equal("hello", ShellQuoting.QuoteForShell("hello", isCmd: false));
    }

    [Fact]
    public void QuoteForShell_PS_ArgWithSpaces_WrapsInSingleQuotes()
    {
        Assert.Equal("'hello world'", ShellQuoting.QuoteForShell("hello world", isCmd: false));
    }

    [Fact]
    public void QuoteForShell_PS_ArgWithInnerSingleQuotes_DoublesQuotes()
    {
        // PowerShell escapes ' by doubling: it's → 'it''s'
        Assert.Equal("'it''s'", ShellQuoting.QuoteForShell("it's", isCmd: false));
    }

    [Fact]
    public void QuoteForShell_PS_ArgWithDollarSign_WrapsInSingleQuotes()
    {
        // Single quotes prevent PS variable expansion
        Assert.Equal("'$HOME'", ShellQuoting.QuoteForShell("$HOME", isCmd: false));
    }

    [Fact]
    public void QuoteForShell_PS_ArgWithBacktick_WrapsInSingleQuotes()
    {
        Assert.Equal("'`n'", ShellQuoting.QuoteForShell("`n", isCmd: false));
    }

    // ── FormatExecCommand ───────────────────────────────────────────

    [Fact]
    public void FormatExecCommand_SimpleArgs_JoinsWithSpaces()
    {
        var result = ShellQuoting.FormatExecCommand(new[] { "echo", "hello" });
        Assert.Equal("echo hello", result);
    }

    [Fact]
    public void FormatExecCommand_EmptyArg_UsesEmptyQuotes()
    {
        var result = ShellQuoting.FormatExecCommand(new[] { "cmd", "" });
        Assert.Equal("cmd \"\"", result);
    }

    [Fact]
    public void FormatExecCommand_ArgWithSpaces_QuotesIt()
    {
        var result = ShellQuoting.FormatExecCommand(new[] { "echo", "hello world" });
        Assert.Equal("echo \"hello world\"", result);
    }

    [Fact]
    public void FormatExecCommand_ArgWithMetachars_QuotesIt()
    {
        var result = ShellQuoting.FormatExecCommand(new[] { "cmd", "/C", "a&b" });
        Assert.Equal("cmd /C \"a&b\"", result);
    }

    [Fact]
    public void FormatExecCommand_ArgWithInnerQuotes_EscapesWithBackslash()
    {
        // FormatExecCommand uses backslash escaping (gateway display convention)
        var result = ShellQuoting.FormatExecCommand(new[] { "echo", "say \"hi\"" });
        Assert.Equal("echo \"say \\\"hi\\\"\"", result);
    }

    // ── Null handling ───────────────────────────────────────────────

    [Fact]
    public void QuoteForShell_NullArg_Cmd_ReturnsEmptyQuotes()
    {
        Assert.Equal("\"\"", ShellQuoting.QuoteForShell(null!, isCmd: true));
    }

    [Fact]
    public void QuoteForShell_NullArg_PS_ReturnsEmptySingleQuotes()
    {
        Assert.Equal("''", ShellQuoting.QuoteForShell(null!, isCmd: false));
    }

    // ── Additional NeedsQuoting metachar coverage ───────────────────

    [Theory]
    [InlineData("a]b", ']')]
    [InlineData("a}b", '}')]
    [InlineData("a)b", ')')]
    public void NeedsQuoting_ClosingBracketsAndParen_ReturnsTrue(string arg, char _)
    {
        Assert.True(ShellQuoting.NeedsQuoting(arg));
    }

    [Fact]
    public void NeedsQuoting_NewlineChars_ReturnTrue()
    {
        Assert.True(ShellQuoting.NeedsQuoting("line1\nline2"));
        Assert.True(ShellQuoting.NeedsQuoting("line1\rline2"));
    }

    // ── FormatExecCommand edge cases ────────────────────────────────

    [Fact]
    public void FormatExecCommand_SingleArg_ReturnsArgUnchanged()
    {
        Assert.Equal("echo", ShellQuoting.FormatExecCommand(new[] { "echo" }));
    }

    [Fact]
    public void FormatExecCommand_EmptyArray_ReturnsEmptyString()
    {
        Assert.Equal("", ShellQuoting.FormatExecCommand(Array.Empty<string>()));
    }
}
