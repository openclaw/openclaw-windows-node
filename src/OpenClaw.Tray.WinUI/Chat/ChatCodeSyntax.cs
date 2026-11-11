// Classifies fenced Markdown code into the web transcript's highlight.js color roles.
// Presentation only: the scan is linear, never executes or rewrites code, and
// unknown languages or oversized input stay plain.
// Usage:
//   foreach (var span in ChatCodeSyntax.Classify(code, language))
//       Color(code.Substring(span.Start, span.Length), span.Role);

namespace OpenClawTray.Chat;

/// <summary>Web highlight.js roles that carry a color in the chat code palette.</summary>
internal enum ChatCodeRole
{
    Keyword,
    String,
    Number,
    Comment,
    Title,
    Type,
    Attribute,
}

internal readonly record struct ChatCodeSpan(int Start, int Length, ChatCodeRole Role);

/// <summary>
/// Small lexical classifier for fenced code. It approximates the web client's
/// highlight.js scopes (keywords, strings, literals/variables, comments, function and
/// class titles, built-in types and property keys) for common chat languages.
/// </summary>
internal static class ChatCodeSyntax
{
    /// <summary>Longer blocks render as plain literal text so highlighting stays cheap.</summary>
    internal const int MaxHighlightedLength = 32_000;

    /// <summary>Token-dense blocks (for example generated data) also stay plain.</summary>
    internal const int MaxHighlightedSpans = 4_000;

    private sealed record Grammar(
        HashSet<string> Keywords,
        HashSet<string> Types,
        HashSet<string> Literals,
        HashSet<string> TitleIntroducers,
        bool SlashComments = false,
        bool HashComments = false,
        bool DashComments = false,
        bool BacktickStrings = false,
        bool TripleQuotes = false,
        bool MultilineStrings = false,
        bool PropertyKeys = false,
        bool AssignmentKeys = false,
        bool DollarVariables = false,
        bool DollarIdentifiers = false,
        bool DashIdentifiers = false,
        bool Cmdlets = false,
        bool CallTitles = true,
        bool Markup = false);

    private static HashSet<string> Set(string words, bool ignoreCase = false) =>
        new(words.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static readonly HashSet<string> NoWords = Set("");

    private static readonly Grammar Script = new(
        Set("abstract as async await break case catch class const continue debugger declare default delete do " +
            "else enum export extends finally for from function get if implements import in instanceof interface " +
            "keyof let namespace new of private protected public readonly return satisfies set static super switch " +
            "this throw try type typeof var void while with yield"),
        Set("any bigint boolean never number object string symbol unknown Array Date Error Map Object Promise " +
            "Record RegExp Set"),
        Set("true false null undefined NaN Infinity"),
        Set("class enum extends function implements interface new type"),
        SlashComments: true, BacktickStrings: true, PropertyKeys: true, DollarIdentifiers: true);

    private static readonly Grammar Json = new(
        NoWords, NoWords, Set("true false null"), NoWords,
        SlashComments: true, PropertyKeys: true, CallTitles: false);

    private static readonly Grammar CSharp = new(
        Set("abstract as async await base break case catch checked class const continue default delegate do else " +
            "enum event explicit extern finally fixed for foreach get goto if implicit in init interface internal is " +
            "lock namespace new operator out override params partial private protected public readonly record ref " +
            "required return sealed set sizeof stackalloc static struct switch this throw try typeof unchecked unsafe " +
            "using value virtual volatile when where while with yield"),
        Set("bool byte char decimal double dynamic float int long nint nuint object sbyte short string uint ulong " +
            "ushort var void"),
        Set("true false null"),
        Set("class enum interface new record struct"),
        SlashComments: true, MultilineStrings: true);

    private static readonly Grammar CFamily = new(
        Set("abstract async await break case catch class const constexpr continue crate default defer delete do " +
            "else enum export extends extern final finally fn for func go if impl implements import in inline " +
            "interface internal let loop match mod module mut namespace new override package private protected pub " +
            "public return select self static struct super switch template this throw throws trait try type typedef " +
            "typename union unsafe use using var virtual when where while yield"),
        Set("bool boolean byte char double f32 f64 float i8 i16 i32 i64 i128 int isize long short size_t str string " +
            "String u8 u16 u32 u64 u128 uint usize void"),
        Set("true false null nil None nullptr"),
        Set("class enum fn func impl interface new struct trait type"),
        SlashComments: true, BacktickStrings: true);

    private static readonly Grammar Python = new(
        Set("and as assert async await break class continue def del elif else except finally for from global if " +
            "import in is lambda match nonlocal not or pass raise return try while with yield"),
        Set("bool bytes dict float int list object set str tuple"),
        Set("True False None"),
        Set("class def"),
        HashComments: true, TripleQuotes: true);

    private static readonly Grammar PowerShell = new(
        Set("begin break catch class continue data do dynamicparam else elseif end enum exit filter finally for " +
            "foreach from function if in param process return switch throw trap try until using while", ignoreCase: true),
        NoWords,
        NoWords,
        Set("class enum filter function", ignoreCase: true),
        HashComments: true, MultilineStrings: true, DollarVariables: true, Cmdlets: true, CallTitles: false);

    private static readonly Grammar Shell = new(
        Set("case do done elif else esac fi for function if in local return select then until while"),
        Set("alias cat cd chmod cp curl echo export grep ls mkdir mv printf pwd read rm sed set source sudo test " +
            "unset"),
        Set("true false"),
        Set("function"),
        HashComments: true, MultilineStrings: true, DollarVariables: true, CallTitles: false);

    private static readonly Grammar Yaml = new(
        NoWords, NoWords, Set("true false null yes no on off ~"), NoWords,
        HashComments: true, PropertyKeys: true, DashIdentifiers: true, CallTitles: false);

    private static readonly Grammar Toml = new(
        NoWords, NoWords, Set("true false"), NoWords,
        HashComments: true, AssignmentKeys: true, DashIdentifiers: true, CallTitles: false);

    private static readonly Grammar Sql = new(
        Set("add all alter and as asc begin between by case column commit create cross database default delete " +
            "desc distinct drop else end exists foreign from full group having if in index inner insert into is join " +
            "key left like limit not on or order outer primary references returning right rollback select set table " +
            "then transaction union unique update using values view when where with", ignoreCase: true),
        Set("bigint bit boolean char date datetime decimal float int integer json numeric real serial smallint text " +
            "time timestamp uuid varchar", ignoreCase: true),
        Set("true false null", ignoreCase: true),
        NoWords,
        DashComments: true, SlashComments: true, CallTitles: true);

    private static readonly Grammar Css = new(
        Set("@import @media @supports @keyframes @font-face !important"),
        NoWords, NoWords, NoWords,
        SlashComments: true, PropertyKeys: true, DashIdentifiers: true, CallTitles: false);

    private static readonly Grammar MarkupGrammar = new(NoWords, NoWords, NoWords, NoWords, Markup: true);

    private static Grammar? GrammarFor(string? language) =>
        language?.Trim().ToLowerInvariant() switch
        {
            "ts" or "typescript" or "tsx" or "mts" or "cts" or "js" or "javascript" or "jsx" or "mjs" or "cjs" or
                "node" => Script,
            "json" or "jsonc" or "json5" => Json,
            "cs" or "csharp" or "c#" => CSharp,
            "c" or "h" or "cpp" or "c++" or "cc" or "hpp" or "java" or "kotlin" or "kt" or "go" or "golang" or
                "rust" or "rs" or "swift" or "dart" or "scala" => CFamily,
            "py" or "python" => Python,
            "ps" or "ps1" or "psm1" or "powershell" or "pwsh" => PowerShell,
            "sh" or "bash" or "shell" or "zsh" => Shell,
            "yaml" or "yml" => Yaml,
            "toml" or "ini" => Toml,
            "sql" => Sql,
            "css" or "scss" or "less" => Css,
            "xml" or "html" or "xaml" or "svg" or "csproj" or "props" or "targets" => MarkupGrammar,
            _ => null,
        };

    /// <summary>Returns ordered, non-overlapping colored spans; uncolored text has no span.</summary>
    internal static IReadOnlyList<ChatCodeSpan> Classify(string code, string? language)
    {
        var spans = new List<ChatCodeSpan>();
        if (string.IsNullOrEmpty(code) || code.Length > MaxHighlightedLength || GrammarFor(language) is not { } grammar)
            return spans;
        if (grammar.Markup)
            ClassifyMarkup(code, spans);
        else
            ClassifyCode(code, grammar, spans);
        if (spans.Count > MaxHighlightedSpans)
            spans.Clear();
        return spans;
    }

    private static void ClassifyCode(string code, Grammar grammar, List<ChatCodeSpan> spans)
    {
        var titlePending = false;
        var i = 0;
        while (i < code.Length)
        {
            var c = code[i];
            var next = i + 1 < code.Length ? code[i + 1] : '\0';

            if (grammar.SlashComments && c == '/' && next == '/')
            {
                i = Add(spans, i, LineEnd(code, i), ChatCodeRole.Comment);
                continue;
            }
            if (grammar.SlashComments && c == '/' && next == '*')
            {
                i = Add(spans, i, IndexAfter(code, "*/", i + 2), ChatCodeRole.Comment);
                continue;
            }
            if (grammar.Cmdlets && c == '<' && next == '#')
            {
                i = Add(spans, i, IndexAfter(code, "#>", i + 2), ChatCodeRole.Comment);
                continue;
            }
            if (grammar.HashComments && c == '#' && (i == 0 || char.IsWhiteSpace(code[i - 1])))
            {
                i = Add(spans, i, LineEnd(code, i), ChatCodeRole.Comment);
                continue;
            }
            if (grammar.DashComments && c == '-' && next == '-')
            {
                i = Add(spans, i, LineEnd(code, i), ChatCodeRole.Comment);
                continue;
            }
            if (c is '"' or '\'' || (c == '`' && grammar.BacktickStrings))
            {
                // Plain YAML/TOML scalars contain apostrophes; C-family quotes beyond one
                // character are lifetimes or labels rather than char literals.
                if (c == '\'' && ((grammar == Yaml || grammar == Toml) && i > 0 && char.IsLetterOrDigit(code[i - 1]) ||
                    grammar == CFamily && !IsCharLiteral(code, i)))
                {
                    i++;
                    continue;
                }
                var end = StringEnd(code, i, grammar);
                var role = grammar.PropertyKeys && IsKeyPosition(code, i, grammar) && NextIsColon(code, end)
                    ? ChatCodeRole.Attribute
                    : ChatCodeRole.String;
                i = Add(spans, i, end, role);
                titlePending = false;
                continue;
            }
            if (char.IsDigit(c) || (c == '.' && char.IsDigit(next)))
            {
                if (i > 0 && IsIdentifierPart(code[i - 1], grammar))
                {
                    i++;
                    continue;
                }
                var end = i + 1;
                while (end < code.Length && (char.IsLetterOrDigit(code[end]) || code[end] is '.' or '_'))
                    end++;
                i = Add(spans, i, end, ChatCodeRole.Number);
                continue;
            }
            if (grammar.DollarVariables && c == '$' && (char.IsLetter(next) || next is '_' or '{'))
            {
                var end = i + 1;
                if (next == '{')
                    end = IndexAfter(code, "}", end);
                else
                    while (end < code.Length && (char.IsLetterOrDigit(code[end]) || code[end] is '_' or ':'))
                        end++;
                i = Add(spans, i, end, ChatCodeRole.Number);
                continue;
            }
            if (char.IsLetter(c) || c == '_' || (c == '$' && grammar.DollarIdentifiers) ||
                (c is '@' or '!' && grammar == Css && char.IsLetter(next)))
            {
                var end = i + 1;
                while (end < code.Length && IsIdentifierPart(code[end], grammar))
                    end++;
                if (grammar.Cmdlets)
                    while (end + 1 < code.Length && code[end] == '-' && char.IsLetter(code[end + 1]))
                    {
                        end += 2;
                        while (end < code.Length && IsIdentifierPart(code[end], grammar))
                            end++;
                    }
                var word = code[i..end];
                var role = Classify(code, i, end, word, grammar, titlePending);
                titlePending = grammar.TitleIntroducers.Contains(word);
                if (role is { } colored)
                    Add(spans, i, end, colored);
                i = end;
                continue;
            }
            if (grammar.AssignmentKeys && c == '[' && IsLineStart(code, i))
            {
                var end = LineEnd(code, i);
                i = Add(spans, i, end, ChatCodeRole.Title);
                continue;
            }
            if (!char.IsWhiteSpace(c) && c != '*')
                titlePending = false;
            i++;
        }
    }

    private static bool IsCharLiteral(string code, int start)
    {
        var limit = Math.Min(code.Length, start + (start + 1 < code.Length && code[start + 1] == '\\' ? 12 : 4));
        for (var i = start + 1; i < limit; i++)
            if (code[i] == '\'')
                return i > start + 1;
        return false;
    }

    private static ChatCodeRole? Classify(string code, int start, int end, string word, Grammar grammar,
        bool titlePending)
    {
        var member = start > 0 && code[start - 1] == '.';
        if (!member && grammar.PropertyKeys && word != "default" &&
            IsKeyPosition(code, start, grammar) && NextIsColon(code, end))
            return ChatCodeRole.Attribute;
        if (!member && grammar.Keywords.Contains(word))
            return ChatCodeRole.Keyword;
        if (!member && grammar.Literals.Contains(word))
            return ChatCodeRole.Number;
        if (!member && grammar.Types.Contains(word))
            return ChatCodeRole.Type;
        if (titlePending)
            return ChatCodeRole.Title;
        if (grammar.Cmdlets && word.Contains('-'))
            return ChatCodeRole.Type;
        var after = SkipInlineSpace(code, end);
        if (grammar.AssignmentKeys && IsLineStart(code, start) && after < code.Length && code[after] == '=')
            return ChatCodeRole.Attribute;
        if (grammar.CallTitles && after < code.Length && code[after] == '(')
            return ChatCodeRole.Title;
        return null;
    }

    private static void ClassifyMarkup(string code, List<ChatCodeSpan> spans)
    {
        var i = 0;
        while (i < code.Length)
        {
            if (string.CompareOrdinal(code, i, "<!--", 0, 4) == 0)
            {
                i = Add(spans, i, IndexAfter(code, "-->", i + 4), ChatCodeRole.Comment);
                continue;
            }
            if (code[i] != '<')
            {
                i++;
                continue;
            }
            i++;
            while (i < code.Length && code[i] is '/' or '?' or '!')
                i++;
            var tagEnd = code.IndexOf('>', i);
            if (tagEnd < 0)
                tagEnd = code.Length;
            var nameEnd = i;
            while (nameEnd < tagEnd && !char.IsWhiteSpace(code[nameEnd]) && code[nameEnd] is not '/' and not '>')
                nameEnd++;
            i = Add(spans, i, nameEnd, ChatCodeRole.Attribute);
            while (i < tagEnd)
            {
                var c = code[i];
                if (c is '"' or '\'')
                {
                    var close = code.IndexOf(c, i + 1);
                    var end = close < 0 || close > tagEnd ? tagEnd : close + 1;
                    i = Add(spans, i, end, ChatCodeRole.String);
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    var end = i + 1;
                    while (end < tagEnd && (char.IsLetterOrDigit(code[end]) || code[end] is '_' or '-' or ':' or '.'))
                        end++;
                    i = Add(spans, i, end, ChatCodeRole.Attribute);
                    continue;
                }
                i++;
            }
            i = Math.Min(code.Length, tagEnd + 1);
        }
    }

    private static int Add(List<ChatCodeSpan> spans, int start, int end, ChatCodeRole role)
    {
        if (end > start)
            spans.Add(new(start, end - start, role));
        return end;
    }

    private static bool IsIdentifierPart(char c, Grammar grammar) =>
        char.IsLetterOrDigit(c) || c == '_' ||
        (c == '$' && grammar.DollarIdentifiers) ||
        (c == '-' && grammar.DashIdentifiers);

    private static int LineEnd(string code, int start)
    {
        var end = code.IndexOf('\n', start);
        if (end < 0)
            return code.Length;
        return end > start && code[end - 1] == '\r' ? end - 1 : end;
    }

    private static int IndexAfter(string code, string terminator, int start)
    {
        var end = code.IndexOf(terminator, start, StringComparison.Ordinal);
        return end < 0 ? code.Length : end + terminator.Length;
    }

    private static int StringEnd(string code, int start, Grammar grammar)
    {
        var quote = code[start];
        if (grammar.TripleQuotes && start + 2 < code.Length && code[start + 1] == quote && code[start + 2] == quote)
            return IndexAfter(code, new string(quote, 3), start + 3);
        var multiline = quote == '`' || grammar.MultilineStrings;
        for (var i = start + 1; i < code.Length; i++)
        {
            var c = code[i];
            if (c == '\\' && !grammar.Cmdlets)
            {
                i++;
                continue;
            }
            if (c == quote)
                return i + 1;
            if (c == '\n' && !multiline)
                return i > start + 1 && code[i - 1] == '\r' ? i - 1 : i;
        }
        return code.Length;
    }

    private static int SkipInlineSpace(string code, int index)
    {
        while (index < code.Length && code[index] is ' ' or '\t')
            index++;
        return index;
    }

    private static bool NextIsColon(string code, int end)
    {
        var index = SkipInlineSpace(code, end);
        if (index < code.Length && code[index] == '?')
            index++;
        return index < code.Length && code[index] == ':' &&
            (index + 1 >= code.Length || code[index + 1] != ':');
    }

    private static bool IsLineStart(string code, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (code[i] == '\n')
                return true;
            if (code[i] is not ' ' and not '\t')
                return false;
        }
        return true;
    }

    // Object, type-literal and mapping keys follow an opening brace, a separator or a line start,
    // so ternaries and parameter annotations do not read as keys.
    private static bool IsKeyPosition(string code, int index, Grammar grammar)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            var c = code[i];
            if (c is ' ' or '\t' or '\r')
                continue;
            return c is '\n' or '{' or ',' or ';' || (c == '-' && grammar == Yaml);
        }
        return true;
    }
}
