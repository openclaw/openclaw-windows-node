// <summary>
// Reads the dashboard appearance preferences a person saved to their Gateway profile (users.prefs.get,
// operator.read, Gateway 2026.8+): the accent and background artwork. It also applies a personal accent to a theme
// the way the Control UI does.
// Values are normalized exactly as upstream normalizeUiAppearancePreference does; anything else reads as unset.
// Usage:
//   var result = await client.SendWizardRequestAsync(GatewayAppearancePreferences.Method, GatewayAppearancePreferences.Request);
//   if (GatewayAppearancePreferences.TryParse(result, out var prefs)) Follow(prefs.Accent);
//   var themed = ThemeAccent.Apply(theme, ThemeColor.FromHex("#3a9b9b"));
// </summary>

using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared.Themes;

/// <summary>
/// The person's saved dashboard appearance. <see cref="Accent"/> is <c>#rrggbb</c> (lower case), <c>theme</c> for an
/// explicit theme accent, or null when unset. <see cref="Background"/> is null when unset (or malformed, which the
/// Control UI also treats as unset).
/// </summary>
public sealed record GatewayAppearancePreferences(string? Accent, BackgroundPreference? Background = null)
{
    public const string Method = "users.prefs.get";
    public const string AccentKey = "ui.accent";
    public const string ThemeAccent = "theme";

    /// <summary>The request parameters: only the keys the Companion follows.</summary>
    public static object Request { get; } = new { keys = new[] { AccentKey, BackgroundPreference.Key } };

    // \z, not $: $ also matches before a final newline, which ThemeColor.FromHex rejects.
    private static readonly Regex s_hex = new(@"^#[0-9a-f]{6}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The accent to apply, or null for the theme's own accent.</summary>
    public ThemeColor? AccentColor => Accent is { } hex && s_hex.IsMatch(hex) ? ThemeColor.FromHex(hex) : null;

    /// <summary>
    /// Reads a <c>users.prefs.get</c> result. False when the connection has no durable profile or the result is not
    /// understood: the person's preferences are then unknown, not unset.
    /// </summary>
    public static bool TryParse(JsonElement result, out GatewayAppearancePreferences preferences)
    {
        preferences = new GatewayAppearancePreferences((string?)null);
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String ||
            status.GetString() != "ok" ||
            !result.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Object)
            return false;
        var background = entries.TryGetProperty(BackgroundPreference.Key, out var raw) &&
            BackgroundPreference.TryParse(raw, out var parsed)
                ? parsed
                : null;
        preferences = new GatewayAppearancePreferences(NormalizeAccent(entries), background);
        return true;
    }

    /// <summary>A persisted accent: <c>#rrggbb</c> (lower case) or null.</summary>
    public static string? NormalizeHex(string? value) => value is not null && s_hex.IsMatch(value) ? value.ToLowerInvariant() : null;

    private static string? NormalizeAccent(JsonElement entries)
    {
        if (!entries.TryGetProperty(AccentKey, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString();
        return text == ThemeAccent ? ThemeAccent : NormalizeHex(text);
    }
}

/// <summary>
/// Applies a personal accent over a theme's accent tokens, as the Control UI's applyControlUiAccent does
/// (ui/src/app/control-ui-presentation.ts): accent, ring and primary take the color, their foregrounds the black or
/// white ink with the better WCAG contrast, hover mixes 18% white in, and the subtle wash is 16% of the accent.
/// </summary>
public static class ThemeAccent
{
    public static ResolvedTheme Apply(ResolvedTheme theme, ThemeColor accent) => theme with
    {
        Light = theme.Light is { } light ? Apply(light, accent) : null,
        Dark = theme.Dark is { } dark ? Apply(dark, accent) : null,
    };

    private static ResolvedThemePalette Apply(ResolvedThemePalette palette, ThemeColor accent)
    {
        var color = accent with { A = 255 };
        // Match the dashboard's WCAG black/white ink threshold.
        var ink = color.RelativeLuminance > 0.179 ? ThemeColor.Black : ThemeColor.White;
        return palette with
        {
            Accent = color,
            HasPersonalAccent = true,
            Ring = color,
            Primary = color,
            AccentForeground = ink,
            PrimaryForeground = ink,
            AccentHover = color.Mix(ThemeColor.White, 0.82),
            AccentSubtle = color.WithAlpha(0.16),
        };
    }
}
