using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenClaw.Shared.Themes;

/// <summary>One built-in theme's light and dark palettes as the probe resolved them.</summary>
public sealed record ThemeProbePalettes(ResolvedThemePalette Light, ResolvedThemePalette Dark);

/// <summary>
/// The probe's output: the palettes of every theme that resolved completely in both modes, the failures that kept
/// the others out, and the raw result object (cached so a later start can rebuild the palettes without a probe).
/// </summary>
public sealed record ThemeProbeResult(
    ImmutableDictionary<string, ThemeProbePalettes> Palettes,
    ImmutableArray<string> Failures,
    JsonElement Result);

/// <summary>
/// The shared token probe (<c>Themes/ThemeProbe.js</c>, embedded) and its result parser. The same script generates the
/// bundled table at build time, so a runtime result and the table are directly comparable. Parsing validates every
/// value, because at runtime the cascade comes from the connected Gateway: colors must be hex, radii bounded, and
/// font stacks plain family names (no paths or URLs a font source would load). A theme with any invalid value is
/// left out rather than applied partially.
/// <example><code>
/// await webView.ExecuteScriptAsync(ThemeProbe.Script);
/// var json = JsonSerializer.Deserialize&lt;string&gt;(await webView.ExecuteScriptAsync(ThemeProbe.ResolveExpression(ids)));
/// var probe = ThemeProbe.Parse(json!);
/// </code></example>
/// </summary>
public static class ThemeProbe
{
    /// <summary>Radii larger than this are not plausible Control UI geometry.</summary>
    public const double MaxRadius = 64;
    public const double MaxCornerScale = 4;
    private const int MaxFontStackLength = 256;
    private const string ResourceName = "OpenClaw.Shared.Themes.ThemeProbe.js";

    // Window foundations, the sidebar derived from them, and menus: WinUI paints these as solid surfaces over the
    // system backdrop, so a translucent value would let Mica show through. Real Gateways serve them opaque.
    private static readonly string[] s_opaqueFields =
    [
        nameof(ResolvedThemePalette.Canvas), nameof(ResolvedThemePalette.Content), nameof(ResolvedThemePalette.Surface),
        nameof(ResolvedThemePalette.Card), nameof(ResolvedThemePalette.Popover), nameof(ResolvedThemePalette.Elevated),
        nameof(ResolvedThemePalette.Panel),
    ];

    private static readonly string[] s_colorFields =
    [
        nameof(ResolvedThemePalette.Canvas), nameof(ResolvedThemePalette.Content), nameof(ResolvedThemePalette.Surface),
        nameof(ResolvedThemePalette.Card), nameof(ResolvedThemePalette.Popover), nameof(ResolvedThemePalette.Hover),
        nameof(ResolvedThemePalette.Text), nameof(ResolvedThemePalette.TextStrong), nameof(ResolvedThemePalette.Muted),
        nameof(ResolvedThemePalette.Border), nameof(ResolvedThemePalette.BorderStrong), nameof(ResolvedThemePalette.Accent),
        nameof(ResolvedThemePalette.AccentHover), nameof(ResolvedThemePalette.AccentForeground),
        nameof(ResolvedThemePalette.Primary), nameof(ResolvedThemePalette.PrimaryForeground), nameof(ResolvedThemePalette.Ring),
        nameof(ResolvedThemePalette.Destructive), nameof(ResolvedThemePalette.Danger), nameof(ResolvedThemePalette.Ok),
        nameof(ResolvedThemePalette.Warn), nameof(ResolvedThemePalette.Info), nameof(ResolvedThemePalette.ComposerSurface),
        nameof(ResolvedThemePalette.InlineCode), nameof(ResolvedThemePalette.Elevated), nameof(ResolvedThemePalette.Panel),
        nameof(ResolvedThemePalette.AccentSubtle),
    ];

    private static readonly Lazy<string> s_script = new(() =>
    {
        using var stream = typeof(ThemeProbe).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"{ResourceName} is not embedded.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    });

    private static readonly Lazy<string> s_scriptHash = new(() =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Script))));

    public static string Script => s_script.Value;

    /// <summary>Identifies this probe version; cached results from a different probe are not reused.</summary>
    public static string ScriptHash => s_scriptHash.Value;

    /// <summary>A script expression that returns the probe result for <paramref name="themeIds"/> as a JSON string.</summary>
    public static string ResolveExpression(IEnumerable<string> themeIds) =>
        "JSON.stringify(openclawThemeProbe.resolve(" + JsonSerializer.Serialize(themeIds.ToArray()) + "))";

    /// <summary>Parses the probe's JSON output (<c>{ result, failures }</c>). Throws on JSON that is not that shape.</summary>
    public static ThemeProbeResult Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var failures = ImmutableArray.CreateBuilder<string>();
        if (root.TryGetProperty("failures", out var reported) && reported.ValueKind == JsonValueKind.Array)
        {
            foreach (var failure in reported.EnumerateArray())
                failures.Add(failure.ValueKind == JsonValueKind.String ? failure.GetString()! : failure.GetRawText());
        }
        return ParseResult(root.GetProperty("result"), failures.ToImmutable());
    }

    /// <summary>
    /// Rebuilds palettes from a cached result object. Pass the failures reported with it: an unstyled theme's result
    /// holds complete base-palette values, so only its failures keep it rejected.
    /// </summary>
    public static ThemeProbeResult ParseResult(JsonElement result, ImmutableArray<string> failures = default)
    {
        if (result.ValueKind != JsonValueKind.Object)
            throw new FormatException("The probe result is not an object.");
        var problems = (failures.IsDefault ? [] : failures).ToBuilder();
        var palettes = ImmutableDictionary.CreateBuilder<string, ThemeProbePalettes>(StringComparer.Ordinal);
        foreach (var theme in result.EnumerateObject())
        {
            if (!GatewayThemeProtocol.IsThemeId(theme.Name) ||
                problems.Any(failure => failure.StartsWith(theme.Name + "/", StringComparison.Ordinal)))
                continue;
            if (theme.Value.ValueKind == JsonValueKind.Object &&
                theme.Value.TryGetProperty("light", out var light) && theme.Value.TryGetProperty("dark", out var dark) &&
                TryPalette(light, ThemeColorMode.Light, out var lightPalette) &&
                TryPalette(dark, ThemeColorMode.Dark, out var darkPalette))
            {
                palettes[theme.Name] = new ThemeProbePalettes(lightPalette, darkPalette);
                continue;
            }
            problems.Add($"{theme.Name}/invalid");
        }
        return new ThemeProbeResult(palettes.ToImmutable(), problems.ToImmutable(), result.Clone());
    }

    private static bool TryPalette(JsonElement palette, ThemeColorMode mode, out ResolvedThemePalette resolved)
    {
        resolved = null!;
        if (palette.ValueKind != JsonValueKind.Object)
            return false;
        var colors = new Dictionary<string, ThemeColor>(StringComparer.Ordinal);
        foreach (var field in s_colorFields)
        {
            if (!palette.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String ||
                !ThemeColor.TryFromHex(value.GetString(), out var color))
                return false;
            colors[field] = color;
        }
        if (s_opaqueFields.Any(field => colors[field].A != 255))
            return false;
        if (!TryNumber(palette, "RadiusSm", MaxRadius, out var small) ||
            !TryNumber(palette, "RadiusMd", MaxRadius, out var medium) ||
            !TryNumber(palette, "CornerScale", MaxCornerScale, out var scale) ||
            !TryFonts(palette, "Body", out var body) || !TryFonts(palette, "Chat", out var chat) ||
            !TryFonts(palette, "Mono", out var mono))
            return false;
        resolved = new ResolvedThemePalette
        {
            Canvas = colors[nameof(ResolvedThemePalette.Canvas)],
            Content = colors[nameof(ResolvedThemePalette.Content)],
            Surface = colors[nameof(ResolvedThemePalette.Surface)],
            Card = colors[nameof(ResolvedThemePalette.Card)],
            Popover = colors[nameof(ResolvedThemePalette.Popover)],
            Hover = colors[nameof(ResolvedThemePalette.Hover)],
            Text = colors[nameof(ResolvedThemePalette.Text)],
            TextStrong = colors[nameof(ResolvedThemePalette.TextStrong)],
            Muted = colors[nameof(ResolvedThemePalette.Muted)],
            Border = colors[nameof(ResolvedThemePalette.Border)],
            BorderStrong = colors[nameof(ResolvedThemePalette.BorderStrong)],
            Accent = colors[nameof(ResolvedThemePalette.Accent)],
            AccentHover = colors[nameof(ResolvedThemePalette.AccentHover)],
            AccentForeground = colors[nameof(ResolvedThemePalette.AccentForeground)],
            Primary = colors[nameof(ResolvedThemePalette.Primary)],
            PrimaryForeground = colors[nameof(ResolvedThemePalette.PrimaryForeground)],
            Ring = colors[nameof(ResolvedThemePalette.Ring)],
            Destructive = colors[nameof(ResolvedThemePalette.Destructive)],
            Danger = colors[nameof(ResolvedThemePalette.Danger)],
            Ok = colors[nameof(ResolvedThemePalette.Ok)],
            Warn = colors[nameof(ResolvedThemePalette.Warn)],
            Info = colors[nameof(ResolvedThemePalette.Info)],
            ComposerSurface = colors[nameof(ResolvedThemePalette.ComposerSurface)],
            InlineCode = colors[nameof(ResolvedThemePalette.InlineCode)],
            Elevated = colors[nameof(ResolvedThemePalette.Elevated)],
            Panel = colors[nameof(ResolvedThemePalette.Panel)],
            AccentSubtle = colors[nameof(ResolvedThemePalette.AccentSubtle)],
            BodyFont = body,
            ChatFont = chat,
            MonoFont = mono,
            Radii = new ThemeRadii(small, medium, scale),
            Mode = mode,
        };
        return true;

        static bool TryNumber(JsonElement palette, string name, double max, out double value)
        {
            value = 0;
            return palette.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number &&
                element.TryGetDouble(out value) && double.IsFinite(value) && value >= 0 && value <= max;
        }

        static bool TryFonts(JsonElement palette, string name, out ThemeFontStack stack)
        {
            stack = null!;
            if (!palette.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String ||
                element.GetString() is not { Length: > 0 and <= MaxFontStackLength } text ||
                !GatewayThemeProtocol.IsFontFamilyList(text))
                return false;
            stack = ThemeFontStack.Parse(text);
            return true;
        }
    }
}
