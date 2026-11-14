using System.Globalization;

namespace OpenClaw.Shared.Themes;

/// <summary>
/// An 8-bit sRGB color with straight (non-premultiplied) alpha.
/// The theme model stores every resolved token in this form so WinUI and tests share one value type.
/// <example><code>
/// var accent = ThemeColor.FromHex("#5ab6d8");
/// var subtle = accent.WithAlpha(0.12);
/// </code></example>
/// </summary>
public readonly record struct ThemeColor(byte A, byte R, byte G, byte B)
{
    public static ThemeColor Transparent { get; } = new(0, 0, 0, 0);
    public static ThemeColor Black { get; } = new(255, 0, 0, 0);
    public static ThemeColor White { get; } = new(255, 255, 255, 255);

    /// <summary>Parses <c>#rgb</c>, <c>#rgba</c>, <c>#rrggbb</c> or <c>#rrggbbaa</c>.</summary>
    public static ThemeColor FromHex(string hex) =>
        TryFromHex(hex, out var color) ? color : throw new FormatException($"'{hex}' is not a hex color.");

    public static bool TryFromHex(string? hex, out ThemeColor color)
    {
        color = default;
        if (string.IsNullOrEmpty(hex) || hex[0] != '#')
            return false;
        var digits = hex.AsSpan(1);
        if (digits.Length is not (3 or 4 or 6 or 8))
            return false;
        foreach (var c in digits)
        {
            if (!char.IsAsciiHexDigit(c))
                return false;
        }

        var value = new byte[digits.Length is 3 or 4 ? digits.Length : digits.Length / 2];
        for (var i = 0; i < value.Length; i++)
        {
            value[i] = digits.Length is 3 or 4
                ? (byte)(int.Parse(digits.Slice(i, 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture) * 17)
                : byte.Parse(digits.Slice(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        color = new ThemeColor(value.Length == 4 ? value[3] : (byte)255, value[0], value[1], value[2]);
        return true;
    }

    /// <summary>Lowercase <c>#rrggbb</c> when opaque, otherwise <c>#rrggbbaa</c>.</summary>
    public string ToHex() => A == 255
        ? $"#{R:x2}{G:x2}{B:x2}"
        : $"#{R:x2}{G:x2}{B:x2}{A:x2}";

    public override string ToString() => ToHex();

    /// <summary>Scales alpha by <paramref name="opacity"/> (0..1).</summary>
    public ThemeColor WithAlpha(double opacity) =>
        this with { A = ToByte(A / 255d * Math.Clamp(opacity, 0, 1)) };

    /// <summary>
    /// CSS <c>color-mix(in srgb, this p, other 1-p)</c>: premultiplied interpolation in gamma-encoded sRGB.
    /// </summary>
    public ThemeColor Mix(ThemeColor other, double weightOfThis)
    {
        var p = Math.Clamp(weightOfThis, 0, 1);
        double a1 = A / 255d, a2 = other.A / 255d;
        var alpha = a1 * p + a2 * (1 - p);
        if (alpha <= 0)
            return Transparent;
        double Channel(byte c1, byte c2) => (c1 / 255d * a1 * p + c2 / 255d * a2 * (1 - p)) / alpha;
        return new ThemeColor(ToByte(alpha), ToByte(Channel(R, other.R)), ToByte(Channel(G, other.G)), ToByte(Channel(B, other.B)));
    }

    /// <summary>Composites this color over an opaque <paramref name="background"/>.</summary>
    public ThemeColor Over(ThemeColor background)
    {
        var a = A / 255d;
        byte Channel(byte fg, byte bg) => ToByte((fg * a + bg * (1 - a)) / 255d);
        return new ThemeColor(255, Channel(R, background.R), Channel(G, background.G), Channel(B, background.B));
    }

    /// <summary>WCAG 2.x relative luminance of the color's RGB channels (alpha ignored).</summary>
    public double RelativeLuminance
    {
        get
        {
            static double Linear(byte c)
            {
                var v = c / 255d;
                return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(R) + 0.7152 * Linear(G) + 0.0722 * Linear(B);
        }
    }

    internal static byte ToByte(double unit) => (byte)Math.Round(Math.Clamp(unit, 0, 1) * 255, MidpointRounding.AwayFromZero);
}
