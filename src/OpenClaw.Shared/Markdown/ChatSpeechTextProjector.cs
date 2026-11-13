using System;
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
///   <item>User punctuation is otherwise preserved verbatim. There is no
///         character blacklist.</item>
///   <item>Fenced code blocks are silent but still end a sentence. Inline code
///         is spoken, with <c>_</c>, <c>/</c> and non-decimal dots read as
///         word separators.</item>
///   <item>Links speak their display text only; autolinks and bare URLs are
///         silent. Images speak their alt text.</item>
///   <item>Emoji, variation selectors and zero-width characters are dropped
///         without gluing the surrounding words together.</item>
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
    private const string SentenceTerminators = ".!?…:;。！？；：";

    /// <summary>Characters after which a source line break needs no pause comma.</summary>
    private const string PauseCharacters = ".!?…:;。！？；：,、，—-\"')]}";

    private static readonly Regex WhitespaceRuns = new(@"\s+", RegexOptions.Compiled);

    private static readonly Regex SpaceBeforePunctuation =
        new(@"\s+([,.;:!?])", RegexOptions.Compiled);

    private static readonly Regex RepeatedCommas = new(@"(?:,\s*){2,}", RegexOptions.Compiled);

    private static readonly Regex CommaBeforeTerminator =
        new(@"([,;:])\s*([.!?…])", RegexOptions.Compiled);

    private static readonly Regex ImagePlaceholder =
        new(@"\[Image(?::\s*(?<alt>[^\]]*))?\]", RegexOptions.Compiled);

    /// <summary>
    /// Speakable plain text for <paramref name="markdown"/>. Returns an empty
    /// string when the message has nothing to read (empty, whitespace, or only
    /// code blocks and URLs).
    /// </summary>
    public static string Project(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        // The builder owns per-call parser state, so each projection gets a
        // fresh instance.
        var document = new ChatMarkdownAstBuilder(linkFlattening: MdLinkFlattening.DisplayOnly)
            .Build(markdown);

        var output = new StringBuilder(markdown!.Length);
        AppendBlocks(output, document.Blocks);
        return output.ToString().Trim();
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
                AppendSentence(output, StripUnspeakable(raw.Text));
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
        return builder.ToString();
    }

    /// <summary>
    /// Speakable form of one inline run: the builder's inert
    /// <c>[Image: alt]</c> placeholder becomes its alt text (or nothing), and
    /// inline code loses its identifier separators.
    /// </summary>
    private static string SpeakInline(MdInlineText text)
    {
        var value = text.IsCode ? SpeakInlineCode(text.Text) : text.Text;
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
        if (SentenceTerminators.IndexOf(sentence[^1]) < 0)
            output.Append(IsCjk(sentence[^1]) ? '。' : '.');
    }

    private static string Cleanup(string text)
    {
        var cleaned = Collapse(text);
        if (cleaned.Length == 0)
            return cleaned;

        cleaned = SpaceBeforePunctuation.Replace(cleaned, "$1");
        cleaned = RepeatedCommas.Replace(cleaned, ", ");
        cleaned = CommaBeforeTerminator.Replace(cleaned, "$2");
        // A comma, semicolon or colon left dangling by a removed link or code
        // span cannot end a sentence: the terminator takes its place.
        cleaned = cleaned.Trim().Trim(',', ';', ':').Trim();
        cleaned = cleaned.TrimStart(',', ';', ':', '.');
        return cleaned;
    }

    private static string Collapse(string text) =>
        WhitespaceRuns.Replace(text, " ").Trim();

    /// <summary>
    /// Appends <paramref name="text"/> with emoji, variation selectors and
    /// zero-width characters removed, and with underscores read as spaces so
    /// <c>user_id</c> is spoken as two words. A removed character leaves one
    /// space so the words on either side stay separate.
    /// </summary>
    private static void AppendSpeakable(StringBuilder builder, string text)
    {
        var start = 0;
        for (var index = 0; index < text.Length;)
        {
            var rune = Rune.GetRuneAt(text, index);
            if (!IsUnspeakable(rune))
            {
                index += rune.Utf16SequenceLength;
                continue;
            }

            if (index > start)
                builder.Append(text, start, index - start);
            builder.Append(' ');
            index += rune.Utf16SequenceLength;
            start = index;
        }

        if (text.Length > start)
            builder.Append(text, start, text.Length - start);
    }

    private static bool IsUnspeakable(Rune rune) =>
        // Markdown emphasis underscores are already gone from the AST, so every
        // remaining one is an identifier separator.
        rune.Value is '_' or 0xFE0F or 0x200B or 0x200D
        || (rune.Value > 0xFFFF && Rune.GetUnicodeCategory(rune) == UnicodeCategory.OtherSymbol);

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
        value is >= '\u3040' and <= '\u30FF'   // kana
            or >= '\u3400' and <= '\u4DBF'     // CJK extension A
            or >= '\u4E00' and <= '\u9FFF'     // CJK unified ideographs
            or >= '\uF900' and <= '\uFAFF';    // compatibility ideographs

    /// <summary>CJK sentences are joined without a space, unlike Latin ones.</summary>
    private static bool IsCjkTerminator(char value) =>
        value is '。' or '！' or '？' or '；' or '：' or '、' or '，';
}
