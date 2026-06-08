using System.Collections.Generic;

namespace OpenClaw.Shared.Markdown;

/// <summary>
/// Pure-data representation of a chat-bubble Markdown document. Produced by
/// <see cref="ChatMarkdownAstBuilder"/> from the vendored md4c SAX parser
/// and consumed by the WinUI-side renderer in <c>OpenClaw.Tray.WinUI</c>.
///
/// Lives in <c>OpenClaw.Shared</c> (pure <c>net10.0</c>, no WinUI) so the
/// parse+sanitize behavior is unit-testable without the WinUI runtime.
///
/// SECURITY: link <c>Href</c> and image <c>Src</c> values are intentionally
/// NOT carried in the AST — the builder flattens them into the
/// surrounding <see cref="MdInlineText"/> at parse time so no downstream
/// renderer can re-introduce a <c>Hyperlink</c> or a remote-image fetch.
/// Raw HTML blocks/spans are dropped (via <c>NoHtml</c> parser flag) and
/// any that leak through are stored as inert <see cref="MdInlineText"/>.
/// </summary>
public sealed record ChatMarkdownDocument(IReadOnlyList<MdBlock> Blocks);

public abstract record MdBlock;

public sealed record MdHeading(int Level, IReadOnlyList<MdInline> Inlines) : MdBlock;
public sealed record MdParagraph(IReadOnlyList<MdInline> Inlines) : MdBlock;
public sealed record MdBlockQuote(IReadOnlyList<MdBlock> Children) : MdBlock;
public sealed record MdThematicBreak : MdBlock;
public sealed record MdCodeBlock(string Code, string? Language) : MdBlock;

/// <summary>Inert plain-text fallback for raw HTML blocks that slipped through.</summary>
public sealed record MdRawTextBlock(string Text) : MdBlock;

public enum MdListMarker
{
    Bullet,
    Ordered,
}

public sealed record MdList(MdListMarker Marker, int StartNumber, IReadOnlyList<MdListItem> Items) : MdBlock;

/// <summary>
/// A single list item. <see cref="TaskState"/> is non-null for GFM task list
/// items: <see cref="MdTaskState.Checked"/> for <c>[x]</c>,
/// <see cref="MdTaskState.Unchecked"/> for <c>[ ]</c>.
/// </summary>
public sealed record MdListItem(IReadOnlyList<MdBlock> Children, MdTaskState? TaskState = null) : MdBlock;

public enum MdTaskState
{
    Unchecked,
    Checked,
}

public enum MdColumnAlignment
{
    Default,
    Left,
    Center,
    Right,
}

public sealed record MdTable(
    IReadOnlyList<MdColumnAlignment> ColumnAlignments,
    IReadOnlyList<MdTableRow> HeaderRows,
    IReadOnlyList<MdTableRow> BodyRows) : MdBlock;

public sealed record MdTableRow(IReadOnlyList<MdTableCell> Cells);

public sealed record MdTableCell(IReadOnlyList<MdInline> Inlines);

/// <summary>
/// Base type for all inline AST nodes.
/// <para>
/// IMPORTANT — cache invariant: <c>ChatMarkdownRenderer</c> uses
/// <c>IReadOnlyList&lt;MdInline&gt;.SequenceEqual</c> (record value-equality)
/// to short-circuit rebuilding <c>TextBlock.Inlines</c> on re-render, which
/// is what keeps a user's text selection alive across pointer-enter/leave
/// and streaming token updates. That short-circuit is sound ONLY while every
/// concrete <c>MdInline</c> subtype contains exclusively value-comparable
/// members (primitives, <c>string</c>, enums). If a future subtype adds a
/// reference-typed member (e.g. <c>IReadOnlyList&lt;MdInline&gt; Children</c>
/// for links), the auto-generated record <c>Equals</c> will compare those
/// members BY REFERENCE — silently breaking cache correctness and wiping
/// selection again on every re-render. Update the renderer's equality
/// strategy (and <c>MdInlineEqualityTests</c>) before introducing such a
/// member.
/// </para>
/// </summary>
public abstract record MdInline;

/// <summary>
/// Run of styled text. <see cref="IsCode"/> = true means the run is part of
/// inline code (renderers should use a monospace font); <see cref="IsStrong"/>
/// and <see cref="IsEmphasis"/> stack independently; <see cref="IsStrike"/>
/// and <see cref="IsUnderline"/> are GFM/extension only.
/// </summary>
public sealed record MdInlineText(
    string Text,
    bool IsStrong = false,
    bool IsEmphasis = false,
    bool IsCode = false,
    bool IsStrike = false,
    bool IsUnderline = false) : MdInline;

/// <summary>Soft or hard line break inside an inline sequence.</summary>
public sealed record MdInlineLineBreak(bool IsHard) : MdInline;
