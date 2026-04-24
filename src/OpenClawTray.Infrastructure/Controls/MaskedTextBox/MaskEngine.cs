namespace OpenClawTray.Infrastructure.Controls;

/// <summary>
/// Mask token types for the mask engine.
/// </summary>
internal enum MaskTokenType
{
    /// <summary>Required digit (0-9).</summary>
    RequiredDigit,
    /// <summary>Optional digit (0-9).</summary>
    OptionalDigit,
    /// <summary>Required letter (a-z, A-Z).</summary>
    RequiredLetter,
    /// <summary>Optional letter (a-z, A-Z).</summary>
    OptionalLetter,
    /// <summary>Required alphanumeric.</summary>
    RequiredAlphanumeric,
    /// <summary>Literal character (auto-inserted).</summary>
    Literal
}

/// <summary>
/// A parsed mask token.
/// </summary>
internal readonly record struct MaskToken(MaskTokenType Type, char LiteralChar = '\0');

/// <summary>
/// Engine that parses mask patterns and applies them to input strings.
///
/// Mask tokens:
///   0 = required digit
///   9 = optional digit
///   A = required letter
///   a = optional letter
///   * = required alphanumeric
///   All others = literal (auto-insert)
/// </summary>
public sealed class MaskEngine
{
    private readonly MaskToken[] _tokens;
    private readonly string _mask;

    public MaskEngine(string mask)
    {
        _mask = mask;
        _tokens = Parse(mask);
    }

    /// <summary>The original mask pattern.</summary>
    public string Mask => _mask;

    /// <summary>Number of tokens in the mask.</summary>
    public int Length => _tokens.Length;

    /// <summary>
    /// Applies the mask to raw input, producing a formatted output.
    /// Literal characters are auto-inserted at the correct positions.
    /// </summary>
    public string Apply(string input, char placeholder = '_')
    {
        var result = new char[_tokens.Length];
        int inputIndex = 0;

        for (int i = 0; i < _tokens.Length; i++)
        {
            var token = _tokens[i];
            if (token.Type == MaskTokenType.Literal)
            {
                result[i] = token.LiteralChar;
                // Skip over literal in input if it matches
                if (inputIndex < input.Length && input[inputIndex] == token.LiteralChar)
                    inputIndex++;
                continue;
            }

            if (inputIndex < input.Length)
            {
                char c = input[inputIndex];
                if (Accepts(token.Type, c))
                {
                    result[i] = c;
                    inputIndex++;
                    continue;
                }
                else if (IsOptional(token.Type))
                {
                    result[i] = placeholder;
                    continue;
                }
                else
                {
                    // Required slot, invalid char — skip the char and try to fill with placeholder
                    result[i] = placeholder;
                    inputIndex++; // skip invalid char
                    continue;
                }
            }
            else
            {
                result[i] = placeholder;
            }
        }

        return new string(result);
    }

    /// <summary>
    /// Extracts the raw value from a formatted string by stripping literals and placeholders.
    /// </summary>
    public string GetRawValue(string formatted, char placeholder = '_')
    {
        var result = new List<char>();
        for (int i = 0; i < Math.Min(formatted.Length, _tokens.Length); i++)
        {
            if (_tokens[i].Type == MaskTokenType.Literal)
                continue;
            if (formatted[i] == placeholder)
                continue;
            result.Add(formatted[i]);
        }
        return new string(result.ToArray());
    }

    /// <summary>
    /// Returns true if all required slots are filled.
    /// </summary>
    public bool IsComplete(string formatted, char placeholder = '_')
    {
        for (int i = 0; i < Math.Min(formatted.Length, _tokens.Length); i++)
        {
            var token = _tokens[i];
            if (token.Type == MaskTokenType.Literal)
                continue;
            if (IsRequired(token.Type) && (i >= formatted.Length || formatted[i] == placeholder))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Returns true if the position is a literal position.
    /// </summary>
    public bool IsLiteral(int position) =>
        position >= 0 && position < _tokens.Length && _tokens[position].Type == MaskTokenType.Literal;

    /// <summary>
    /// Finds the next non-literal position at or after the given position.
    /// </summary>
    public int NextInputPosition(int position)
    {
        for (int i = position; i < _tokens.Length; i++)
            if (_tokens[i].Type != MaskTokenType.Literal)
                return i;
        return _tokens.Length;
    }

    /// <summary>
    /// Finds the previous non-literal position at or before the given position.
    /// </summary>
    public int PreviousInputPosition(int position)
    {
        for (int i = position; i >= 0; i--)
            if (_tokens[i].Type != MaskTokenType.Literal)
                return i;
        return -1;
    }

    // ════════════════════════════════════════════════════════════════
    //  Internals
    // ════════════════════════════════════════════════════════════════

    private static MaskToken[] Parse(string mask)
    {
        var tokens = new MaskToken[mask.Length];
        for (int i = 0; i < mask.Length; i++)
        {
            tokens[i] = mask[i] switch
            {
                '0' => new MaskToken(MaskTokenType.RequiredDigit),
                '9' => new MaskToken(MaskTokenType.OptionalDigit),
                'A' => new MaskToken(MaskTokenType.RequiredLetter),
                'a' => new MaskToken(MaskTokenType.OptionalLetter),
                '*' => new MaskToken(MaskTokenType.RequiredAlphanumeric),
                char c => new MaskToken(MaskTokenType.Literal, c),
            };
        }
        return tokens;
    }

    private static bool Accepts(MaskTokenType type, char c) => type switch
    {
        MaskTokenType.RequiredDigit => char.IsDigit(c),
        MaskTokenType.OptionalDigit => char.IsDigit(c),
        MaskTokenType.RequiredLetter => char.IsLetter(c),
        MaskTokenType.OptionalLetter => char.IsLetter(c),
        MaskTokenType.RequiredAlphanumeric => char.IsLetterOrDigit(c),
        _ => false,
    };

    private static bool IsOptional(MaskTokenType type) =>
        type is MaskTokenType.OptionalDigit or MaskTokenType.OptionalLetter;

    private static bool IsRequired(MaskTokenType type) =>
        type is MaskTokenType.RequiredDigit or MaskTokenType.RequiredLetter or MaskTokenType.RequiredAlphanumeric;
}

/// <summary>
/// Common mask presets for standard formats.
/// </summary>
public static class MaskPreset
{
    /// <summary>US phone: (000) 000-0000</summary>
    public const string PhoneUS = "(000) 000-0000";
    /// <summary>SSN: 000-00-0000</summary>
    public const string SSN = "000-00-0000";
    /// <summary>ZIP code: 00000</summary>
    public const string ZipCode = "00000";
    /// <summary>ZIP+4: 00000-0000</summary>
    public const string ZipCodePlus4 = "00000-0000";
    /// <summary>Credit card: 0000 0000 0000 0000</summary>
    public const string CreditCard = "0000 0000 0000 0000";
    /// <summary>Date: 00/00/0000</summary>
    public const string Date = "00/00/0000";
    /// <summary>Time: 00:00</summary>
    public const string Time = "00:00";
    /// <summary>IPv4: 099.099.099.099</summary>
    public const string IPv4 = "099.099.099.099";
}
