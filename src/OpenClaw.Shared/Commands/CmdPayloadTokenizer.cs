using System;
using System.Collections.Generic;

namespace OpenClaw.Shared.Commands;

/// <summary>
/// Single owner of "is this cmd payload a static command, and where exactly are its
/// tokens".
///
/// The payload is the text cmd.exe receives after <c>/c</c>. cmd parses that text
/// itself, so anything that could make cmd do something other than run one program
/// with fixed arguments (redirection, chaining, variable or delayed expansion, caret
/// escaping, quoting, grouping) makes the payload non-static and unusable for durable
/// approval.
///
/// Token spans are exposed because rewriting a payload has to be done by replacing a
/// known span, never by string search and replace. A naive replacement of an
/// executable name would also rewrite a later argument that happens to contain the
/// same text, silently changing the command that runs after it was approved.
/// </summary>
internal static class CmdPayloadTokenizer
{
    /// <summary>Half-open span of one token within the payload string.</summary>
    internal readonly record struct TokenSpan(int Start, int Length)
    {
        internal int End => Start + Length;
    }

    internal static bool TryTokenize(string payload, out IReadOnlyList<string> argv)
        => TryTokenize(payload, out argv, out _);

    internal static bool TryTokenize(
        string payload,
        out IReadOnlyList<string> argv,
        out IReadOnlyList<TokenSpan> spans)
    {
        argv = [];
        spans = [];
        if (payload is null)
            return false;

        var trimmedStart = payload.TrimStart();
        if (string.IsNullOrWhiteSpace(trimmedStart)
            || trimmedStart[0] == '@'
            || payload.Contains('"')
            || payload.TrimEnd(' ', '\t').EndsWith('\\'))
            return false;

        var tokens = new List<string>();
        var tokenSpans = new List<TokenSpan>();
        var tokenStart = -1;

        for (var i = 0; i < payload.Length; i++)
        {
            var ch = payload[i];
            if ((char.IsControl(ch) && ch != '\t')
                || (char.IsWhiteSpace(ch) && !IsCmdWhitespace(ch))
                || IsForbiddenCmdSyntax(ch))
                return false;

            if (IsCmdWhitespace(ch))
            {
                if (tokenStart >= 0)
                {
                    tokens.Add(payload[tokenStart..i]);
                    tokenSpans.Add(new TokenSpan(tokenStart, i - tokenStart));
                    tokenStart = -1;
                }
                continue;
            }

            if (tokenStart < 0)
                tokenStart = i;
        }

        if (tokenStart >= 0)
        {
            tokens.Add(payload[tokenStart..]);
            tokenSpans.Add(new TokenSpan(tokenStart, payload.Length - tokenStart));
        }

        if (tokens.Count == 0 || string.IsNullOrWhiteSpace(tokens[0]))
            return false;

        argv = tokens;
        spans = tokenSpans;
        return true;
    }

    /// <summary>
    /// True when <paramref name="value"/> can be written into a cmd payload as a single
    /// token that cmd will read back byte for byte.
    ///
    /// Whitespace is refused rather than quoted. Under <c>/s</c> cmd strips the first
    /// and last quote of the whole payload and uses the remainder verbatim, so quoting
    /// a leading executable token does not protect it: the quotes are removed and the
    /// path is left ambiguous again, which is the very substitution this pinning exists
    /// to prevent. A path containing a space therefore fails closed to prompt-only
    /// instead of being pinned unsafely.
    /// </summary>
    internal static bool IsSafelyRepresentableToken(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        if (value[0] == '@')
            return false;
        if (value.EndsWith('\\'))
            return false;

        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch)
                || char.IsControl(ch)
                || ch == '"'
                || IsCmdCommandNameDelimiter(ch)
                || IsForbiddenCmdSyntax(ch))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Replaces the executable token of <paramref name="payload"/> with
    /// <paramref name="pinnedExecutable"/>, preserving every other byte of the payload
    /// including its exact interior spacing.
    ///
    /// Returns false unless the result tokenizes back to the same argument list with
    /// only the executable changed. That round trip is the proof that the rewrite
    /// introduced no drift; it is not a formality, because it is the only thing
    /// standing between "we approved one command" and "cmd ran another".
    /// </summary>
    internal static bool TryPinExecutable(
        string payload,
        string pinnedExecutable,
        out string pinnedPayload)
    {
        pinnedPayload = "";
        if (!IsSafelyRepresentableToken(pinnedExecutable))
            return false;
        if (!TryTokenize(payload, out var originalArgv, out var spans) || spans.Count == 0)
            return false;

        var head = spans[0];
        var candidate = string.Concat(
            payload.AsSpan(0, head.Start),
            pinnedExecutable,
            payload.AsSpan(head.End));

        if (!TryTokenize(candidate, out var candidateArgv, out _))
            return false;
        if (candidateArgv.Count != originalArgv.Count)
            return false;
        if (!string.Equals(candidateArgv[0], pinnedExecutable, StringComparison.Ordinal))
            return false;
        for (var i = 1; i < candidateArgv.Count; i++)
        {
            if (!string.Equals(candidateArgv[i], originalArgv[i], StringComparison.Ordinal))
                return false;
        }

        pinnedPayload = candidate;
        return true;
    }

    private static bool IsForbiddenCmdSyntax(char ch) =>
        ch is '&' or '|' or '<' or '>' or '^' or '%' or '!' or '(' or ')';

    /// <summary>
    /// cmd ends the command-name token at these characters as well as whitespace. Our
    /// own tokenizer deliberately does not model them (a longer token simply fails to
    /// resolve, which is fail-closed), but a pinned path containing one of them would
    /// be split by cmd into a different program plus an argument. That is drift the
    /// round trip cannot see, so such paths are refused outright.
    /// </summary>
    private static bool IsCmdCommandNameDelimiter(char ch) => ch is ',' or ';' or '=';

    private static bool IsCmdWhitespace(char ch) => ch is ' ' or '\t';
}
