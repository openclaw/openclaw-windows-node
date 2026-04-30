using System;
using System.Text.Json.Nodes;
using Microsoft.UI;
using Windows.UI;

namespace OpenClawTray.A2UI.Theming;

/// <summary>
/// Per-surface theme overrides resolved from createSurface.theme. Optional —
/// surfaces without theme payloads default to Fluent (XamlControlsResources).
/// Values resolve to <see cref="ThemeResource"/> overrides applied to the
/// SurfaceHost's resource scope, never globally.
/// </summary>
public sealed class A2UITheme
{
    public Color? Accent { get; init; }
    public Color? Background { get; init; }
    public Color? Foreground { get; init; }
    public Color? CardBackground { get; init; }
    public string? FontFamily { get; init; }
    public double? CornerRadius { get; init; }
    public double? Spacing { get; init; }

    public static A2UITheme Empty { get; } = new();

    /// <summary>
    /// Parse <c>beginRendering.styles</c>. v0.8 standard catalog defines two
    /// fields: <c>font</c> and <c>primaryColor</c>. Older / extended payloads
    /// may carry richer color/typography blocks; we read both forms.
    /// </summary>
    public static A2UITheme Parse(JsonObject? payload)
    {
        if (payload == null) return Empty;

        var primary = ParseFlatString(payload, "primaryColor");
        var fontFlat = ParseFlatString(payload, "font");

        return new A2UITheme
        {
            Accent = primary != null ? TryParseHex(primary) : ParseNestedColor(payload, "colors", "accent"),
            Background = ParseNestedColor(payload, "colors", "background"),
            Foreground = ParseNestedColor(payload, "colors", "foreground"),
            CardBackground = ParseNestedColor(payload, "colors", "card"),
            FontFamily = fontFlat ?? ParseNestedString(payload, "typography", "fontFamily"),
            CornerRadius = ParseDouble(payload, "radius"),
            Spacing = ParseDouble(payload, "spacing"),
        };
    }

    private static string? ParseFlatString(JsonObject root, string key) =>
        root[key] is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;

    private static Color? ParseNestedColor(JsonObject root, string parent, string key)
    {
        if (root[parent] is not JsonObject p) return null;
        if (p[key] is not JsonValue jv) return null;
        if (!jv.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s)) return null;
        return TryParseHex(s);
    }

    private static string? ParseNestedString(JsonObject root, string parent, string key)
    {
        if (root[parent] is not JsonObject p) return null;
        if (p[key] is JsonValue jv && jv.TryGetValue<string>(out var s)) return s;
        return null;
    }

    private static double? ParseDouble(JsonObject root, string key)
    {
        if (root[key] is JsonValue jv && jv.TryGetValue<double>(out var d)) return d;
        return null;
    }

    private static Color? TryParseHex(string s)
    {
        s = s.Trim();
        if (s.StartsWith('#')) s = s[1..];
        try
        {
            if (s.Length == 6)
            {
                byte r = Convert.ToByte(s.Substring(0, 2), 16);
                byte g = Convert.ToByte(s.Substring(2, 2), 16);
                byte b = Convert.ToByte(s.Substring(4, 2), 16);
                return Color.FromArgb(0xFF, r, g, b);
            }
            if (s.Length == 8)
            {
                byte a = Convert.ToByte(s.Substring(0, 2), 16);
                byte r = Convert.ToByte(s.Substring(2, 2), 16);
                byte g = Convert.ToByte(s.Substring(4, 2), 16);
                byte b = Convert.ToByte(s.Substring(6, 2), 16);
                return Color.FromArgb(a, r, g, b);
            }
        }
        catch { }
        return null;
    }
}
