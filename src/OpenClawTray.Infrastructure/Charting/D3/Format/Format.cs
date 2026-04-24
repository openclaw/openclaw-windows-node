// Port of d3-format — ISC License, Copyright 2010-2023 Mike Bostock

using System.Globalization;

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// Locale-independent number formatting matching D3's format specifier syntax.
/// Direct port of d3.format() — supports specifiers like ".2f", ".0%", ",.0f", ".2s", etc.
/// </summary>
public static class D3Format
{
    private static readonly string[] SIPrefixes =
        ["y","z","a","f","p","n","µ","m","","k","M","G","T","P","E","Z","Y"];

    /// <summary>
    /// Returns a formatting function for the given specifier string.
    /// Supported types: f (fixed), e (exponent), g (general), r (rounded), s (SI-prefix),
    /// % (percent), d (integer), b (binary), o (octal), x/X (hex), c (character).
    /// </summary>
    public static Func<double, string> Format(string specifier)
    {
        var spec = ParseSpecifier(specifier);
        return v => FormatValue(v, spec);
    }

    /// <summary>Formats a value using the given specifier string.</summary>
    public static string FormatValue(double value, string specifier)
    {
        var spec = ParseSpecifier(specifier);
        return FormatValue(value, spec);
    }

    /// <summary>
    /// Returns a SI-prefix format function that auto-selects the prefix.
    /// </summary>
    public static Func<double, string> FormatPrefix(string specifier, double reference)
    {
        var spec = ParseSpecifier(specifier);
        int i = Math.Max(-8, Math.Min(8, (int)Math.Floor(Math.Log10(Math.Abs(reference)) / 3))) * 3;
        double k = Math.Pow(10, -i);
        string prefix = SIPrefixes[8 + i / 3];

        return v =>
        {
            spec.Type = 'f';
            string result = FormatValue(v * k, spec);
            return result + prefix;
        };
    }

    /// <summary>
    /// Returns the decimal precision needed for fixed-point notation of the given step.
    /// </summary>
    public static int PrecisionFixed(double step)
    {
        return Math.Max(0, -(int)Math.Floor(Math.Log10(Math.Abs(step)) + 1e-15));
    }

    /// <summary>
    /// Returns the decimal precision needed for significant-digit notation of the given step
    /// relative to the given max value.
    /// </summary>
    public static int PrecisionRound(double step, double max)
    {
        step = Math.Abs(step);
        max = Math.Abs(max) - Math.Floor(Math.Abs(max));
        return Math.Max(0, (int)(-Math.Floor(Math.Log10(step)) + Math.Floor(Math.Log10(max))));
    }

    /// <summary>
    /// Returns the decimal precision needed for the given step and value.
    /// </summary>
    public static int PrecisionPrefix(double step, double value)
    {
        return Math.Max(0, 3 * Math.Max(-8, Math.Min(8, (int)Math.Floor(Math.Log10(Math.Abs(value)) / 3)))
            - (int)Math.Floor(Math.Log10(Math.Abs(step))));
    }

    private static string FormatValue(double value, FormatSpec spec)
    {
        bool negative = value < 0 || (1.0 / value < 0); // detect -0
        if (negative) value = -value;

        string body = spec.Type switch
        {
            'f' => value.ToString($"F{spec.Precision ?? 6}", CultureInfo.InvariantCulture),
            'e' => value.ToString($"E{spec.Precision ?? 6}", CultureInfo.InvariantCulture).ToLowerInvariant(),
            'g' => FormatGeneral(value, spec.Precision ?? 6),
            'd' => ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture),
            '%' => (value * 100).ToString($"F{spec.Precision ?? 6}", CultureInfo.InvariantCulture) + "%",
            's' => FormatSI(value, spec.Precision ?? 6),
            'r' => FormatRounded(value, spec.Precision ?? 6),
            'b' => Convert.ToString((long)Math.Round(value), 2),
            'o' => Convert.ToString((long)Math.Round(value), 8),
            'x' => Convert.ToString((long)Math.Round(value), 16),
            'X' => Convert.ToString((long)Math.Round(value), 16).ToUpperInvariant(),
            'c' => ((char)(int)value).ToString(),
            'n' => FormatGrouped(value, spec.Precision ?? 6),
            _ => value.ToString(CultureInfo.InvariantCulture),
        };

        // Apply comma grouping for ',' flag
        if (spec.Comma && spec.Type != 'n')
            body = ApplyGrouping(body);

        // Sign
        string sign = negative ? (spec.Sign == '(' ? "(" : "-")
            : spec.Sign == '+' ? "+"
            : spec.Sign == ' ' ? " "
            : "";

        string suffix = negative && spec.Sign == '(' ? ")" : "";

        // Padding
        int len = sign.Length + body.Length + suffix.Length;
        string pad = "";
        if (spec.Width > len)
        {
            pad = new string(spec.Zero ? '0' : ' ', spec.Width - len);
        }

        if (spec.Zero)
            body = sign + pad + body + suffix;
        else if (spec.Align == '>')
            body = pad + sign + body + suffix;
        else if (spec.Align == '<')
            body = sign + body + suffix + pad;
        else if (spec.Align == '^')
        {
            int half = (spec.Width - len) / 2;
            body = new string(' ', half) + sign + body + suffix + new string(' ', spec.Width - len - half);
        }
        else
            body = sign + body + suffix;

        return body;
    }

    private static string FormatGeneral(double value, int precision)
    {
        if (precision <= 0) precision = 1;
        string result = value.ToString($"G{precision}", CultureInfo.InvariantCulture);
        return result.ToLowerInvariant();
    }

    private static string FormatRounded(double value, int precision)
    {
        if (value == 0) return "0";
        if (precision <= 0) precision = 1;
        double factor = Math.Pow(10, precision - 1 - (int)Math.Floor(Math.Log10(Math.Abs(value))));
        if (double.IsInfinity(factor)) return value.ToString(CultureInfo.InvariantCulture);
        return (Math.Round(value * factor) / factor).ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatSI(double value, int precision)
    {
        if (value == 0) return "0";
        int i = Math.Max(-8, Math.Min(8, (int)Math.Floor(Math.Log10(Math.Abs(value)) / 3))) * 3;
        double k = Math.Pow(10, -i);
        string prefix = SIPrefixes[8 + i / 3];
        string num = FormatRounded(value * k, precision);
        return num + prefix;
    }

    private static string FormatGrouped(double value, int precision)
    {
        string body = value.ToString($"F{precision}", CultureInfo.InvariantCulture);
        return ApplyGrouping(body);
    }

    private static string ApplyGrouping(string body)
    {
        int dotIndex = body.IndexOf('.');
        string intPart = dotIndex >= 0 ? body[..dotIndex] : body;
        string fracPart = dotIndex >= 0 ? body[dotIndex..] : "";

        // Group the integer part with commas
        bool neg = intPart.StartsWith('-');
        if (neg) intPart = intPart[1..];

        var grouped = new global::System.Text.StringBuilder();
        for (int i = 0; i < intPart.Length; i++)
        {
            if (i > 0 && (intPart.Length - i) % 3 == 0)
                grouped.Append(',');
            grouped.Append(intPart[i]);
        }

        return (neg ? "-" : "") + grouped + fracPart;
    }

    private static FormatSpec ParseSpecifier(string s)
    {
        var spec = new FormatSpec();
        int i = 0;

        // Fill and align
        if (i < s.Length - 1 && (s[i + 1] == '<' || s[i + 1] == '>' || s[i + 1] == '^'))
        {
            spec.Fill = s[i];
            spec.Align = s[i + 1];
            i += 2;
        }
        else if (i < s.Length && (s[i] == '<' || s[i] == '>' || s[i] == '^'))
        {
            spec.Align = s[i];
            i++;
        }

        // Sign
        if (i < s.Length && (s[i] == '+' || s[i] == '-' || s[i] == ' ' || s[i] == '('))
        {
            spec.Sign = s[i];
            i++;
        }

        // Symbol ($ or #)
        if (i < s.Length && s[i] == '$') { spec.Symbol = '$'; i++; }
        else if (i < s.Length && s[i] == '#') { spec.Symbol = '#'; i++; }

        // Zero fill
        if (i < s.Length && s[i] == '0') { spec.Zero = true; spec.Align = '='; i++; }

        // Width
        while (i < s.Length && char.IsDigit(s[i]))
        {
            spec.Width = spec.Width * 10 + (s[i] - '0');
            i++;
        }

        // Comma grouping
        if (i < s.Length && s[i] == ',') { spec.Comma = true; i++; }

        // Precision
        if (i < s.Length && s[i] == '.')
        {
            i++;
            int p = 0;
            while (i < s.Length && char.IsDigit(s[i]))
            {
                p = p * 10 + (s[i] - '0');
                i++;
            }
            spec.Precision = p;
        }

        // Trim
        if (i < s.Length && s[i] == '~') { spec.Trim = true; i++; }

        // Type
        if (i < s.Length) spec.Type = s[i];

        return spec;
    }

    private class FormatSpec
    {
        public char Fill = ' ';
        public char Align = '>';
        public char Sign = '-';
        public char Symbol = '\0';
        public bool Zero;
        public int Width;
        public bool Comma;
        public int? Precision;
        public bool Trim;
        public char Type = '\0';
    }
}
