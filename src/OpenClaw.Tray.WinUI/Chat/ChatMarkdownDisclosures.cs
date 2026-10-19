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
        // Unclosed openers in document order. md4c reports an indented closer that follows
        // list or quote text as a lazy continuation, so a closer outside a top-level paragraph
        // is accepted only when it lines up with the opener it would close. Openers that md4c
        // places inside list or quote content are tracked as Ignored: their own closers pair
        // with them and stay literal instead of closing an enclosing disclosure.
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
            var tagOffset = SkipContainerMarkers(line, out var hasMarkers);
            var tag = position + tagOffset;
            var content = line[tagOffset..];
            // Only a line that begins with a wrapper (after any list or quote markers) can
            // open or close one; mentions inside prose are never tracked.
            var topLevelShape = !hasMarkers && indent <= 3;

            if (content.Equals("</details>", StringComparison.OrdinalIgnoreCase) &&
                eligible.Close.Contains(tag))
            {
                if (topLevelShape && eligible.Open.Contains(tag))
                {
                    // A closer that md4c reads as a top-level paragraph ends any
                    // list-local openers still pending.
                    while (openers.Count > 0 && openers.Peek().Ignored)
                        openers.Pop();
                    tokens.Add(new Token(position, end, null, false));
                    openers.TryPop(out _);
                }
                else if (openers.Count > 0 && openers.Peek().Ignored)
                    openers.Pop();
                else if (topLevelShape && (indent == 0 || (openers.Count > 0 && openers.Peek().Indent == indent)))
                {
                    tokens.Add(new Token(position, end, null, false));
                    openers.TryPop(out _);
                }
            }
            else if (IsOpeningTagStart(content) && eligible.Close.Contains(tag))
            {
                if (topLevelShape && eligible.Open.Contains(tag))
                {
                    if (TryOpening(text, tag, eligible.Close, out var summary, out var expanded, out var bodyStart))
                    {
                        tokens.Add(new Token(position, bodyStart, summary, expanded));
                        end = bodyStart;
                    }
                    else
                        tokens.Add(new Token(position, end, string.Empty, false));
                    openers.Push((indent, false));
                }
                else if (!HasInlineClosers(text, tag, position + line.Length, eligible.Close))
                    openers.Push((indent, true));
            }
            position = end;
        }
        return tokens;
    }

    private static bool IsOpeningTagStart(ReadOnlySpan<char> content) =>
        content.StartsWith("<details", StringComparison.OrdinalIgnoreCase) &&
        content.Length > 8 && (content[8] == '>' || char.IsWhiteSpace(content[8])) &&
        content.IndexOf('>') >= 0;

    // A list-local opener whose closer is on the same line is balanced and needs no tracking.
    private static bool HasInlineClosers(string text, int from, int lineEnd, HashSet<int> closable)
    {
        for (var i = text.IndexOf("</details>", from, lineEnd - from, StringComparison.OrdinalIgnoreCase);
            i >= 0; i = text.IndexOf("</details>", i + 1, lineEnd - i - 1, StringComparison.OrdinalIgnoreCase))
        {
            if (closable.Contains(i))
                return true;
        }
        return false;
    }

    // Returns the offset of the first character after any spaces, quote markers, and list
    // markers that precede the line's content.
    private static int SkipContainerMarkers(ReadOnlySpan<char> line, out bool hasMarkers)
    {
        hasMarkers = false;
        var offset = 0;
        while (true)
        {
            while (offset < line.Length && line[offset] == ' ')
                offset++;
            var marker = MarkerLength(line[offset..]);
            if (marker == 0)
                return offset;
            hasMarkers = true;
            offset += marker;
        }
    }

    private static int MarkerLength(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
            return 0;
        if (text[0] == '>')
            return 1;
        if (text[0] is '-' or '+' or '*')
            return text.Length > 1 && text[1] == ' ' ? 1 : 0;
        var digits = 0;
        while (digits < text.Length && digits < 9 && char.IsAsciiDigit(text[digits]))
            digits++;
        return digits > 0 && digits + 1 < text.Length &&
            text[digits] is '.' or ')' && text[digits + 1] == ' ' ? digits + 1 : 0;
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
        string text, int start, HashSet<int> closable, out string summary, out bool expanded, out int bodyStart)
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
        // The summary must be prose, not an indented or fenced code block.
        if (!closable.Contains(after) ||
            !text.AsSpan(after).StartsWith("<summary>", StringComparison.OrdinalIgnoreCase))
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
