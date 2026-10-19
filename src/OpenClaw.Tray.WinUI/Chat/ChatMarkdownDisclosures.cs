using OpenClaw.Shared.Markdown;
using OpenClaw.Shared.Markdown.Md4c;

namespace OpenClawTray.Chat;

internal abstract record ChatMarkdownPart;
internal sealed record ChatMarkdownText(string Text) : ChatMarkdownPart;
internal sealed record ChatMarkdownDisclosure(
    string Summary, bool InitiallyExpanded, IReadOnlyList<ChatMarkdownPart> Body) : ChatMarkdownPart;

/// <summary>
/// Recognizes only block-level disclosure wrappers, not general HTML.
/// Unbalanced, unsupported, or still-streaming wrappers remain literal Markdown.
/// </summary>
internal static class ChatMarkdownDisclosures
{
    private const int MaxDepth = 16;
    private const int MaxSummaryLength = 4096;

    internal static IReadOnlyList<ChatMarkdownPart> Parse(string text)
    {
        if (text.Length > ChatMarkdownAstBuilder.MaxInputBytes ||
            !text.Contains("<details", StringComparison.OrdinalIgnoreCase))
            return [new ChatMarkdownText(text)];

        var tokens = Scan(text);
        var stack = new Stack<int>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Summary is not null)
                stack.Push(i);
            else if (stack.TryPop(out var opening))
                tokens[opening] = tokens[opening] with { Closing = i };
        }
        if (!tokens.Any(token => token.Closing >= 0))
            return [new ChatMarkdownText(text)];
        return Parts(text, tokens, 0, text.Length, 0);
    }

    private static IReadOnlyList<ChatMarkdownPart> Parts(
        string text, List<Token> tokens, int start, int end, int depth)
    {
        var parts = new List<ChatMarkdownPart>();
        var position = start;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Start < position || token.Start >= end || token.Summary is null)
                continue;
            // Do not turn nested fragments of an incomplete outer disclosure into controls.
            if (token.Closing < 0 || depth >= MaxDepth)
                break;
            var closing = tokens[token.Closing];
            if (closing.End > end)
                break;
            if (token.Summary.Length == 0)
            {
                // Unsupported nested wrappers stay opaque, including their closing tag.
                // They must not close an enclosing supported disclosure.
                i = token.Closing;
                continue;
            }

            AddText(token.Start);
            parts.Add(new ChatMarkdownDisclosure(
                token.Summary, token.Expanded,
                Parts(text, tokens, token.End, closing.Start, depth + 1)));
            position = closing.End;
            i = token.Closing;
        }
        AddText(end);
        return parts;

        void AddText(int until)
        {
            var markdown = text[position..until].Trim('\r', '\n');
            if (markdown.Length > 0)
                parts.Add(new ChatMarkdownText(markdown));
        }
    }

    private static List<Token> Scan(string text)
    {
        var tokens = new List<Token>();
        var eligible = WrapperPositions(text);
        // Unclosed openers. md4c reports an indented closer that follows list or quote text
        // as a lazy continuation, so such a closer is accepted only when it lines up with
        // the opener it would close. Openers that md4c places inside list or quote content are
        // tracked as Ignored so their own closers pair with them and stay literal.
        var openers = new Stack<(int Indent, bool Ignored)>();
        var position = 0;
        while (position < text.Length)
        {
            var newline = text.IndexOf('\n', position);
            var end = newline < 0 ? text.Length : newline + 1;
            var line = text.AsSpan(position, end - position).TrimEnd();
            var indent = 0;
            while (indent < line.Length && line[indent] == ' ')
                indent++;
            var content = line[indent..];
            var tag = position + indent;
            if (indent <= 3 && !content.IsEmpty)
            {
                if (content.Equals("</details>", StringComparison.OrdinalIgnoreCase) &&
                    eligible.Close.Contains(tag))
                {
                    if (eligible.Open.Contains(tag))
                    {
                        // A top-level closer ends any list-local openers still pending.
                        while (openers.Count > 0 && openers.Peek().Ignored)
                            openers.Pop();
                        tokens.Add(new Token(position, end, null, false));
                        openers.TryPop(out _);
                    }
                    else if (openers.Count > 0 && openers.Peek().Ignored)
                        openers.Pop();
                    else if (indent == 0 || (openers.Count > 0 && openers.Peek().Indent == indent))
                    {
                        tokens.Add(new Token(position, end, null, false));
                        openers.TryPop(out _);
                    }
                }
                else if (eligible.Open.Contains(tag) &&
                    TryOpening(text, tag, out var summary, out var expanded, out var bodyStart))
                {
                    tokens.Add(new Token(position, bodyStart, summary, expanded));
                    openers.Push((indent, false));
                    end = bodyStart;
                }
                else if (content.StartsWith("<details", StringComparison.OrdinalIgnoreCase) &&
                    content.Length > 8 && (content[8] == '>' || char.IsWhiteSpace(content[8])) &&
                    content.IndexOf('>') >= 0)
                {
                    if (eligible.Open.Contains(tag))
                    {
                        tokens.Add(new Token(position, end, string.Empty, false));
                        openers.Push((indent, false));
                    }
                    else if (eligible.Close.Contains(tag))
                        openers.Push((indent, true));
                }
            }
            else if (!content.IsEmpty)
            {
                TrackIgnoredWrappers(text, position, end, eligible, openers);
            }
            if (indent <= 3 && !content.IsEmpty && content[0] != '<')
                TrackIgnoredWrappers(text, position, end, eligible, openers);
            position = end;
        }
        return tokens;
    }

    // Wrappers after a list or quote marker are never disclosure tokens, but their pairs must
    // still be tracked so a list-local closer cannot close an enclosing disclosure.
    private static void TrackIgnoredWrappers(
        string text, int start, int end,
        (HashSet<int> Open, HashSet<int> Close) eligible,
        Stack<(int Indent, bool Ignored)> openers)
    {
        for (var i = text.IndexOf('<', start, end - start); i >= 0 && i < end;
            i = text.IndexOf('<', i + 1, end - i - 1))
        {
            if (!eligible.Close.Contains(i) || eligible.Open.Contains(i))
                continue;
            var tail = text.AsSpan(i, end - i);
            if (tail.StartsWith("</details>", StringComparison.OrdinalIgnoreCase))
            {
                if (openers.Count > 0 && openers.Peek().Ignored)
                    openers.Pop();
            }
            else if (tail.StartsWith("<details", StringComparison.OrdinalIgnoreCase) &&
                tail.Length > 8 && (tail[8] == '>' || char.IsWhiteSpace(tail[8])))
                openers.Push((0, true));
        }
    }

    private static (HashSet<int> Open, HashSet<int> Close) WrapperPositions(string text)
    {
        var openings = new HashSet<int>();
        var closings = new HashSet<int>();
        var blocks = new Stack<MarkdownBlockType>();
        // Let the existing Markdown parser decide whether a tag is prose,
        // code, a link label, or content inside a list/quote/table.
        var spans = 0;
        var result = Md4cParser.Parse(text, MarkdownParserFlags.Tables | MarkdownParserFlags.NoHtml,
            (type, _) => { blocks.Push(type); return 0; },
            (_, _) => { blocks.Pop(); return 0; },
            (_, _) => { spans++; return 0; },
            (_, _) => { spans--; return 0; },
            (type, content) =>
            {
                if (type != MarkdownTextType.Normal || spans != 0 ||
                    blocks.Contains(MarkdownBlockType.Code) ||
                    !text.AsSpan().Overlaps(content, out var offset))
                    return 0;
                // A line starting with "</details>" can only be quote text as a lazy
                // continuation (a quoted closer begins with ">"), so quotes may hold closers
                // but never openers.
                var quoted = blocks.Contains(MarkdownBlockType.Quote);
                for (var i = 0; i < content.Length; i++)
                    if (content[i] == '<')
                    {
                        closings.Add(offset + i);
                        if (!quoted && blocks.Count == 2 && blocks.Peek() == MarkdownBlockType.P)
                            openings.Add(offset + i);
                    }
                return 0;
            });
        if (result != 0)
        {
            openings.Clear();
            closings.Clear();
        }
        return (openings, closings);
    }

    private static bool TryOpening(
        string text, int start, out string summary, out bool expanded, out int bodyStart)
    {
        summary = string.Empty;
        expanded = false;
        bodyStart = start;
        var tail = text.AsSpan(start);
        int after;
        if (tail.StartsWith("<details>", StringComparison.OrdinalIgnoreCase))
            after = start + "<details>".Length;
        else if (tail.StartsWith("<details open>", StringComparison.OrdinalIgnoreCase))
        {
            expanded = true;
            after = start + "<details open>".Length;
        }
        else
            return false;

        // Summary may follow the opener on the same line or the next nonblank line.
        while (after < text.Length && char.IsWhiteSpace(text[after]))
            after++;
        if (!text.AsSpan(after).StartsWith("<summary>", StringComparison.OrdinalIgnoreCase))
            return false;
        var summaryStart = after + "<summary>".Length;
        var close = text.IndexOf("</summary>", summaryStart, StringComparison.OrdinalIgnoreCase);
        if (close < 0 || close - summaryStart > MaxSummaryLength)
            return false;

        after = close + "</summary>".Length;
        var newline = text.IndexOf('\n', after);
        var lineEnd = newline < 0 ? text.Length : newline;
        if (!text.AsSpan(after, lineEnd - after).Trim().IsEmpty)
            return false;

        summary = text[summaryStart..close].Trim();
        if (summary.Length == 0)
            return false;
        bodyStart = newline < 0 ? text.Length : newline + 1;
        return true;
    }

    private sealed record Token(
        int Start, int End, string? Summary, bool Expanded, int Closing = -1);
}
