using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenClawTray.Infrastructure.Controls;

/// <summary>
/// Transforms input text and cursor position. Used in formatter pipelines.
/// </summary>
/// <param name="Input">The input text.</param>
/// <param name="CursorPos">The cursor position in the input.</param>
public readonly record struct FormatResult(string Output, int CursorPos);

/// <summary>
/// Base class for input formatters that transform text as the user types.
/// </summary>
public abstract class InputFormatter
{
    /// <summary>
    /// Formats the input and adjusts cursor position.
    /// </summary>
    public abstract FormatResult Format(string input, int cursorPos);

    /// <summary>
    /// Parses a formatted value back to a raw model value.
    /// Default implementation returns the formatted value as-is.
    /// </summary>
    public virtual string Parse(string formatted) => formatted;

    // ════════════════════════════════════════════════════════════════
    //  Built-in formatters
    // ════════════════════════════════════════════════════════════════

    /// <summary>US phone format: (555) 123-4567</summary>
    public static InputFormatter PhoneUS => new PhoneUSFormatter();

    /// <summary>International phone with configurable country code.</summary>
    public static InputFormatter PhoneIntl(string countryCode = "+1") => new PhoneIntlFormatter(countryCode);

    /// <summary>Currency format: $1,234.56</summary>
    public static InputFormatter Currency(string symbol = "$") => new CurrencyFormatter(symbol);

    /// <summary>Force uppercase.</summary>
    public static InputFormatter UpperCase => new UpperCaseFormatter();

    /// <summary>Force lowercase.</summary>
    public static InputFormatter LowerCase => new LowerCaseFormatter();

    /// <summary>Title case (first letter of each word uppercase).</summary>
    public static InputFormatter TitleCase => new TitleCaseFormatter();

    /// <summary>Trim leading and trailing whitespace.</summary>
    public static InputFormatter TrimWhitespace => new TrimWhitespaceFormatter();

    /// <summary>Truncate at n characters.</summary>
    public static InputFormatter MaxLength(int n) => new MaxLengthFormatter(n);

    /// <summary>Only allow characters matching the regex.</summary>
    public static InputFormatter AllowOnly(string regex) => new AllowOnlyFormatter(regex);

    /// <summary>Remove characters matching the regex.</summary>
    public static InputFormatter DenyOnly(string regex) => new DenyOnlyFormatter(regex);

    /// <summary>Custom formatter with format and parse functions.</summary>
    public static InputFormatter Custom(Func<string, string> format, Func<string, string> parse)
        => new CustomFormatter(format, parse);
}

/// <summary>
/// Chains multiple formatters into a pipeline.
/// </summary>
public sealed class FormatterPipeline
{
    private readonly InputFormatter[] _formatters;

    public FormatterPipeline(params InputFormatter[] formatters)
    {
        _formatters = formatters;
    }

    /// <summary>
    /// Runs all formatters in sequence.
    /// </summary>
    public FormatResult Format(string input, int cursorPos)
    {
        var result = new FormatResult(input, cursorPos);
        foreach (var formatter in _formatters)
        {
            result = formatter.Format(result.Output, result.CursorPos);
        }
        return result;
    }

    /// <summary>
    /// Parses a formatted value through all formatters in reverse order.
    /// </summary>
    public string Parse(string formatted)
    {
        var result = formatted;
        for (int i = _formatters.Length - 1; i >= 0; i--)
        {
            result = _formatters[i].Parse(result);
        }
        return result;
    }
}

// ════════════════════════════════════════════════════════════════
//  Formatter implementations
// ════════════════════════════════════════════════════════════════

internal sealed class PhoneUSFormatter : InputFormatter
{
    public override FormatResult Format(string input, int cursorPos)
    {
        var digits = new string(input.Where(char.IsDigit).ToArray());
        if (digits.Length > 10) digits = digits[..10];

        var formatted = digits.Length switch
        {
            >= 7 => $"({digits[..3]}) {digits[3..6]}-{digits[6..]}",
            >= 4 => $"({digits[..3]}) {digits[3..]}",
            >= 1 => $"({digits}",
            _ => ""
        };

        return new FormatResult(formatted, formatted.Length);
    }

    public override string Parse(string formatted) =>
        new string(formatted.Where(char.IsDigit).ToArray());
}

internal sealed class PhoneIntlFormatter(string countryCode) : InputFormatter
{
    public override FormatResult Format(string input, int cursorPos)
    {
        var digits = new string(input.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return new("", 0);
        var formatted = $"{countryCode} {digits}";
        return new(formatted, formatted.Length);
    }

    public override string Parse(string formatted)
    {
        var stripped = formatted.Replace(countryCode, "").Trim();
        return new string(stripped.Where(char.IsDigit).ToArray());
    }
}

internal sealed class CurrencyFormatter(string symbol) : InputFormatter
{
    public override FormatResult Format(string input, int cursorPos)
    {
        // Extract digits and at most one decimal point
        var clean = new string(input.Where(c => char.IsDigit(c) || c == '.').ToArray());

        // Handle multiple decimal points
        var parts = clean.Split('.');
        var intPart = parts[0];
        var decPart = parts.Length > 1 ? parts[1] : null;

        // Limit decimal to 2 places
        if (decPart is not null && decPart.Length > 2)
            decPart = decPart[..2];

        // Add thousands separator
        if (long.TryParse(intPart, out var intVal))
            intPart = intVal.ToString("N0", CultureInfo.InvariantCulture);

        var formatted = decPart is not null
            ? $"{symbol}{intPart}.{decPart}"
            : $"{symbol}{intPart}";

        return new(formatted, formatted.Length);
    }

    public override string Parse(string formatted)
    {
        var clean = formatted.Replace(symbol, "").Replace(",", "").Trim();
        return clean;
    }
}

internal sealed class UpperCaseFormatter : InputFormatter
{
    public override FormatResult Format(string input, int cursorPos) =>
        new(input.ToUpperInvariant(), cursorPos);

    public override string Parse(string formatted) => formatted;
}

internal sealed class LowerCaseFormatter : InputFormatter
{
    public override FormatResult Format(string input, int cursorPos) =>
        new(input.ToLowerInvariant(), cursorPos);

    public override string Parse(string formatted) => formatted;
}

internal sealed class TitleCaseFormatter : InputFormatter
{
    public override FormatResult Format(string input, int cursorPos)
    {
        var ti = CultureInfo.InvariantCulture.TextInfo;
        return new(ti.ToTitleCase(input.ToLowerInvariant()), cursorPos);
    }

    public override string Parse(string formatted) => formatted;
}

internal sealed class TrimWhitespaceFormatter : InputFormatter
{
    public override FormatResult Format(string input, int cursorPos)
    {
        var trimmed = input.Trim();
        var newCursor = Math.Min(cursorPos, trimmed.Length);
        return new(trimmed, newCursor);
    }

    public override string Parse(string formatted) => formatted.Trim();
}

internal sealed class MaxLengthFormatter(int max) : InputFormatter
{
    public override FormatResult Format(string input, int cursorPos)
    {
        if (input.Length <= max) return new(input, cursorPos);
        var truncated = input[..max];
        return new(truncated, Math.Min(cursorPos, max));
    }

    public override string Parse(string formatted) => formatted;
}

internal sealed class AllowOnlyFormatter(string pattern) : InputFormatter
{
    private readonly Regex _regex = new(pattern, RegexOptions.Compiled);

    public override FormatResult Format(string input, int cursorPos)
    {
        var filtered = new string(input.Where(c => _regex.IsMatch(c.ToString())).ToArray());
        var newCursor = Math.Min(cursorPos, filtered.Length);
        return new(filtered, newCursor);
    }

    public override string Parse(string formatted) => formatted;
}

internal sealed class DenyOnlyFormatter(string pattern) : InputFormatter
{
    private readonly Regex _regex = new(pattern, RegexOptions.Compiled);

    public override FormatResult Format(string input, int cursorPos)
    {
        var filtered = new string(input.Where(c => !_regex.IsMatch(c.ToString())).ToArray());
        var newCursor = Math.Min(cursorPos, filtered.Length);
        return new(filtered, newCursor);
    }

    public override string Parse(string formatted) => formatted;
}

internal sealed class CustomFormatter(Func<string, string> format, Func<string, string> parse) : InputFormatter
{
    public override FormatResult Format(string input, int cursorPos)
    {
        var formatted = format(input);
        return new(formatted, Math.Min(cursorPos, formatted.Length));
    }

    public override string Parse(string formatted) => parse(formatted);
}
