// C# port of Martin Mitáš's md4c Markdown parser types.
// Ported from md4c/src/md4c.h

namespace OpenClawTray.Infrastructure.Markdown;

/// <summary>
/// String attribute used for propagating strings within detail structures
/// (e.g. link titles, code info strings). May contain mixed substring types.
/// </summary>
public readonly struct MdAttribute
{
    public readonly string? Text;
    public readonly MdTextType[] SubstrTypes;
    public readonly int[] SubstrOffsets;

    public MdAttribute(string? text, MdTextType[] substrTypes, int[] substrOffsets)
    {
        Text = text;
        SubstrTypes = substrTypes;
        SubstrOffsets = substrOffsets;
    }

    /// <summary>
    /// Creates a simple attribute with a single normal-text substring.
    /// </summary>
    public static MdAttribute Simple(string? text)
    {
        if (text == null)
            return default;
        return new MdAttribute(text, new[] { MdTextType.Normal }, new[] { 0, text.Length });
    }
}

/// <summary>Detailed info for <see cref="MdBlockType.Ul"/>.</summary>
public struct MdBlockUlDetail
{
    /// <summary>True if tight list, false if loose.</summary>
    public bool IsTight;
    /// <summary>Item bullet character in Markdown source, e.g. '-', '+', '*'.</summary>
    public char Mark;
}

/// <summary>Detailed info for <see cref="MdBlockType.Ol"/>.</summary>
public struct MdBlockOlDetail
{
    /// <summary>Start index of the ordered list.</summary>
    public int Start;
    /// <summary>True if tight list, false if loose.</summary>
    public bool IsTight;
    /// <summary>Character delimiting the item marks, e.g. '.' or ')'.</summary>
    public char MarkDelimiter;
}

/// <summary>Detailed info for <see cref="MdBlockType.Li"/>.</summary>
public struct MdBlockLiDetail
{
    /// <summary>True if this is a task list item (requires <see cref="MdParserFlags.TaskLists"/>).</summary>
    public bool IsTask;
    /// <summary>If IsTask, one of 'x', 'X' or ' '.</summary>
    public char TaskMark;
    /// <summary>If IsTask, offset in input of the char between '[' and ']'.</summary>
    public int TaskMarkOffset;
}

/// <summary>Detailed info for <see cref="MdBlockType.H"/>.</summary>
public struct MdBlockHDetail
{
    /// <summary>Header level (1-6).</summary>
    public int Level;
}

/// <summary>Detailed info for <see cref="MdBlockType.Code"/>.</summary>
public struct MdBlockCodeDetail
{
    public MdAttribute Info;
    public MdAttribute Lang;
    /// <summary>The character used for fenced code block; or '\0' for indented code block.</summary>
    public char FenceChar;
}

/// <summary>Detailed info for <see cref="MdBlockType.Table"/>.</summary>
public struct MdBlockTableDetail
{
    /// <summary>Count of columns in the table.</summary>
    public int ColCount;
    /// <summary>Count of rows in the table header (currently always 1).</summary>
    public int HeadRowCount;
    /// <summary>Count of rows in the table body.</summary>
    public int BodyRowCount;
}

/// <summary>Detailed info for <see cref="MdBlockType.Th"/> and <see cref="MdBlockType.Td"/>.</summary>
public struct MdBlockTdDetail
{
    public MdAlign Align;
}

/// <summary>Detailed info for <see cref="MdSpanType.A"/>.</summary>
public struct MdSpanADetail
{
    public MdAttribute Href;
    public MdAttribute Title;
    /// <summary>True if this is an autolink.</summary>
    public bool IsAutolink;
}

/// <summary>Detailed info for <see cref="MdSpanType.Img"/>.</summary>
public struct MdSpanImgDetail
{
    public MdAttribute Src;
    public MdAttribute Title;
}

/// <summary>Detailed info for <see cref="MdSpanType.WikiLink"/>.</summary>
public struct MdSpanWikiLinkDetail
{
    public MdAttribute Target;
}

/// <summary>
/// Callback delegates for the SAX-style parser.
/// All callbacks may abort parsing by returning non-zero.
/// </summary>
public delegate int MdEnterBlockCallback(MdBlockType type, object? detail);
public delegate int MdLeaveBlockCallback(MdBlockType type, object? detail);
public delegate int MdEnterSpanCallback(MdSpanType type, object? detail);
public delegate int MdLeaveSpanCallback(MdSpanType type, object? detail);
public delegate int MdTextCallback(MdTextType type, ReadOnlySpan<char> text);
public delegate void MdDebugLogCallback(string message);
