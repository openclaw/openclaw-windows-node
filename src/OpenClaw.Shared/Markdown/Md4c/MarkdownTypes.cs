// C# port of Martin Mitáš's md4c Markdown parser types.
// Ported from md4c/src/md4c.h

namespace OpenClaw.Shared.Markdown.Md4c;

/// <summary>
/// String attribute used for propagating strings within detail structures
/// (e.g. link titles, code info strings). May contain mixed substring types.
/// </summary>
public readonly struct MarkdownAttribute
{
    public readonly string? Text;
    public readonly MarkdownTextType[] SubstrTypes;
    public readonly int[] SubstrOffsets;

    public MarkdownAttribute(string? text, MarkdownTextType[] substrTypes, int[] substrOffsets)
    {
        Text = text;
        SubstrTypes = substrTypes;
        SubstrOffsets = substrOffsets;
    }

    /// <summary>
    /// Creates a simple attribute with a single normal-text substring.
    /// </summary>
    public static MarkdownAttribute Simple(string? text)
    {
        if (text == null)
            return default;
        return new MarkdownAttribute(text, new[] { MarkdownTextType.Normal }, new[] { 0, text.Length });
    }
}

/// <summary>Detailed info for <see cref="MarkdownBlockType.Ul"/>.</summary>
public struct MarkdownBlockUlDetail
{
    /// <summary>True if tight list, false if loose.</summary>
    public bool IsTight;
    /// <summary>Item bullet character in Markdown source, e.g. '-', '+', '*'.</summary>
    public char Mark;
}

/// <summary>Detailed info for <see cref="MarkdownBlockType.Ol"/>.</summary>
public struct MarkdownBlockOlDetail
{
    /// <summary>Start index of the ordered list.</summary>
    public int Start;
    /// <summary>True if tight list, false if loose.</summary>
    public bool IsTight;
    /// <summary>Character delimiting the item marks, e.g. '.' or ')'.</summary>
    public char MarkDelimiter;
}

/// <summary>Detailed info for <see cref="MarkdownBlockType.Li"/>.</summary>
public struct MarkdownBlockLiDetail
{
    /// <summary>True if this is a task list item (requires <see cref="MarkdownParserFlags.TaskLists"/>).</summary>
    public bool IsTask;
    /// <summary>If IsTask, one of 'x', 'X' or ' '.</summary>
    public char TaskMark;
    /// <summary>If IsTask, offset in input of the char between '[' and ']'.</summary>
    public int TaskMarkOffset;
}

/// <summary>Detailed info for <see cref="MarkdownBlockType.H"/>.</summary>
public struct MarkdownBlockHDetail
{
    /// <summary>Header level (1-6).</summary>
    public int Level;
}

/// <summary>Detailed info for <see cref="MarkdownBlockType.Code"/>.</summary>
public struct MarkdownBlockCodeDetail
{
    public MarkdownAttribute Info;
    public MarkdownAttribute Lang;
    /// <summary>The character used for fenced code block; or '\0' for indented code block.</summary>
    public char FenceChar;
}

/// <summary>Detailed info for <see cref="MarkdownBlockType.Table"/>.</summary>
public struct MarkdownBlockTableDetail
{
    /// <summary>Count of columns in the table.</summary>
    public int ColCount;
    /// <summary>Count of rows in the table header (currently always 1).</summary>
    public int HeadRowCount;
    /// <summary>Count of rows in the table body.</summary>
    public int BodyRowCount;
}

/// <summary>Detailed info for <see cref="MarkdownBlockType.Th"/> and <see cref="MarkdownBlockType.Td"/>.</summary>
public struct MarkdownBlockTdDetail
{
    public MarkdownAlign Align;
}

/// <summary>Detailed info for <see cref="MarkdownSpanType.A"/>.</summary>
public struct MarkdownSpanADetail
{
    public MarkdownAttribute Href;
    public MarkdownAttribute Title;
    /// <summary>True if this is an autolink.</summary>
    public bool IsAutolink;
}

/// <summary>Detailed info for <see cref="MarkdownSpanType.Img"/>.</summary>
public struct MarkdownSpanImgDetail
{
    public MarkdownAttribute Src;
    public MarkdownAttribute Title;
}

/// <summary>Detailed info for <see cref="MarkdownSpanType.WikiLink"/>.</summary>
public struct MarkdownSpanWikiLinkDetail
{
    public MarkdownAttribute Target;
}

/// <summary>
/// Callback delegates for the SAX-style parser.
/// All callbacks may abort parsing by returning non-zero.
/// </summary>
public delegate int MarkdownEnterBlockCallback(MarkdownBlockType type, object? detail);
public delegate int MarkdownLeaveBlockCallback(MarkdownBlockType type, object? detail);
public delegate int MarkdownEnterSpanCallback(MarkdownSpanType type, object? detail);
public delegate int MarkdownLeaveSpanCallback(MarkdownSpanType type, object? detail);
public delegate int MarkdownTextCallback(MarkdownTextType type, ReadOnlySpan<char> text);
public delegate void MarkdownDebugLogCallback(string message);
