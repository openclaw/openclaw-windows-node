using System.Globalization;

namespace OpenClaw.Shared.Sessions;

/// <summary>
/// Session tint and icon vocabulary shared with the Gateway (<c>sessions.patch</c>
/// <c>color</c>/<c>icon</c>). Clients render theme-tuned hues per color name,
/// never raw values, and only the named glyph ids map to native icons.
/// </summary>
public static class SessionAppearance
{
    /// <summary>Gateway color names in their canonical order.</summary>
    public static IReadOnlyList<string> ColorIds { get; } =
        ["red", "blue", "green", "yellow", "purple", "orange", "pink", "cyan"];

    /// <summary>Named icon glyph ids. Custom SVG icons are valid on the wire but have no native glyph.</summary>
    public static IReadOnlyList<string> IconIds { get; } = ["braces", "book", "monitor", "bot", "kanban", "coins"];

    public static string? NormalizeColor(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is not null && ColorIds.Contains(normalized) ? normalized : null;
    }

    public static string? NormalizeIcon(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is not null && IconIds.Contains(normalized) ? normalized : null;
    }

    /// <summary>
    /// A stable palette color for an identity without its own color (agents, groups,
    /// repositories), so the same name keeps the same hue across launches and machines.
    /// </summary>
    public static string PaletteColor(string? seed)
    {
        if (string.IsNullOrWhiteSpace(seed))
            return ColorIds[1];
        // FNV-1a: string.GetHashCode is randomized per process.
        var hash = 2166136261u;
        foreach (var ch in seed.Trim().ToLowerInvariant())
        {
            hash ^= ch;
            hash *= 16777619u;
        }
        return ColorIds[(int)(hash % (uint)ColorIds.Count)];
    }

    /// <summary>Up to two uppercase initials for an avatar without an emoji.</summary>
    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "?";
        var words = name.Split([' ', '-', '_', '.', ':', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Where(word => char.IsLetterOrDigit(word[0]))
            .ToArray();
        if (words.Length == 0)
            return "?";
        var first = StringInfo.GetNextTextElement(words[0]);
        var second = words.Length > 1 ? StringInfo.GetNextTextElement(words[1]) : "";
        return (first + second).ToUpper(CultureInfo.CurrentCulture);
    }
}
