using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared.Markdown;

/// <summary>
/// Projects a chat Markdown message into plain text that a text-to-speech
/// engine reads with natural pacing.
///
/// <para>
/// The projector renders the same AST the chat bubble renders
/// (<see cref="ChatMarkdownAstBuilder"/>), so speech and display agree on what
/// is a heading, list item, table cell, code span or link. It is the single
/// owner of "Markdown in, speakable text out": callers hand it the raw
/// message and pass the result straight to the TTS engine.
/// </para>
///
/// <para>Rules:</para>
/// <list type="bullet">
///   <item>Every heading, paragraph, list item and table row ends with a
///         sentence terminator, so the engine pauses between blocks. Existing
///         terminal punctuation is never doubled; CJK blocks use <c>。</c>.</item>
///   <item>User punctuation is otherwise preserved. Only gaps the projector
///         created (by dropping a link, code span or URL) are tidied, and never
///         before a dot that starts a word, so <c>.NET</c> and <c>./build</c>
///         survive.</item>
///   <item>No URL is ever spoken: autolinks are dropped by the builder, and
///         URL-shaped tokens are removed from every prose and code run, because
///         md4c does not autolink ports, <c>localhost</c>, <c>ws://</c>,
///         <c>www.</c> or e-mail addresses reliably.</item>
///   <item>Inline code that looks like a credential assignment (<c>token=…</c>,
///         <c>api_key: …</c>, a bearer header) is dropped whole, as the previous
///         auto-read path dropped code spans. It must never reach a cloud
///         provider.</item>
///   <item>Fenced code blocks are silent but still end a sentence. Other inline
///         code is spoken, with <c>_</c>, <c>/</c> and non-decimal dots read as
///         word separators.</item>
///   <item>Images speak their alt text. Emoji, variation selectors and
///         zero-width characters are dropped without gluing neighbouring words
///         together.</item>
///   <item>A document the parser rejected is not spoken at all: raw source still
///         contains fences, URLs and credentials.</item>
/// </list>
///
/// <example>
/// <code>
/// var spoken = ChatSpeechTextProjector.Project(assistantMarkdown);
/// await speakAsync(new TtsSpeakArgs { Text = spoken });
/// </code>
/// </example>
/// </summary>
public static class ChatSpeechTextProjector
{
    /// <summary>Characters that already end a sentence; none of them is doubled.</summary>
    private const string SentenceTerminators =
        ".!?…:;。！？；：؟۔।॥．‼⁉։።။";

    /// <summary>Characters after which a source line break needs no pause comma.</summary>
    private const string PauseCharacters = ".!?…:;。！？；：,、，—-\"')]}";

    /// <summary>Trailing closers that carry a sentence's terminator inside them.</summary>
    private const string ClosingCharacters = "\"')]}»”’」』）｝】〉》";

    /// <summary>Punctuation peeled from the end of a URL so the sentence keeps it.</summary>
    private const string TrailingUrlPunctuation = ".,;:!?…)]}\"'";

    private static readonly Regex WhitespaceRuns = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// A gap before punctuation only exists because the projector dropped a
    /// link, code span or URL there, and such an orphan has nothing attached to
    /// it. Punctuation followed by a word or a path is user text: the space in
    /// <c>Install the .NET SDK</c> and <c>Run ./build</c> survives.
    /// </summary>
    private static readonly Regex SpaceBeforePunctuation =
        new(@"\s+([,.;:!?])(?=$|\s)", RegexOptions.Compiled);

    /// <summary>Orphaned separators from two adjacent dropped runs ("a , , b").</summary>
    private static readonly Regex RepeatedCommas =
        new(@",(?:\s+,)+", RegexOptions.Compiled);

    private static readonly Regex CommaBeforeTerminator =
        new(@"([,;:])\s*([.!?…])(?=$|\s)", RegexOptions.Compiled);

    private static readonly Regex ImagePlaceholder =
        new(@"\[Image(?::\s*(?<alt>[^\]]{0,256}))?\]", RegexOptions.Compiled);

    /// <summary>
    /// URL-shaped tokens: any scheme (<c>ws://</c>, <c>file://</c>), the shapes
    /// md4c refuses to autolink (ports, <c>localhost</c>, encoded paths) and
    /// <c>www.</c>/e-mail forms that only look like plain text.
    /// </summary>
    private static readonly Regex UrlToken = new(
        @"[A-Za-z][A-Za-z0-9+.\-]*://[^\s]+"
        + @"|www\.[^\s]+"
        + @"|[\p{L}\p{N}._%+\-]+@[\p{L}\p{N}\-]+(?:\.[\p{L}\p{N}\-]+)+",
        RegexOptions.Compiled);

    /// <summary>Inline code that assigns a credential is not spoken at all.</summary>
    private static readonly Regex CredentialAssignment = new(
        @"(?i)\b(?:token|secret|password|passwd|api[_-]?key|access[_-]?key"
        + @"|client[_-]?secret|authorization|bearer)\b\s*[:=]",
        RegexOptions.Compiled);

    /// <summary>
    /// Speakable plain text for <paramref name="markdown"/>. Returns an empty
    /// string when the message has nothing to read (empty, whitespace, only
    /// code blocks and URLs, or a document the parser rejected).
    /// </summary>
    public static string Project(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        var source = CapInput(markdown!);

        // The builder owns per-call parser state, so each projection gets a
        // fresh instance.
        var document = new ChatMarkdownAstBuilder(linkFlattening: MdLinkFlattening.DisplayOnly)
            .Build(source);

        // The fallback document is the verbatim source: fences, URLs and
        // credentials are all still in it, so it must not be spoken.
        if (document.IsFallback)
            return string.Empty;

        var output = new StringBuilder(source.Length);
        AppendBlocks(output, document.Blocks);
        return output.ToString().Trim();
    }

    /// <summary>
    /// Input larger than the builder's cap would come back as a raw-source
    /// fallback. Truncate first, at a scalar boundary, so the oversized message
    /// is still parsed and spoken.
    /// </summary>
    private static string CapInput(string markdown)
    {
        if (markdown.Length <= ChatMarkdownAstBuilder.MaxInputBytes)
            return markdown;

        var length = ChatMarkdownAstBuilder.MaxInputBytes;
        if (char.IsHighSurrogate(markdown[length - 1]))
            length--;

        return length == markdown.Length ? markdown : markdown[..length];
    }

    private static void AppendBlocks(StringBuilder output, IReadOnlyList<MdBlock> blocks)
    {
        foreach (var block in blocks)
            AppendBlock(output, block);
    }

    private static void AppendBlock(StringBuilder output, MdBlock block)
    {
        switch (block)
        {
            case MdHeading heading:
                AppendSentence(output, BuildBlockText(heading.Inlines));
                return;
            case MdParagraph paragraph:
                AppendSentence(output, BuildBlockText(paragraph.Inlines));
                return;
            case MdBlockQuote quote:
                AppendBlocks(output, quote.Children);
                return;
            case MdList list:
                foreach (var item in list.Items)
                    AppendBlocks(output, item.Children);
                return;
            case MdTable table:
                foreach (var row in table.HeaderRows)
                    AppendRow(output, row);
                foreach (var row in table.BodyRows)
                    AppendRow(output, row);
                return;
            case MdRawTextBlock raw:
                AppendSentence(output, StripUrls(StripUnspeakable(raw.Text)));
                return;
            case MdCodeBlock:
            case MdThematicBreak:
                // Silent. The surrounding blocks supply their own boundaries,
                // so skipping them cannot run two sentences together.
                return;
        }
    }

    private static void AppendRow(StringBuilder output, MdTableRow row)
    {
        var text = new StringBuilder();
        foreach (var cell in row.Cells)
        {
            var cellText = StripUnspeakable(BuildBlockText(cell.Inlines)).Trim();
            if (cellText.Length == 0)
                continue;
            if (text.Length > 0)
                text.Append(", ");
            text.Append(cellText);
        }

        AppendSentence(output, text.ToString());
    }

    /// <summary>
    /// Inline text of one block, before whitespace cleanup. Line breaks become
    /// a pause so wrapped or deliberately broken lines stay separate.
    /// </summary>
    private static string BuildBlockText(IReadOnlyList<MdInline> inlines)
    {
        var builder = new StringBuilder();
        AppendInlines(builder, inlines);

        // URLs are removed after the runs are joined: the parser splits a run at
        // every decoded entity, so `…?a=1&amp;b=2` arrives as several runs and a
        // per-run filter would leave the tail of the URL behind.
        return StripUrls(builder.ToString());
    }

    /// <summary>
    /// Speakable form of one inline run: the builder's inert
    /// <c>[Image: alt]</c> placeholder becomes its alt text, and credential
    /// assignments inside code spans are dropped whole. URLs are removed later,
    /// on the joined block text.
    /// </summary>
    private static string SpeakInline(MdInlineText text)
    {
        if (text.IsCode)
        {
            if (CredentialAssignment.IsMatch(text.Text))
                return string.Empty;

            // Strip here as well as on the joined block: splitting an identifier
            // on `/` and `.` would otherwise hide the scheme ("https: api …").
            return SpeakInlineCode(StripUrls(text.Text));
        }

        var value = text.Text;
        if (value.Contains("[Image", StringComparison.Ordinal))
        {
            value = ImagePlaceholder.Replace(
                value,
                match => match.Groups["alt"].Success
                    ? " " + match.Groups["alt"].Value + " "
                    : " ");
        }
        return value;
    }

    /// <summary>
    /// Removes URL-shaped tokens, keeping any sentence punctuation that trailed
    /// them so the sentence still ends properly.
    /// </summary>
    private static string StripUrls(string text) =>
        UrlToken.Replace(text, match =>
        {
            var value = match.Value;
            var end = value.Length;
            while (end > 0 && TrailingUrlPunctuation.IndexOf(value[end - 1]) >= 0)
                end--;
            return " " + value[end..];
        });

    private static void AppendInlines(StringBuilder builder, IReadOnlyList<MdInline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case MdInlineText text:
                    AppendSpeakable(builder, SpeakInline(text));
                    break;
                case MdInlineLineBreak:
                    AppendLineBreak(builder);
                    break;
            }
        }
    }

    private static void AppendLineBreak(StringBuilder builder)
    {
        var last = LastVisibleCharacter(builder);
        if (last is not null && PauseCharacters.IndexOf(last.Value) >= 0)
            builder.Append(' ');
        else
            builder.Append(", ");
    }

    private static char? LastVisibleCharacter(StringBuilder builder)
    {
        for (int i = builder.Length - 1; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(builder[i]))
                return builder[i];
        }
        return null;
    }

    /// <summary>Appends one sentence, followed by a terminator it does not already have.</summary>
    private static void AppendSentence(StringBuilder output, string text)
    {
        var sentence = Cleanup(text);
        if (sentence.Length == 0)
            return;

        if (output.Length > 0
            && !(IsCjkTerminator(output[^1]) && IsCjk(sentence[0])))
        {
            output.Append(' ');
        }

        output.Append(sentence);

        var end = LastSpeechCharacterIndex(sentence);
        if (end < 0 || SentenceTerminators.IndexOf(sentence[end]) < 0)
            output.Append(end >= 0 && IsCjk(sentence[end]) ? '。' : '.');
    }

    /// <summary>
    /// Index of the character that decides the sentence ending, skipping
    /// closing quotes and brackets (<c>"Done."</c> is already finished) and
    /// invisible format characters such as bidi marks.
    /// </summary>
    private static int LastSpeechCharacterIndex(string sentence)
    {
        for (var index = sentence.Length - 1; index >= 0; index--)
        {
            var value = sentence[index];
            if (ClosingCharacters.IndexOf(value) >= 0
                || CharUnicodeInfo.GetUnicodeCategory(value) == UnicodeCategory.Format)
            {
                continue;
            }
            return index;
        }
        return -1;
    }

    private static string Cleanup(string text)
    {
        var cleaned = Collapse(text);
        if (cleaned.Length == 0)
            return cleaned;

        cleaned = SpaceBeforePunctuation.Replace(cleaned, "$1");
        cleaned = RepeatedCommas.Replace(cleaned, ", ");
        cleaned = CommaBeforeTerminator.Replace(cleaned, "$2");
        return TrimOrphanedSeparators(cleaned);
    }

    /// <summary>
    /// Trims only separators the projector can orphan: a trailing <c>, ; :</c>
    /// (the terminator replaces it) and a leading separator that is not the
    /// start of a word, so <c>.NET 10</c> keeps its dot.
    /// </summary>
    private static string TrimOrphanedSeparators(string text)
    {
        var trimmed = text.Trim().TrimEnd(',', ';', ':').TrimEnd();
        if (trimmed.Length == 0)
            return trimmed;

        if (trimmed[0] is '.' or ',' or ';' or ':'
            && (trimmed.Length == 1 || !char.IsLetterOrDigit(trimmed[1])))
        {
            trimmed = trimmed.TrimStart(',', ';', ':', '.');
        }

        return trimmed;
    }

    private static string Collapse(string text) =>
        WhitespaceRuns.Replace(text, " ").Trim();

    /// <summary>
    /// Appends <paramref name="text"/> with emoji, variation selectors and
    /// zero-width characters removed, and with underscores read as spaces so
    /// <c>user_id</c> is spoken as two words. A removed character leaves one
    /// space so the words on either side stay separate. Invalid UTF-16 is
    /// skipped instead of throwing, because a truncated or malformed message
    /// must not silence the whole reply.
    /// </summary>
    private static void AppendSpeakable(StringBuilder builder, string text)
    {
        var start = 0;
        for (var index = 0; index < text.Length;)
        {
            var status = Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var consumed);
            var length = consumed > 0 ? consumed : 1;

            if (status == OperationStatus.Done && !IsUnspeakable(rune))
            {
                index += length;
                continue;
            }

            if (index > start)
                builder.Append(text, start, index - start);

            // Joiners are invisible parts of the surrounding word: ZWJ holds
            // Indic syllables and emoji sequences together, and a variation
            // selector only modifies the glyph before it. Only a removed
            // pictograph or underscore needs a separator.
            if (status == OperationStatus.Done && !IsJoiner(rune))
                builder.Append(' ');

            index += length;
            start = index;
        }

        if (text.Length > start)
            builder.Append(text, start, text.Length - start);
    }

    private static bool IsJoiner(Rune rune) =>
        rune.Value is 0x200D or 0xFE0E or 0xFE0F;

    private static bool IsUnspeakable(Rune rune)
    {
        var value = rune.Value;
        return value is '_' or 0x20E3
                or 0xFE0E or 0xFE0F    // variation selectors
                or 0x200B or 0x200D    // zero-width space / joiner
            || value is >= 0x2600 and <= 0x27BF   // ☀ ✅ ❌ ⚠ ❤ ✨ ⭐ ➡
            || value is >= 0x2B00 and <= 0x2BFF   // ⬛ ⬜ ⭐
            || value is >= 0x1F000 and <= 0x1FAFF // faces, transport, skin tones
            || value is >= 0xE0020 and <= 0xE007F; // emoji tag sequences
    }

    private static string StripUnspeakable(string text)
    {
        var builder = new StringBuilder(text.Length);
        AppendSpeakable(builder, text);
        return builder.ToString();
    }

    /// <summary>
    /// Inline code is spoken as words: <c>a/b</c> and <c>build.ps1</c> become
    /// separate words rather than one run-together token. A dot between digits
    /// survives so <c>1.5</c> is still "one point five"; every other dot would
    /// be read as a sentence end, which inserts a false pause. Underscores are
    /// handled by <see cref="AppendSpeakable"/> for every run.
    /// </summary>
    private static string SpeakInlineCode(string code)
    {
        var builder = new StringBuilder(code.Length);
        for (var index = 0; index < code.Length; index++)
        {
            var value = code[index];
            if (value is '/' or '.' && !IsDecimalPoint(code, index))
            {
                builder.Append(' ');
                continue;
            }

            builder.Append(value);
        }

        return builder.ToString();
    }

    private static bool IsDecimalPoint(string code, int index) =>
        code[index] == '.'
        && index > 0
        && index + 1 < code.Length
        && char.IsAsciiDigit(code[index - 1])
        && char.IsAsciiDigit(code[index + 1]);

    private static bool IsCjk(char value) =>
        value is >= '\u3000' and <= '\u303F'   // CJK punctuation
            or >= '\u3040' and <= '\u30FF'     // kana
            or >= '\u3400' and <= '\u4DBF'     // CJK extension A
            or >= '\u4E00' and <= '\u9FFF'     // CJK unified ideographs
            or >= '\uF900' and <= '\uFAFF'     // compatibility ideographs
            or >= '\uFF00' and <= '\uFFEF';    // fullwidth forms

    /// <summary>CJK sentences are joined without a space, unlike Latin ones.</summary>
    private static bool IsCjkTerminator(char value) =>
        value is '。' or '！' or '？' or '；' or '：' or '、' or '，' or '．' or '」' or '』';
}
