using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OpenClaw.Shared.ExecApprovals;

namespace OpenClaw.Shared;

internal sealed class ExecShellEvaluationTarget
{
    public string Command { get; init; } = "";
    public string? Shell { get; init; }
}

internal sealed class ExecShellParseResult
{
    public List<ExecShellEvaluationTarget> Targets { get; } = new();
    public string? Error { get; init; }
}

internal static class ExecShellWrapperParser
{
    private const int MaxDepth = 4;

    internal static ExecShellParseResult Expand(string command, string? shell = null)
    {
        var result = new ExecShellParseResult();

        if (string.IsNullOrWhiteSpace(command))
            return result;

        var pending = new Queue<(string Command, string? Shell, int Depth)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue((command, NormalizeShell(shell), 0));

        while (pending.Count > 0)
        {
            var (current, currentShell, depth) = pending.Dequeue();
            if (string.IsNullOrWhiteSpace(current) || depth > MaxDepth)
                continue;

            var segments = SplitTopLevelCommands(current);
            var hasMultipleSegments = segments.Count > 1;

            foreach (var rawSegment in segments)
            {
                var segment = TrimMatchingQuotes(rawSegment.Trim());
                if (string.IsNullOrWhiteSpace(segment))
                    continue;

                // Fail closed on an execution-introducing construct the parser cannot safely
                // decompose: an unquoted subexpression/subshell `(` (PowerShell and POSIX shells
                // EXECUTE it), or - for POSIX shells - an unquoted command-substitution backtick.
                // $(...) is exempt (decomposed into its own target below); cmd is exempt (no
                // $()/backtick, and its `(...)` grouping chains via ; & | which are already split -
                // so `C:\Program Files (x86)` paths stay valid).
                if (HasUndecomposableExec(segment, currentShell))
                {
                    return new ExecShellParseResult
                    {
                        Error = "Command contains a shell subexpression or substitution that policy cannot evaluate"
                    };
                }

                if ((depth > 0 || hasMultipleSegments) && seen.Add($"{currentShell}|{segment}"))
                {
                    result.Targets.Add(new ExecShellEvaluationTarget
                    {
                        Command = segment,
                        Shell = currentShell
                    });
                }

                var wrapped = TryExtractWrappedPayload(segment);
                if (wrapped.Error != null)
                {
                    return new ExecShellParseResult { Error = wrapped.Error };
                }

                if (!string.IsNullOrWhiteSpace(wrapped.Payload))
                {
                    pending.Enqueue((wrapped.Payload!, wrapped.Shell ?? currentShell, depth + 1));
                }

                // Command substitution / subexpression - $(...), @(...), `...` - runs the enclosed
                // command and splices its output, so the shell executes it. Surface each inner
                // command for approval too; otherwise `echo $(Remove-Item ...)` runs a denied
                // command that never appears as a target.
                foreach (var inner in ExtractCommandSubstitutions(segment))
                {
                    pending.Enqueue((inner, currentShell, depth + 1));
                }
            }
        }

        return result;
    }

    private static (string? Payload, string? Shell, string? Error) TryExtractWrappedPayload(string command)
    {
        var tokens = Tokenize(command);
        if (tokens.Length < 2)
            return default;

        var executable = ExecCommandToken.NormalizedBasename(tokens[0]);
        if (string.IsNullOrWhiteSpace(executable))
            return default;

        if (executable == "cmd")
        {
            for (var i = 1; i < tokens.Length; i++)
            {
                if (tokens[i].Equals("/c", StringComparison.OrdinalIgnoreCase) ||
                    tokens[i].Equals("/k", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = string.Join(" ", tokens, i + 1, tokens.Length - i - 1).Trim();
                    return string.IsNullOrWhiteSpace(payload)
                        ? ("", "cmd", "Shell wrapper payload was empty")
                        : (payload, "cmd", null);
                }
            }
        }

        if (executable == "powershell")
            return ParsePowerShellPayload(tokens, "powershell");

        if (executable == "pwsh")
            return ParsePowerShellPayload(tokens, "pwsh");

        if (executable is "bash" or "sh" or "zsh" or "dash" or "ash" or "ksh" or "fish")
        {
            for (var i = 1; i < tokens.Length; i++)
            {
                if (tokens[i].Equals("-c", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = string.Join(" ", tokens, i + 1, tokens.Length - i - 1).Trim();
                    return string.IsNullOrWhiteSpace(payload)
                        ? ("", "sh", "Shell wrapper payload was empty")
                        : (payload, "sh", null);
                }
            }
        }

        return default;
    }

    private static (string? Payload, string? Shell, string? Error) ParsePowerShellPayload(string[] tokens, string shell)
    {
        for (var i = 1; i < tokens.Length; i++)
        {
            var option = tokens[i];

            // Check for inline separator form first: -flag:value or -flag=value
            var sepIdx = IndexOfFlagSeparator(option);
            if (sepIdx > 0)
            {
                var flagPart = option[..sepIdx];
                var valuePart = option[(sepIdx + 1)..];

                if (IsCommandFlag(flagPart))
                {
                    return string.IsNullOrWhiteSpace(valuePart)
                        ? ("", shell, "Shell wrapper payload was empty")
                        : (valuePart, shell, null);
                }

                if (IsEncodedCommandFlag(flagPart))
                    return DecodeEncodedPayload(valuePart, shell);
            }

            if (IsCommandFlag(option))
            {
                var payload = string.Join(" ", tokens, i + 1, tokens.Length - i - 1).Trim();
                return string.IsNullOrWhiteSpace(payload)
                    ? ("", shell, "Shell wrapper payload was empty")
                    : (payload, shell, null);
            }

            if (IsEncodedCommandFlag(option))
            {
                var encoded = i + 1 < tokens.Length ? tokens[i + 1] : null;
                return DecodeEncodedPayload(encoded, shell);
            }
        }

        return default;
    }

    // Returns the index of the first ':' or '=' in a flag token (after the leading '-').
    private static int IndexOfFlagSeparator(string token)
    {
        for (var i = 1; i < token.Length; i++)
        {
            if (token[i] == ':' || token[i] == '=')
                return i;
        }
        return -1;
    }

    // Matches -Command and -c (documented PowerShell -Command aliases).
    private static bool IsCommandFlag(string flag) =>
        flag.Equals("-Command", StringComparison.OrdinalIgnoreCase) ||
        flag.Equals("-c", StringComparison.OrdinalIgnoreCase);

    // Matches -e/-ec aliases and all unique prefix abbreviations of -EncodedCommand.
    // Windows PowerShell accepts -e as EncodedCommand despite the apparent ambiguity with
    // -ExecutionPolicy, so the parser must fail closed and decode it.
    private static bool IsEncodedCommandFlag(string flag)
    {
        if (flag.Equals("-e", StringComparison.OrdinalIgnoreCase))
            return true;

        if (flag.Equals("-ec", StringComparison.OrdinalIgnoreCase))
            return true;

        const string fullFlag = "-encodedcommand";
        return flag.Length >= 3 &&  // minimum: -en
               flag.Length <= fullFlag.Length &&
               fullFlag.StartsWith(flag, StringComparison.OrdinalIgnoreCase);
    }

    private static (string? Payload, string? Shell, string? Error) DecodeEncodedPayload(string? encoded, string shell)
    {
        if (string.IsNullOrWhiteSpace(encoded))
            return ("", shell, "Shell wrapper payload was empty");

        try
        {
            var bytes = Convert.FromBase64String(encoded);
            var payload = Encoding.Unicode.GetString(bytes).Trim();
            return string.IsNullOrWhiteSpace(payload)
                ? ("", shell, "EncodedCommand decoded to an empty payload")
                : (payload, shell, null);
        }
        catch (FormatException)
        {
            return ("", shell, "EncodedCommand could not be decoded");
        }
    }

    private static List<string> SplitTopLevelCommands(string command)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var inSingleQuotes = false;
        var inDoubleQuotes = false;
        // Depth of unquoted parentheses. Separators inside a $(...) / @(...) / (...) group belong
        // to that sub-expression, not the top level, so they must not split here - the group's
        // contents are surfaced separately via ExtractCommandSubstitutions and re-expanded.
        var parenDepth = 0;

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];

            if (c == '"' && !inSingleQuotes)
            {
                inDoubleQuotes = !inDoubleQuotes;
                current.Append(c);
                continue;
            }

            if (c == '\'' && !inDoubleQuotes)
            {
                inSingleQuotes = !inSingleQuotes;
                current.Append(c);
                continue;
            }

            if (!inSingleQuotes && !inDoubleQuotes)
            {
                if (c == '(')
                {
                    parenDepth++;
                }
                else if (c == ')')
                {
                    if (parenDepth > 0)
                        parenDepth--;
                }
                else if (parenDepth == 0)
                {
                    if (c == ';' || c == '&')
                    {
                        FlushCurrent(parts, current);
                        if (c == '&' && i + 1 < command.Length && command[i + 1] == '&')
                            i++;
                        continue;
                    }

                    // A pipeline stage is a distinct command the shell runs - `a | b` executes both
                    // `a` and `b` - so `|` is a top-level separator like `;`/`&&`/`||`. Splitting it
                    // surfaces every stage for approval; without this a denied executor
                    // (`... | iex`, `... | Remove-Item`) hides behind a benign first stage.
                    if (c == '|')
                    {
                        FlushCurrent(parts, current);
                        if (i + 1 < command.Length && command[i + 1] == '|')
                            i++; // consume the second '|' of a "||" operator
                        continue;
                    }
                }
            }

            current.Append(c);
        }

        FlushCurrent(parts, current);
        return parts;
    }

    // True when `segment` contains an execution-introducing construct the parser does not decompose,
    // for a shell that evaluates it: an unquoted `(` subexpression/subshell not part of a `$(` or
    // `@(` command substitution, or - for POSIX shells only - an unquoted backtick substitution. cmd
    // is exempt (no $()/backtick, and its `(...)` grouping chains via ; & | which are already split
    // - so `C:\Program Files (x86)` paths stay valid). Single/double quote state is tracked so a `(`
    // or backtick inside a quoted argument is treated as a literal and not flagged.
    private static bool HasUndecomposableExec(string segment, string? shell)
    {
        var s = (shell ?? "powershell").ToLowerInvariant();
        if (s is "cmd" or "cmd.exe")
            return false;
        var posix = s is "sh" or "bash" or "zsh" or "dash" or "ash" or "ksh" or "fish";

        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            if (c == '"' && !inSingle) { inDouble = !inDouble; continue; }
            if (c == '\'' && !inDouble) { inSingle = !inSingle; continue; }
            if (inSingle || inDouble) continue;

            if (c == '`' && posix)
                return true; // POSIX command substitution
            if (c == '(' && (i == 0 || (segment[i - 1] != '$' && segment[i - 1] != '@')))
                return true; // bare subexpression / subshell (not a decomposed `$(` or `@(`)
        }
        return false;
    }

    /// <summary>
    /// Extracts the inner command of every command-substitution / subexpression the shell will
    /// execute inside <paramref name="s"/> - <c>$(...)</c>, <c>@(...)</c>, and <c>`...`</c>
    /// (backticks). Spans inside single quotes are literal in both POSIX shells and PowerShell and
    /// are skipped; double-quoted and unquoted spans are surfaced. The paren forms are nesting- and
    /// quote-aware; nested substitutions are re-discovered when the extracted command is itself
    /// expanded.
    /// </summary>
    private static List<string> ExtractCommandSubstitutions(string s)
    {
        var found = new List<string>();
        var inSingleQuotes = false;
        var inDoubleQuotes = false;

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];

            if (c == '\'' && !inDoubleQuotes)
            {
                inSingleQuotes = !inSingleQuotes;
                continue;
            }
            if (c == '"' && !inSingleQuotes)
            {
                inDoubleQuotes = !inDoubleQuotes;
                continue;
            }
            if (inSingleQuotes)
                continue; // literal - the shell performs no expansion inside single quotes

            // $(...) / @(...) subexpression: find the matching ')' with nesting + quote awareness.
            if ((c == '$' || c == '@') && i + 1 < s.Length && s[i + 1] == '(')
            {
                var inner = ExtractBalancedParen(s, i + 1, out var closeIndex);
                if (inner != null)
                {
                    if (!string.IsNullOrWhiteSpace(inner))
                        found.Add(inner);
                    i = closeIndex; // resume after the matching ')'
                    continue;
                }
            }

            // `...` backtick command substitution (POSIX). Paired, no nesting.
            if (c == '`')
            {
                var close = s.IndexOf('`', i + 1);
                if (close > i)
                {
                    var inner = s.Substring(i + 1, close - i - 1);
                    if (!string.IsNullOrWhiteSpace(inner))
                        found.Add(inner);
                    i = close;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Given the index of an opening '(', returns the text strictly inside its matching ')'
    /// (respecting nested parens and quotes) and sets <paramref name="closeIndex"/> to that ')'.
    /// Returns null when the parenthesis is unbalanced.
    /// </summary>
    private static string? ExtractBalancedParen(string s, int openIndex, out int closeIndex)
    {
        closeIndex = openIndex;
        var depth = 0;
        var inSingleQuotes = false;
        var inDoubleQuotes = false;

        for (var i = openIndex; i < s.Length; i++)
        {
            var c = s[i];

            if (c == '\'' && !inDoubleQuotes)
            {
                inSingleQuotes = !inSingleQuotes;
                continue;
            }
            if (c == '"' && !inSingleQuotes)
            {
                inDoubleQuotes = !inDoubleQuotes;
                continue;
            }
            if (inSingleQuotes || inDoubleQuotes)
                continue;

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    closeIndex = i;
                    return s.Substring(openIndex + 1, i - openIndex - 1);
                }
            }
        }

        return null; // unbalanced - leave the span untouched
    }

    private static string[] Tokenize(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inSingleQuotes = false;
        var inDoubleQuotes = false;
        var escapeNext = false;

        foreach (var c in command)
        {
            if (escapeNext)
            {
                current.Append(c);
                escapeNext = false;
                continue;
            }

            if (c == '\\' && inDoubleQuotes)
            {
                escapeNext = true;
                continue;
            }

            if (c == '"' && !inSingleQuotes)
            {
                inDoubleQuotes = !inDoubleQuotes;
                continue;
            }

            if (c == '\'' && !inDoubleQuotes)
            {
                inSingleQuotes = !inSingleQuotes;
                continue;
            }

            if (!inSingleQuotes && !inDoubleQuotes && char.IsWhiteSpace(c))
            {
                FlushCurrent(tokens, current);
                continue;
            }

            current.Append(c);
        }

        FlushCurrent(tokens, current);
        return tokens.ToArray();
    }

    private static string TrimMatchingQuotes(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    private static string? NormalizeShell(string? shell) =>
        string.IsNullOrWhiteSpace(shell) ? "powershell" : shell.ToLowerInvariant();

    private static void FlushCurrent(List<string> parts, StringBuilder current)
    {
        if (current.Length == 0)
            return;

        parts.Add(current.ToString());
        current.Clear();
    }
}
