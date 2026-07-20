namespace OpenClaw.Shared;

/// <summary>
/// POSIX / WSL single-quote quoting. This is a deliberately separate contract
/// from <see cref="ShellQuoting"/>, which targets Windows cmd.exe / PowerShell
/// (double quotes, or single quotes doubled as <c>''</c>). Those rules are
/// <em>wrong and injection-unsafe</em> for the bash/sh command lines OpenClaw
/// builds to run inside WSL.
///
/// A POSIX shell performs no processing whatsoever inside single quotes - there
/// is no backslash escape for a single quote. The only way to place a literal
/// single quote in a single-quoted string is to close the quote, emit an escaped
/// literal quote, then reopen: the canonical <c>'\''</c> idiom. Every embedded
/// single quote is replaced by that four-character sequence.
///
/// The two operations are kept distinct on purpose (see docs/ARCHITECTURE.md →
/// <c>wsl-posix-quoting</c>); conflating "escape the inner text" with "produce a
/// wrapped token" is exactly the divergent-semantics bug this type retires:
/// <list type="bullet">
/// <item><see cref="EscapePosixSingleQuoteInner"/> escapes embedded quotes for a
/// value the caller is placing between its <em>own</em> outer single quotes, and
/// adds no quotes of its own.</item>
/// <item><see cref="QuotePosixSingleQuote"/> returns a complete, standalone
/// single-quoted token (<c>''</c> for an empty value).</item>
/// </list>
/// Neither operation trims, collapses, or otherwise interprets the input: the
/// bytes between the quotes are preserved verbatim, which is what makes the
/// result injection-safe for arbitrary values (URLs, JSON, paths, newlines).
/// </summary>
internal static class WslShellQuoting
{
    // Close quote ('), an escaped literal quote (\'), then reopen quote (').
    private const string EscapedSingleQuote = "'\\''";

    /// <summary>
    /// Escapes embedded single quotes using the POSIX <c>'\''</c> idiom, for a
    /// value the caller will wrap in its own outer single quotes. Does NOT add
    /// outer quotes. An empty input yields an empty string.
    /// </summary>
    internal static string EscapePosixSingleQuoteInner(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Replace("'", EscapedSingleQuote);
    }

    /// <summary>
    /// Wraps <paramref name="value"/> in single quotes, escaping any embedded
    /// single quotes, so the result is exactly one POSIX-shell token. An empty
    /// input yields <c>''</c> (an empty argument, not an omitted one).
    /// </summary>
    internal static string QuotePosixSingleQuote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return string.Concat("'", EscapePosixSingleQuoteInner(value), "'");
    }
}
