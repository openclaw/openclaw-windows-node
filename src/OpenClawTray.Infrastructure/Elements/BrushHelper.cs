using System.Collections.Concurrent;
using Microsoft.UI.Xaml.Media;

namespace OpenClawTray.Infrastructure;

/// <summary>
/// Color and brush parsing utilities.
/// Supports named colors, hex (#RRGGBB, #AARRGGBB), and direct Color values.
/// Colors are cached by string; a fresh SolidColorBrush is created per call
/// because DependencyObjects have thread affinity and cannot be safely shared.
/// </summary>
public static class BrushHelper
{
    private static readonly ConcurrentDictionary<string, global::Windows.UI.Color> _colorCache = new();

    /// <summary>
    /// Parses a color string into a SolidColorBrush.
    /// Supports named colors (red, green, blue, white, black, gray, lightgray, transparent)
    /// and hex codes (#RRGGBB or #AARRGGBB).
    /// Color parsing is cached; a new brush is created each call (thread-safe).
    /// </summary>
    public static SolidColorBrush Parse(string color)
    {
        var parsed = _colorCache.GetOrAdd(color, static c =>
            c.ToLowerInvariant() switch
            {
                "red" => global::Windows.UI.Color.FromArgb(255, 255, 0, 0),
                "green" => global::Windows.UI.Color.FromArgb(255, 0, 128, 0),
                "blue" => global::Windows.UI.Color.FromArgb(255, 0, 0, 255),
                "white" => global::Windows.UI.Color.FromArgb(255, 255, 255, 255),
                "black" => global::Windows.UI.Color.FromArgb(255, 0, 0, 0),
                "gray" or "grey" => global::Windows.UI.Color.FromArgb(255, 128, 128, 128),
                "lightgray" or "lightgrey" => global::Windows.UI.Color.FromArgb(255, 211, 211, 211),
                "transparent" => global::Windows.UI.Color.FromArgb(0, 0, 0, 0),
                _ when c.StartsWith('#') => ParseHex(c),
                _ => global::Windows.UI.Color.FromArgb(255, 128, 128, 128),
            });
        return new SolidColorBrush(parsed);
    }

    internal static global::Windows.UI.Color ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6
            && byte.TryParse(hex[0..2], global::System.Globalization.NumberStyles.HexNumber, null, out var r6)
            && byte.TryParse(hex[2..4], global::System.Globalization.NumberStyles.HexNumber, null, out var g6)
            && byte.TryParse(hex[4..6], global::System.Globalization.NumberStyles.HexNumber, null, out var b6))
        {
            return global::Windows.UI.Color.FromArgb(255, r6, g6, b6);
        }
        if (hex.Length == 8
            && byte.TryParse(hex[0..2], global::System.Globalization.NumberStyles.HexNumber, null, out var a8)
            && byte.TryParse(hex[2..4], global::System.Globalization.NumberStyles.HexNumber, null, out var r8)
            && byte.TryParse(hex[4..6], global::System.Globalization.NumberStyles.HexNumber, null, out var g8)
            && byte.TryParse(hex[6..8], global::System.Globalization.NumberStyles.HexNumber, null, out var b8))
        {
            return global::Windows.UI.Color.FromArgb(a8, r8, g8, b8);
        }
        // Fallback to gray for malformed hex, consistent with named color fallback
        return global::Windows.UI.Color.FromArgb(255, 128, 128, 128);
    }
}
