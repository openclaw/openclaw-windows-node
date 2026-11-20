// Resolves the CSS paint values used by the vendored lobster artwork into concrete sRGB colours: hex, rgb()/rgba(),
// a few named colours, var(--x[, fallback]) with recursion, and color-mix(in srgb, ...).
// OpenClaw 4cffd43ee043275f6a4c693aaacee8d8e5a61288: ui/src/styles/lobster-pet.css and
// ui/src/components/lobster-pet-artwork.tsx (MIT; see THIRD_PARTY_NOTICES.md).
//
// Usage:
//   var vars = new Dictionary<string, string> { ["--lob-shell"] = "#ff4f40" };
//   LobsterRgba? tail = LobsterPaint.Resolve("color-mix(in srgb, var(--lob-shell) 78%, #10181f)", vars);
//   // null means "none"/"transparent": paint nothing.
using System.Globalization;

namespace OpenClawTray.Chat.LobsterVisits;

/// <summary>A straight (non-premultiplied) sRGB colour, 0-255 per channel.</summary>
internal readonly record struct LobsterRgba(byte R, byte G, byte B, byte A = 255)
{
    public override string ToString() => A == 255 ? $"#{R:x2}{G:x2}{B:x2}" : $"#{R:x2}{G:x2}{B:x2}{A:x2}";
}

internal static class LobsterPaint
{
    private const int MaxDepth = 16;

    private static readonly Dictionary<string, LobsterRgba> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = new(255, 255, 255),
        ["black"] = new(0, 0, 0),
    };

    /// <summary>
    /// Resolves <paramref name="value"/>. Returns null for none, transparent, an unresolvable var, or an unknown value.
    /// <paramref name="currentColor"/> answers <c>currentColor</c>.
    /// </summary>
    internal static LobsterRgba? Resolve(
        string? value,
        IReadOnlyDictionary<string, string> vars,
        LobsterRgba? currentColor = null) => Resolve(value, vars, currentColor, 0);

    private static LobsterRgba? Resolve(
        string? value, IReadOnlyDictionary<string, string> vars, LobsterRgba? currentColor, int depth)
    {
        if (value is null || depth > MaxDepth)
            return null;
        value = value.Trim();
        if (value.Length == 0 || value.Equals("none", StringComparison.OrdinalIgnoreCase)
            || value.Equals("transparent", StringComparison.OrdinalIgnoreCase))
            return null;
        if (value.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
            return currentColor;
        if (value[0] == '#')
            return ParseHex(value);
        if (Named.TryGetValue(value, out var named))
            return named;
        var open = value.IndexOf('(');
        if (open < 0 || value[^1] != ')')
            return null;
        var name = value[..open].Trim().ToLowerInvariant();
        var args = SplitTopLevel(value[(open + 1)..^1]);
        switch (name)
        {
            case "var":
                if (vars.TryGetValue(args[0].Trim(), out var bound) && Resolve(bound, vars, currentColor, depth + 1) is { } hit)
                    return hit;
                return args.Count > 1 ? Resolve(string.Join(",", args.Skip(1)), vars, currentColor, depth + 1) : null;
            case "rgb":
            case "rgba":
                return ParseRgb(args);
            case "color-mix":
                return Mix(args, vars, currentColor, depth);
            default:
                return null;
        }
    }

    private static LobsterRgba? ParseHex(string hex)
    {
        var digits = hex[1..];
        if (digits.Length is 3 or 4)
            digits = string.Concat(digits.Select(c => new string(c, 2)));
        if (digits.Length is not (6 or 8) || !uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return null;
        if (digits.Length == 6)
            v = (v << 8) | 0xff;
        return new LobsterRgba((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    private static LobsterRgba? ParseRgb(List<string> args)
    {
        // Accept both comma and space syntax, with an optional "/ alpha".
        var parts = string.Join(" ", args).Replace("/", " ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            return null;
        byte Channel(string s) => (byte)Math.Round(Math.Clamp(s.EndsWith('%')
            ? double.Parse(s[..^1], CultureInfo.InvariantCulture) * 2.55
            : double.Parse(s, CultureInfo.InvariantCulture), 0, 255));
        var alpha = parts.Length > 3
            ? (parts[3].EndsWith('%') ? double.Parse(parts[3][..^1], CultureInfo.InvariantCulture) / 100 : double.Parse(parts[3], CultureInfo.InvariantCulture))
            : 1;
        return new LobsterRgba(Channel(parts[0]), Channel(parts[1]), Channel(parts[2]), (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255));
    }

    private static LobsterRgba? Mix(List<string> args, IReadOnlyDictionary<string, string> vars, LobsterRgba? currentColor, int depth)
    {
        if (args.Count != 3 || !args[0].Trim().Equals("in srgb", StringComparison.OrdinalIgnoreCase))
            return null;
        var (colorA, pa) = SplitPercentage(args[1]);
        var (colorB, pb) = SplitPercentage(args[2]);
        // CSS Color 5: a missing percentage is 100% minus the other; both missing is 50/50.
        if (pa is null && pb is null)
            (pa, pb) = (0.5, 0.5);
        pa ??= 1 - pb!.Value;
        pb ??= 1 - pa.Value;
        var sum = pa.Value + pb.Value;
        if (sum <= 0)
            return null;
        var a = Resolve(colorA, vars, currentColor, depth + 1) ?? new LobsterRgba(0, 0, 0, 0);
        var b = Resolve(colorB, vars, currentColor, depth + 1) ?? new LobsterRgba(0, 0, 0, 0);
        double wa = pa.Value / sum, wb = pb.Value / sum;
        // Interpolate premultiplied channels; a sum under 100% scales the resulting alpha.
        double aa = a.A / 255.0, ab = b.A / 255.0;
        var alpha = aa * wa + ab * wb;
        byte Channel(byte ca, byte cb) => alpha <= 0
            ? (byte)0
            : (byte)Math.Round(Math.Clamp((ca * aa * wa + cb * ab * wb) / alpha, 0, 255));
        var outAlpha = alpha * Math.Min(sum, 1);
        return new LobsterRgba(Channel(a.R, b.R), Channel(a.G, b.G), Channel(a.B, b.B), (byte)Math.Round(outAlpha * 255));
    }

    private static (string Color, double? Fraction) SplitPercentage(string part)
    {
        part = part.Trim();
        var space = part.LastIndexOf(' ');
        if (space > 0 && part.EndsWith('%') && !part[space..].Contains(')', StringComparison.Ordinal))
            return (part[..space].Trim(), double.Parse(part[(space + 1)..^1], CultureInfo.InvariantCulture) / 100);
        return (part, null);
    }

    private static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '(')
                depth++;
            else if (text[i] == ')')
                depth--;
            else if (text[i] == ',' && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }
        parts.Add(text[start..]);
        return parts;
    }
}
