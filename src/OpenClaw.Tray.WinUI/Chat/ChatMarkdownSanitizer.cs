using System.Text;

namespace OpenClawTray.Chat;

/// <summary>
/// Conservative pre-rendering sanitizer for chat-bubble Markdown.
/// Removes constructs that would otherwise trigger an outbound URL
/// fetch or a click-to-navigate hyperlink:
/// <list type="bullet">
///   <item>Inline images <c>![alt](src)</c> render as <c>[Image: alt]</c>
///         text (no Uri instantiation, no <c>BitmapImage</c> fetch).</item>
///   <item>Inline links <c>[text](url)</c> render as plain
///         <c>text (url)</c> with the URL visible but inert.</item>
///   <item>Reference link definitions <c>[ref]: url</c> render as
///         plain <c>ref: url</c> so any usage <c>[text][ref]</c>
///         degrades to literal text.</item>
/// </list>
/// Code spans (<c>`code`</c>) and fenced code blocks (<c>```...```</c>
/// or <c>~~~...~~~</c>) are preserved verbatim. Idempotent and safe on
/// null / empty input.
/// </summary>
/// <remarks>
/// SECURITY (chat-rubber-duck HIGH 1 / MEDIUM 3): the gateway, a
/// compromised tool, or a prompt-injected model can place arbitrary
/// Markdown in assistant text. Default rendering would (a) auto-fetch
/// <c>http(s)://</c> images into <c>BitmapImage</c> / <c>SvgImageSource</c>
/// (SSRF, tracking pixels, beacon attacks) and (b) attach
/// click-to-navigate hyperlinks that look like trusted assistant prose.
/// Rendering URL-bearing constructs as inert text breaks both vectors at
/// the source before any renderer can activate links or remote media.
/// </remarks>
internal static class ChatMarkdownSanitizer
{
    public readonly record struct TextSegment(string Text, bool IsStrong);

    /// <summary>
    /// Flatten a parsed Markdown link's display text + destination URI
    /// into a single inert plain-text string. Used by the
    /// <c>OpenClawChatTimeline</c> rendering path so
    /// that links the parser DOES emit (bare URLs, autolinks
    /// <c>&lt;https://…&gt;</c>) collapse to non-clickable text instead
    /// of <see cref="System.Windows.Documents.Hyperlink"/>-style runs.
    /// Pure function — kept here so it can be unit-tested without a
    /// dependency on the WinUI project.
    /// </summary>
    /// <remarks>
    /// Output rules:
    /// <list type="bullet">
    ///   <item>Empty display + URI → URI text (autolink case).</item>
    ///   <item>Display equals URI → display only (avoid duplicate
    ///         "<c>https://x (https://x)</c>" rendering).</item>
    ///   <item>Otherwise → "<c>{display} ({uri})</c>" so the navigation
    ///         target stays visible and inspectable.</item>
    /// </list>
    /// </remarks>
    public static string FlattenLinkToInertText(string? displayText, string? uriText)
    {
        var d = displayText ?? string.Empty;
        var u = uriText ?? string.Empty;
        if (string.IsNullOrEmpty(d)) return u;
        if (string.Equals(d, u, StringComparison.Ordinal)) return d;
        return string.IsNullOrEmpty(u) ? d : $"{d} ({u})";
    }

    /// <summary>
    /// Convert a raw Markdown HTML block into inert display text. The caller
    /// must render the returned string with a plain text control, not an HTML
    /// or WebView renderer.
    /// </summary>
    public static string FlattenRawHtmlBlockToInertText(string? rawHtml) => rawHtml ?? string.Empty;

    /// <summary>
    /// Sanitize a chat-bubble markdown string.
    /// </summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        if (text.IndexOf('[') < 0 && text.IndexOf('!') < 0) return text;

        var sb = new StringBuilder(text.Length);
        int i = 0;
        int n = text.Length;
        bool atLineStart = true;
        while (i < n)
        {
            char c = text[i];

            // Indented code block: line begins with a tab or 4+ spaces.
            // CommonMark treats such lines as code content; pass them
            // through verbatim so link-like syntax inside them is never
            // mutated by the link / image / ref-def branches below.
            if (atLineStart && IsIndentedCodeStart(text, i, n))
            {
                int lineEnd = text.IndexOf('\n', i);
                int segEnd = lineEnd < 0 ? n : lineEnd + 1;
                sb.Append(text, i, segEnd - i);
                i = segEnd;
                atLineStart = true;
                continue;
            }

            // Fenced code block: ``` or ~~~ at line start, optionally
            // preceded by up to 3 spaces (per CommonMark §4.5). Pass
            // through to matching closing fence (or EOF). The closing
            // fence must use the same character and may also have up to
            // 3 leading spaces; we accept any leading whitespace on
            // close to stay conservative.
            if (atLineStart && TryReadFenceOpen(text, i, n, out var fenceChar, out int fenceLen, out int afterFenceLine))
            {
                sb.Append(text, i, afterFenceLine - i);
                i = afterFenceLine;
                while (i < n)
                {
                    int nextLine = text.IndexOf('\n', i);
                    int segEnd = nextLine < 0 ? n : nextLine + 1;
                    sb.Append(text, i, segEnd - i);
                    var line = text.AsSpan(i, segEnd - i);
                    i = segEnd;
                    var trimmed = line.TrimStart();
                    if (trimmed.Length >= fenceLen)
                    {
                        bool allFence = true;
                        for (int k = 0; k < fenceLen; k++)
                        {
                            if (trimmed[k] != fenceChar) { allFence = false; break; }
                        }
                        if (allFence) break;
                    }
                }
                atLineStart = true;
                continue;
            }

            // Inline code: backtick run; pass through up to matching run of
            // the same length (or EOF).
            if (c == '`')
            {
                int runLen = 0;
                while (i + runLen < n && text[i + runLen] == '`') runLen++;
                int searchFrom = i + runLen;
                int closeAt = -1;
                while (searchFrom < n)
                {
                    int found = text.IndexOf('`', searchFrom);
                    if (found < 0) break;
                    int foundLen = 0;
                    while (found + foundLen < n && text[found + foundLen] == '`') foundLen++;
                    if (foundLen == runLen) { closeAt = found; break; }
                    searchFrom = found + foundLen;
                }
                if (closeAt < 0)
                {
                    sb.Append(text, i, n - i);
                    i = n;
                    atLineStart = false;
                    continue;
                }
                int end = closeAt + runLen;
                sb.Append(text, i, end - i);
                i = end;
                atLineStart = false;
                continue;
            }

            // Reference link definition at line start: ``[ref]: url ...``.
            // CommonMark also allows up to 3 leading spaces before the
            // definition; 4+ spaces are handled above as indented code.
            if (atLineStart && TryParseReferenceDefinition(text, i, n, out var refLabel, out var refUrl, out var refEnd))
            {
                sb.Append(refLabel);
                sb.Append(": ");
                sb.Append(refUrl);
                i = refEnd;
                continue;
            }

            // Inline image: ![alt](src) → [Image: alt]
            if (c == '!' && i + 1 < n && text[i + 1] == '[')
            {
                if (TryParseLinkOrImage(text, i + 1, out _, out var alt, out _, out var afterParen, out var exceededScanLimit))
                {
                    sb.Append("[Image");
                    if (!string.IsNullOrEmpty(alt))
                    {
                        // Recursively sanitize alt text — the gateway can
                        // place link / image syntax inside an alt and
                        // we must not re-emit it verbatim.
                        sb.Append(": ");
                        sb.Append(Sanitize(alt));
                    }
                    sb.Append(']');
                    i = afterParen;
                    atLineStart = false;
                    continue;
                }

                if (exceededScanLimit)
                {
                    sb.Append(@"\!\[");
                    i += 2;
                    atLineStart = false;
                    continue;
                }
            }

            // Inline link: [text](url) → text (url)
            if (c == '[')
            {
                if (TryParseLinkOrImage(text, i, out _, out var linkText, out var url, out var afterParen, out var exceededScanLimit))
                {
                    // Recursively sanitize the link text so a nested
                    // image / link inside the brackets (e.g.
                    // ``[![alt](http://img)](http://other)``) is also
                    // flattened. Without this the inner ``![alt](src)``
                    // syntax would be preserved verbatim and re-parsed
                    // by a markdown renderer as a real image fetch.
                    sb.Append(Sanitize(linkText));
                    if (!string.IsNullOrEmpty(url))
                    {
                        sb.Append(" (");
                        sb.Append(url);
                        sb.Append(')');
                    }
                    i = afterParen;
                    atLineStart = false;
                    continue;
                }

                if (exceededScanLimit)
                {
                    sb.Append(@"\[");
                    i++;
                    atLineStart = false;
                    continue;
                }
            }

            sb.Append(c);
            atLineStart = c == '\n';
            i++;
        }

        return sb.ToString();
    }

    public static IReadOnlyList<TextSegment> SanitizeAndSplitStrongEmphasis(string? text) =>
        SplitStrongEmphasis(Sanitize(text));

    private static IReadOnlyList<TextSegment> SplitStrongEmphasis(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<TextSegment>();

        var segments = new List<TextSegment>();
        var normal = new StringBuilder(text.Length);
        int i = 0;
        int n = text.Length;
        bool atLineStart = true;

        void FlushNormal()
        {
            if (normal.Length == 0)
                return;

            segments.Add(new TextSegment(normal.ToString(), IsStrong: false));
            normal.Clear();
        }

        while (i < n)
        {
            if (atLineStart && TryReadFenceOpen(text, i, n, out var fenceChar, out int fenceLen, out int afterFenceLine))
            {
                AppendFencedBlock(text, normal, ref i, n, fenceChar, fenceLen, afterFenceLine);
                atLineStart = true;
                continue;
            }

            if (text[i] == '`')
            {
                AppendInlineCode(text, normal, ref i, n);
                atLineStart = false;
                continue;
            }

            if (i + 1 < n && text[i] == '*' && text[i + 1] == '*' && !IsEscaped(text, i))
            {
                var close = FindStrongClose(text, i + 2);
                if (close > i + 2)
                {
                    FlushNormal();
                    segments.Add(new TextSegment(text.Substring(i + 2, close - i - 2), IsStrong: true));
                    i = close + 2;
                    atLineStart = false;
                    continue;
                }
            }

            normal.Append(text[i]);
            atLineStart = text[i] == '\n';
            i++;
        }

        FlushNormal();
        return segments;
    }

    private static void AppendFencedBlock(string text, StringBuilder output, ref int i, int n, char fenceChar, int fenceLen, int afterFenceLine)
    {
        output.Append(text, i, afterFenceLine - i);
        i = afterFenceLine;

        while (i < n)
        {
            int nextLine = text.IndexOf('\n', i);
            int segEnd = nextLine < 0 ? n : nextLine + 1;
            output.Append(text, i, segEnd - i);
            var line = text.AsSpan(i, segEnd - i);
            i = segEnd;

            var trimmed = line.TrimStart();
            if (trimmed.Length < fenceLen)
                continue;

            if (trimmed[0] != fenceChar)
                continue;

            bool allFence = true;
            for (int k = 0; k < fenceLen; k++)
            {
                if (trimmed[k] != fenceChar) { allFence = false; break; }
            }

            if (allFence)
                break;
        }
    }

    private static void AppendInlineCode(string text, StringBuilder output, ref int i, int n)
    {
        int runLen = 0;
        while (i + runLen < n && text[i + runLen] == '`') runLen++;

        int searchFrom = i + runLen;
        while (searchFrom < n)
        {
            int found = text.IndexOf('`', searchFrom);
            if (found < 0)
                break;

            int foundLen = 0;
            while (found + foundLen < n && text[found + foundLen] == '`') foundLen++;
            if (foundLen == runLen)
            {
                var end = found + foundLen;
                output.Append(text, i, end - i);
                i = end;
                return;
            }

            searchFrom = found + foundLen;
        }

        output.Append(text, i, n - i);
        i = n;
    }

    private static int FindStrongClose(string text, int start)
    {
        var i = start;
        while (i + 1 < text.Length)
        {
            if (text[i] == '`')
            {
                var previous = i;
                AppendInlineCode(text, new StringBuilder(), ref i, text.Length);
                if (i == previous)
                    i++;
                continue;
            }

            if (text[i] == '*' && text[i + 1] == '*' && !IsEscaped(text, i))
                return i;

            i++;
        }

        return -1;
    }

    private static bool IsEscaped(string text, int index)
    {
        var slashCount = 0;
        for (var i = index - 1; i >= 0 && text[i] == '\\'; i--)
            slashCount++;

        return slashCount % 2 == 1;
    }

    private static bool TryParseReferenceDefinition(
        string text, int lineStart, int n,
        out string label, out string url, out int definitionEnd)
    {
        label = string.Empty;
        url = string.Empty;
        definitionEnd = lineStart;

        int p = lineStart;
        int leadingSpaces = 0;
        while (p < n && text[p] == ' ' && leadingSpaces < 3)
        {
            p++;
            leadingSpaces++;
        }

        if (p >= n || text[p] != '[') return false;

        int lineEnd = text.IndexOf('\n', p);
        int segEnd = lineEnd < 0 ? n : lineEnd;
        int close = text.IndexOf(']', p + 1);
        if (close <= p || close >= segEnd || close + 1 >= n || text[close + 1] != ':')
            return false;

        label = text.Substring(p + 1, close - p - 1);
        url = text.Substring(close + 2, segEnd - (close + 2)).TrimStart();
        definitionEnd = segEnd;
        return true;
    }

    // Detect a CommonMark indented code block: a line that begins with
    // a tab OR 4+ spaces. Blank lines (within an existing code block
    // context) are not handled here — but the conservative
    // single-line-at-a-time copy is fine because a non-indented line
    // simply re-enters the normal scan path.
    private static bool IsIndentedCodeStart(string text, int i, int n)
    {
        if (i >= n) return false;
        if (text[i] == '\t') return true;
        // 4 spaces.
        if (i + 3 < n &&
            text[i] == ' ' && text[i + 1] == ' ' && text[i + 2] == ' ' && text[i + 3] == ' ')
        {
            return true;
        }
        return false;
    }

    // Detect a CommonMark fenced code block opening at position ``i``.
    // Allows up to 3 leading spaces of indentation (per spec §4.5) and
    // a fence run of 3+ backticks or 3+ tildes. Returns the fence char,
    // the run length, and the byte offset of the start of the next line
    // (or end-of-input if no newline).
    private static bool TryReadFenceOpen(
        string text, int i, int n,
        out char fenceChar, out int fenceLen, out int afterFenceLine)
    {
        fenceChar = '\0';
        fenceLen = 0;
        afterFenceLine = i;

        int p = i;
        int leadingSpaces = 0;
        while (p < n && text[p] == ' ' && leadingSpaces < 3)
        {
            p++;
            leadingSpaces++;
        }
        if (p >= n) return false;
        char ch = text[p];
        if (ch != '`' && ch != '~') return false;
        int runStart = p;
        while (p < n && text[p] == ch) p++;
        int run = p - runStart;
        if (run < 3) return false;

        fenceChar = ch;
        fenceLen = run;
        int lineEnd = text.IndexOf('\n', p);
        afterFenceLine = lineEnd < 0 ? n : lineEnd + 1;
        return true;
    }

    // Parse ``[text](url)`` starting at the ``[`` index. Returns false
    // if the construct doesn't fully match within reasonable bounds.
    private static bool TryParseLinkOrImage(
        string text, int bracketStart,
        out int closeBracket, out string innerText, out string url, out int afterParen,
        out bool exceededScanLimit)
    {
        closeBracket = -1;
        innerText = string.Empty;
        url = string.Empty;
        afterParen = -1;
        exceededScanLimit = false;

        int n = text.Length;
        if (bracketStart >= n || text[bracketStart] != '[') return false;

        int depth = 1;
        int j = bracketStart + 1;
        int scanLimit = Math.Min(n, bracketStart + 1024);
        while (j < scanLimit)
        {
            char ch = text[j];
            if (ch == '\\' && j + 1 < scanLimit) { j += 2; continue; }
            if (ch == '\n' && j > bracketStart + 1 && text[j - 1] == '\n') return false;
            if (ch == '[') depth++;
            else if (ch == ']') { depth--; if (depth == 0) { closeBracket = j; break; } }
            j++;
        }
        if (closeBracket < 0)
        {
            exceededScanLimit = scanLimit < n;
            return false;
        }
        if (closeBracket + 1 >= n || text[closeBracket + 1] != '(') return false;

        int parenStart = closeBracket + 2;
        int parenEnd = -1;
        int parenScanLimit = Math.Min(n, parenStart + 2048);
        for (int k = parenStart; k < parenScanLimit; k++)
        {
            char ch = text[k];
            if (ch == ')') { parenEnd = k; break; }
            if (ch == '\n') return false;
        }
        if (parenEnd < 0)
        {
            exceededScanLimit = parenScanLimit < n;
            return false;
        }

        innerText = text.Substring(bracketStart + 1, closeBracket - bracketStart - 1);
        var rawUrl = text.Substring(parenStart, parenEnd - parenStart).Trim();
        // Strip optional title: ``url "title"`` or ``url 'title'``.
        int sp = rawUrl.IndexOf(' ');
        url = sp > 0 ? rawUrl[..sp] : rawUrl;
        // Drop angle brackets if present: ``<url>``.
        if (url.Length >= 2 && url[0] == '<' && url[^1] == '>') url = url[1..^1];
        afterParen = parenEnd + 1;
        return true;
    }
}
