// C# port of Martin Mitáš's md4c Markdown parser enums and flags.
// Ported from md4c/src/md4c.h

namespace OpenClawTray.Infrastructure.Markdown;

/// <summary>
/// Block represents a part of document hierarchy structure like a paragraph or list item.
/// </summary>
public enum MdBlockType
{
    /// <summary>&lt;body&gt;...&lt;/body&gt;</summary>
    Doc = 0,
    /// <summary>&lt;blockquote&gt;...&lt;/blockquote&gt;</summary>
    Quote,
    /// <summary>&lt;ul&gt;...&lt;/ul&gt;. Detail: <see cref="MdBlockUlDetail"/>.</summary>
    Ul,
    /// <summary>&lt;ol&gt;...&lt;/ol&gt;. Detail: <see cref="MdBlockOlDetail"/>.</summary>
    Ol,
    /// <summary>&lt;li&gt;...&lt;/li&gt;. Detail: <see cref="MdBlockLiDetail"/>.</summary>
    Li,
    /// <summary>&lt;hr&gt;</summary>
    Hr,
    /// <summary>&lt;h1&gt;...&lt;/h6&gt; (levels 1-6). Detail: <see cref="MdBlockHDetail"/>.</summary>
    H,
    /// <summary>&lt;pre&gt;&lt;code&gt;...&lt;/code&gt;&lt;/pre&gt;. Detail: <see cref="MdBlockCodeDetail"/>.</summary>
    Code,
    /// <summary>Raw HTML block.</summary>
    Html,
    /// <summary>&lt;p&gt;...&lt;/p&gt;</summary>
    P,
    /// <summary>&lt;table&gt;...&lt;/table&gt;. Detail: <see cref="MdBlockTableDetail"/>.</summary>
    Table,
    /// <summary>&lt;thead&gt;...&lt;/thead&gt;</summary>
    Thead,
    /// <summary>&lt;tbody&gt;...&lt;/tbody&gt;</summary>
    Tbody,
    /// <summary>&lt;tr&gt;...&lt;/tr&gt;</summary>
    Tr,
    /// <summary>&lt;th&gt;...&lt;/th&gt;. Detail: <see cref="MdBlockTdDetail"/>.</summary>
    Th,
    /// <summary>&lt;td&gt;...&lt;/td&gt;. Detail: <see cref="MdBlockTdDetail"/>.</summary>
    Td,
}

/// <summary>
/// Span represents an in-line piece of a document which should be rendered with
/// the same font, color and other attributes.
/// </summary>
public enum MdSpanType
{
    /// <summary>&lt;em&gt;...&lt;/em&gt;</summary>
    Em,
    /// <summary>&lt;strong&gt;...&lt;/strong&gt;</summary>
    Strong,
    /// <summary>&lt;a href="xxx"&gt;...&lt;/a&gt;. Detail: <see cref="MdSpanADetail"/>.</summary>
    A,
    /// <summary>&lt;img src="xxx"&gt;...&lt;/img&gt;. Detail: <see cref="MdSpanImgDetail"/>.</summary>
    Img,
    /// <summary>&lt;code&gt;...&lt;/code&gt;</summary>
    Code,
    /// <summary>&lt;del&gt;...&lt;/del&gt; (requires <see cref="MdParserFlags.Strikethrough"/>).</summary>
    Del,
    /// <summary>Inline LaTeX math (requires <see cref="MdParserFlags.LatexMathSpans"/>).</summary>
    LatexMath,
    /// <summary>Display LaTeX math (requires <see cref="MdParserFlags.LatexMathSpans"/>).</summary>
    LatexMathDisplay,
    /// <summary>Wiki link (requires <see cref="MdParserFlags.WikiLinks"/>).</summary>
    WikiLink,
    /// <summary>&lt;u&gt;...&lt;/u&gt; (requires <see cref="MdParserFlags.Underline"/>).</summary>
    U,
}

/// <summary>
/// Text is the actual textual contents of a span.
/// </summary>
public enum MdTextType
{
    /// <summary>Normal text.</summary>
    Normal = 0,
    /// <summary>NULL character (should be replaced with U+FFFD).</summary>
    NullChar,
    /// <summary>&lt;br&gt; (hard break).</summary>
    Br,
    /// <summary>Soft break ('\n' in source where not semantically meaningful).</summary>
    SoftBr,
    /// <summary>HTML entity (named, numeric, or hex).</summary>
    Entity,
    /// <summary>Text in a code block or inline code.</summary>
    Code,
    /// <summary>Raw HTML text.</summary>
    Html,
    /// <summary>Text inside a LaTeX equation.</summary>
    LatexMath,
}

/// <summary>
/// Table cell alignment.
/// </summary>
public enum MdAlign
{
    Default = 0,
    Left,
    Center,
    Right,
}

/// <summary>
/// Parser extension flags.
/// </summary>
[Flags]
public enum MdParserFlags : uint
{
    None = 0,
    /// <summary>In MD_TEXT_NORMAL, collapse non-trivial whitespace into single ' '.</summary>
    CollapseWhitespace = 0x0001,
    /// <summary>Do not require space in ATX headers ( ###header ).</summary>
    PermissiveAtxHeaders = 0x0002,
    /// <summary>Recognize URLs as autolinks even without '&lt;', '&gt;'.</summary>
    PermissiveUrlAutolinks = 0x0004,
    /// <summary>Recognize e-mails as autolinks even without '&lt;', '&gt;' and 'mailto:'.</summary>
    PermissiveEmailAutolinks = 0x0008,
    /// <summary>Disable indented code blocks. (Only fenced code works.)</summary>
    NoIndentedCodeBlocks = 0x0010,
    /// <summary>Disable raw HTML blocks.</summary>
    NoHtmlBlocks = 0x0020,
    /// <summary>Disable raw HTML (inline).</summary>
    NoHtmlSpans = 0x0040,
    /// <summary>Enable tables extension.</summary>
    Tables = 0x0100,
    /// <summary>Enable strikethrough extension.</summary>
    Strikethrough = 0x0200,
    /// <summary>Enable WWW autolinks (even without scheme prefix, if they begin with 'www.').</summary>
    PermissiveWwwAutolinks = 0x0400,
    /// <summary>Enable task list extension.</summary>
    TaskLists = 0x0800,
    /// <summary>Enable $ and $$ containing LaTeX equations.</summary>
    LatexMathSpans = 0x1000,
    /// <summary>Enable wiki links extension.</summary>
    WikiLinks = 0x2000,
    /// <summary>Enable underline extension (disables '_' for normal emphasis).</summary>
    Underline = 0x4000,
    /// <summary>Force all soft breaks to act as hard breaks.</summary>
    HardSoftBreaks = 0x8000,

    /// <summary>All permissive autolinks.</summary>
    PermissiveAutolinks = PermissiveEmailAutolinks | PermissiveUrlAutolinks | PermissiveWwwAutolinks,
    /// <summary>Disable all raw HTML.</summary>
    NoHtml = NoHtmlBlocks | NoHtmlSpans,

    /// <summary>CommonMark dialect (no extensions).</summary>
    DialectCommonMark = 0,
    /// <summary>GitHub-flavored Markdown dialect.</summary>
    DialectGitHub = PermissiveAutolinks | Tables | Strikethrough | TaskLists,
}
