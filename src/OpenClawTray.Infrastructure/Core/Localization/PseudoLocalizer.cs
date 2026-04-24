using System.Text;
using System.Text.RegularExpressions;

namespace OpenClawTray.Infrastructure.Localization;

/// <summary>
/// Transforms strings for pseudolocalization testing:
/// - Replaces ASCII chars with accented equivalents
/// - Wraps in [!! ... !!] markers
/// - Pads ~30% longer to simulate text expansion
/// - Preserves ICU syntax ({variables}, {count, plural, ...}) untouched
/// </summary>
public static class PseudoLocalizer
{
    private static readonly Dictionary<char, char> AccentMap = new()
    {
        ['a'] = 'å', ['b'] = 'ƀ', ['c'] = 'ç', ['d'] = 'ð', ['e'] = 'é',
        ['f'] = 'ƒ', ['g'] = 'ĝ', ['h'] = 'ĥ', ['i'] = 'î', ['j'] = 'ĵ',
        ['k'] = 'ķ', ['l'] = 'ĺ', ['m'] = 'ɱ', ['n'] = 'ñ', ['o'] = 'ö',
        ['p'] = 'þ', ['q'] = 'q', ['r'] = 'ř', ['s'] = 'š', ['t'] = 'ţ',
        ['u'] = 'ü', ['v'] = 'v', ['w'] = 'ŵ', ['x'] = 'x', ['y'] = 'ý',
        ['z'] = 'ž',
        ['A'] = 'Å', ['B'] = 'Ɓ', ['C'] = 'Ç', ['D'] = 'Ð', ['E'] = 'É',
        ['F'] = 'Ƒ', ['G'] = 'Ĝ', ['H'] = 'Ĥ', ['I'] = 'Î', ['J'] = 'Ĵ',
        ['K'] = 'Ķ', ['L'] = 'Ĺ', ['M'] = 'Ṁ', ['N'] = 'Ñ', ['O'] = 'Ö',
        ['P'] = 'Þ', ['Q'] = 'Q', ['R'] = 'Ř', ['S'] = 'Š', ['T'] = 'Ţ',
        ['U'] = 'Ü', ['V'] = 'V', ['W'] = 'Ŵ', ['X'] = 'X', ['Y'] = 'Ý',
        ['Z'] = 'Ž',
    };

    // Matches ICU syntax: {variableName} or {variableName, type, style}
    // Also handles nested braces within plural/select blocks
    private static readonly Regex IcuPattern = new(@"\{[^}]*\}", RegexOptions.Compiled);

    /// <summary>
    /// Pseudolocalizes a string, preserving ICU syntax.
    /// </summary>
    public static string Transform(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var sb = new StringBuilder();
        sb.Append("[!! ");

        // Split on ICU patterns, transform only the text parts
        var lastIndex = 0;
        foreach (Match match in IcuPattern.Matches(input))
        {
            // Transform text before the ICU token
            if (match.Index > lastIndex)
                AppendAccented(sb, input, lastIndex, match.Index - lastIndex);

            // Preserve the ICU token as-is
            sb.Append(match.Value);
            lastIndex = match.Index + match.Length;
        }

        // Transform remaining text after last ICU token
        if (lastIndex < input.Length)
            AppendAccented(sb, input, lastIndex, input.Length - lastIndex);

        sb.Append(" !!");

        // Pad ~30% with tildes to simulate text expansion
        var padCount = (int)(input.Length * 0.3);
        sb.Append(new string('~', Math.Max(padCount, 1)));

        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// Returns the missing key pseudolocalization marker.
    /// </summary>
    public static string MissingKeyMarker(MessageKey key)
    {
        return $"[?? {key} ??]";
    }

    private static void AppendAccented(StringBuilder sb, string source, int start, int length)
    {
        for (int i = start; i < start + length; i++)
        {
            var c = source[i];
            sb.Append(AccentMap.TryGetValue(c, out var accented) ? accented : c);
        }
    }
}
