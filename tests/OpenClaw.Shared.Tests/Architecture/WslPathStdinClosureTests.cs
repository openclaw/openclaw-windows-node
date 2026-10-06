using System.Text.RegularExpressions;
using Xunit;

namespace OpenClaw.Shared.Tests.Architecture;

/// <summary>
/// wsl.exe expands <c>$PATH</c> in argv before bash starts, so a PATH prefix passed to
/// <c>bash -c</c> becomes the Windows PATH. <c>RunInWslAsync(..., inputViaStdin: true)</c>
/// pipes the script to <c>bash -s</c> instead. This scan fails when a new production
/// call passes a PATH-bearing script on the argv path.
/// </summary>
public sealed class WslPathStdinClosureTests
{
    private static readonly Regex PathMarker = new(
        @"\$\{?PATH\b|WslPathPrefix|OpenClawWslPathPrefix|GetPathPrefix|\.PathPrefix\b",
        RegexOptions.CultureInvariant);

    private static readonly Regex Identifier = new(
        @"\b[A-Za-z_][A-Za-z0-9_]*\b",
        RegexOptions.CultureInvariant);

    private static readonly HashSet<string> IgnoredIdentifiers = new(StringComparer.Ordinal)
    {
        "var", "string", "true", "false", "null", "new", "await", "return", "if", "else",
        "set", "get", "echo", "openclaw", "config", "gateway", "and", "or", "not",
        "Task", "CommandResult", "TimeSpan", "CancellationToken", "FromSeconds",
    };

    [Fact]
    public void PathBearingRunInWslScripts_UseStdinNotArgv()
    {
        var failures = new List<string>();
        foreach (var file in ProductionSourceFiles.All)
        {
            foreach (var call in FindInvocations(file.Text, "RunInWslAsync"))
            {
                if (IsMethodDeclaration(file.Text, call.Index))
                    continue;
                if (HasNamedTrue(call.Args, "inputViaStdin"))
                    continue;
                if (!CommandIsPathBearing(file.Text, call.Index, call.Args))
                    continue;

                var line = LineOf(file.Text, call.Index);
                failures.Add($"{RelativePath(file.Path)}:{line}");
            }
        }

        Assert.True(
            failures.Count == 0,
            "PATH-bearing WSL scripts must use RunInWslAsync(..., inputViaStdin: true) " +
            "so wsl.exe does not expand $PATH on argv. See docs/WSL_EXE_ARGV_PITFALL.md. " +
            string.Join(", ", failures));
    }

    private static bool CommandIsPathBearing(string text, int callIndex, string args)
    {
        var command = ExtractCommandArgument(args);
        return command is not null && ExpressionIsPathBearing(text, callIndex, command, depth: 0);
    }

    private static bool ExpressionIsPathBearing(string text, int beforeIndex, string expression, int depth)
    {
        if (PathMarker.IsMatch(expression))
            return true;
        if (depth >= 6)
            return false;

        foreach (Match match in Identifier.Matches(expression))
        {
            var name = match.Value;
            if (IgnoredIdentifiers.Contains(name) || PathMarker.IsMatch(name))
                continue;

            var assigned = NearestAssignment(text, beforeIndex, name);
            if (assigned is null)
                continue;
            if (ExpressionIsPathBearing(text, assigned.Value.At, assigned.Value.Expr, depth + 1))
                return true;
        }

        return false;
    }

    private static (int At, string Expr)? NearestAssignment(string text, int beforeIndex, string name)
    {
        var pattern = new Regex(@"\b" + Regex.Escape(name) + @"\s*=(?!=)", RegexOptions.CultureInvariant);
        (int At, string Expr)? nearest = null;
        foreach (Match match in pattern.Matches(text))
        {
            if (match.Index >= beforeIndex)
                break;
            if (match.Index > 0 && "=!<>".Contains(text[match.Index - 1]))
                continue;

            var exprStart = match.Index + match.Length;
            var expr = ReadExpression(text, exprStart);
            if (expr is null)
                continue;
            nearest = (exprStart, expr);
        }

        return nearest;
    }

    private static string? ExtractCommandArgument(string args)
    {
        var parts = SplitTopLevel(args, ',');
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("command:", StringComparison.Ordinal))
                return trimmed["command:".Length..];
        }

        return parts.Count > 1 ? parts[1] : null;
    }

    private static bool HasNamedTrue(string args, string name)
    {
        foreach (var part in SplitTopLevel(args, ','))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith(name + ":", StringComparison.Ordinal) &&
                trimmed.EndsWith("true", StringComparison.Ordinal) &&
                Regex.IsMatch(trimmed, @"^" + Regex.Escape(name) + @"\s*:\s*true$"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMethodDeclaration(string text, int index)
    {
        var start = Math.Max(0, index - 80);
        var prefix = text[start..index];
        return prefix.Contains("Task<CommandResult>", StringComparison.Ordinal) ||
               prefix.Contains("Task<OpenClaw.SetupEngine.CommandResult>", StringComparison.Ordinal);
    }

    private readonly record struct Invocation(int Index, string Args);

    private static List<Invocation> FindInvocations(string text, string name)
    {
        var results = new List<Invocation>();
        var i = 0;
        while (i < text.Length)
        {
            if (TryConsumeStringOrComment(text, ref i))
                continue;

            if (IsIdentifierAt(text, i, name))
            {
                var after = i + name.Length;
                var open = IndexOfOpenParen(text, after);
                if (open >= 0)
                {
                    var close = FindMatchingParen(text, open);
                    if (close > open)
                    {
                        results.Add(new Invocation(i, text[(open + 1)..close]));
                        i = close + 1;
                        continue;
                    }
                }
            }

            i++;
        }

        return results;
    }

    private static int IndexOfOpenParen(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
                continue;
            return text[i] == '(' ? i : -1;
        }

        return -1;
    }

    private static int FindMatchingParen(string text, int open)
    {
        var depth = 0;
        var i = open;
        while (i < text.Length)
        {
            if (TryConsumeStringOrComment(text, ref i))
                continue;
            if (text[i] == '(')
                depth++;
            else if (text[i] == ')')
            {
                depth--;
                if (depth == 0)
                    return i;
            }

            i++;
        }

        return -1;
    }

    private static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        var start = 0;
        var depth = 0;
        var i = 0;
        while (i < text.Length)
        {
            if (TryConsumeStringOrComment(text, ref i))
                continue;
            var ch = text[i];
            if (ch is '(' or '[' or '{')
                depth++;
            else if (ch is ')' or ']' or '}')
                depth--;
            else if (ch == separator && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }

            i++;
        }

        parts.Add(text[start..]);
        return parts;
    }

    private static string? ReadExpression(string text, int start)
    {
        var depth = 0;
        var i = start;
        while (i < text.Length)
        {
            if (TryConsumeStringOrComment(text, ref i))
                continue;
            var ch = text[i];
            if (ch is '(' or '[' or '{')
                depth++;
            else if (ch is ')' or ']' or '}')
                depth--;
            else if (ch == ';' && depth == 0)
                return text[start..i];

            i++;
        }

        return null;
    }

    private static bool TryConsumeStringOrComment(string text, ref int i)
    {
        if (i >= text.Length)
            return false;

        if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/')
        {
            i += 2;
            while (i < text.Length && text[i] != '\n')
                i++;
            return true;
        }

        if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
        {
            i += 2;
            while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                i++;
            i = Math.Min(text.Length, i + 2);
            return true;
        }

        if (text[i] == '\'')
        {
            i++;
            if (i < text.Length && text[i] == '\\')
                i++;
            if (i < text.Length)
                i++;
            if (i < text.Length && text[i] == '\'')
                i++;
            return true;
        }

        var dollarCount = 0;
        var cursor = i;
        while (cursor < text.Length && text[cursor] == '$')
        {
            dollarCount++;
            cursor++;
        }

        var verbatim = cursor < text.Length && text[cursor] == '@';
        if (verbatim)
            cursor++;

        if (cursor >= text.Length || text[cursor] != '"')
            return false;
        if (dollarCount == 0 && !verbatim && cursor != i)
            return false;

        var quoteStart = cursor;
        var rawQuotes = CountRawQuotes(text, quoteStart);
        if (rawQuotes >= 3)
        {
            i = SkipRawString(text, quoteStart + rawQuotes, rawQuotes, dollarCount);
            return true;
        }

        if (dollarCount > 1 && !verbatim)
            return false;

        i = SkipQuotedString(text, quoteStart + 1, verbatim, dollarCount > 0);
        return true;
    }

    private static int CountRawQuotes(string text, int start)
    {
        var count = 0;
        while (start + count < text.Length && text[start + count] == '"')
            count++;
        return count;
    }

    private static int SkipRawString(string text, int contentStart, int quoteCount, int dollarCount)
    {
        var i = contentStart;
        while (i < text.Length)
        {
            if (dollarCount > 0 && text[i] == '{')
            {
                var braces = CountChar(text, i, '{');
                if (braces > dollarCount)
                {
                    i += braces;
                    continue;
                }

                if (braces == dollarCount)
                {
                    i += braces;
                    var depth = 1;
                    while (i < text.Length && depth > 0)
                    {
                        if (TryConsumeStringOrComment(text, ref i))
                            continue;
                        if (text[i] == '{')
                            depth++;
                        else if (text[i] == '}')
                            depth--;
                        if (depth > 0)
                            i++;
                    }

                    if (i < text.Length && text[i] == '}')
                        i++;
                    continue;
                }
            }

            if (text[i] == '"')
            {
                var run = CountRawQuotes(text, i);
                if (run >= quoteCount)
                    return i + quoteCount;
            }

            i++;
        }

        return text.Length;
    }

    private static int SkipQuotedString(string text, int start, bool verbatim, bool interpolated)
    {
        var i = start;
        while (i < text.Length)
        {
            if (verbatim && text[i] == '"')
            {
                if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            if (!verbatim && text[i] == '\\')
            {
                i += Math.Min(2, text.Length - i);
                continue;
            }

            if (interpolated && text[i] == '{')
            {
                if (i + 1 < text.Length && text[i + 1] == '{')
                {
                    i += 2;
                    continue;
                }

                i++;
                var depth = 1;
                while (i < text.Length && depth > 0)
                {
                    if (TryConsumeStringOrComment(text, ref i))
                        continue;
                    if (text[i] == '{')
                        depth++;
                    else if (text[i] == '}')
                        depth--;
                    if (depth > 0)
                        i++;
                }

                if (i < text.Length && text[i] == '}')
                    i++;
                continue;
            }

            if (text[i] == '"')
                return i + 1;

            i++;
        }

        return text.Length;
    }

    private static int CountChar(string text, int start, char value)
    {
        var count = 0;
        while (start + count < text.Length && text[start + count] == value)
            count++;
        return count;
    }

    private static bool IsIdentifierAt(string text, int index, string name)
    {
        if (index < 0 || index + name.Length > text.Length)
            return false;
        if (!text.AsSpan(index, name.Length).SequenceEqual(name))
            return false;
        if (index > 0 && IsIdentifierChar(text[index - 1]))
            return false;
        var end = index + name.Length;
        return end >= text.Length || !IsIdentifierChar(text[end]);
    }

    private static bool IsIdentifierChar(char value) =>
        char.IsLetterOrDigit(value) || value == '_';

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
                line++;
        }

        return line;
    }

    private static string RelativePath(string path)
    {
        var marker = $"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}";
        var at = path.LastIndexOf(marker, StringComparison.Ordinal);
        return at >= 0 ? path[(at + 1)..].Replace(Path.DirectorySeparatorChar, '/') : path;
    }
}
