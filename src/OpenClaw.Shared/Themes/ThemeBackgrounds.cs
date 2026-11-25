// <summary>
// The dashboard's background artwork preference (users.prefs ui.background, gateway-protocol
// background-preferences.ts) and how a theme's artwork is drawn on the Companion's chat surfaces, matching the
// Control UI (ui/src/components/session-background.tsx and ui/src/styles/session-background.css).
// Usage:
//   if (BackgroundPreference.TryParse(json, out var preference)) Save(preference);
//   var opacity = ThemeBackgrounds.Opacity(preference, ThemeBackgroundSurface.Conversation);
//   var file = ThemeBackgrounds.ArtworkFamily(theme) + "-dark.png";
// </summary>

using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared.Themes;

public enum BackgroundSourceKind
{
    None,
    Theme,
    /// <summary>A personal image uploaded to the Gateway profile; the Companion doesn't fetch it yet.</summary>
    Custom,
}

public enum BackgroundPresentation
{
    /// <summary>The artwork fades into the canvas on the new-chat surface.</summary>
    Faded,
    /// <summary>Edge-to-edge artwork over the opaque canvas, at most 70% strength.</summary>
    FullBleed,
}

/// <summary>Where chat artwork appears: the new-chat surface or an open conversation.</summary>
public enum ThemeBackgroundSurface
{
    NewSession,
    Conversation,
}

/// <summary>A person's background artwork choice, in the Control UI's model.</summary>
public sealed record BackgroundPreference(
    BackgroundSourceKind Source,
    string? AssetId,
    BackgroundPresentation Presentation,
    bool ShowOnNewSession,
    bool ShowInSessions,
    double Visibility)
{
    public const string Key = "ui.background";

    /// <summary>The Control UI default (DEFAULT_BACKGROUND_PREFERENCE): theme artwork, faded, both surfaces, 50%.</summary>
    public static BackgroundPreference Default { get; } =
        new(BackgroundSourceKind.Theme, null, BackgroundPresentation.Faded, true, true, 0.5);

    // \z, not $: $ also matches before a final newline.
    private static readonly Regex s_assetId = new(@"^[A-Za-z0-9_-]{1,128}\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads the upstream wire shape exactly as normalizeBackgroundPreference does: anything malformed is rejected
    /// as a whole, and a missing presentation means faded.
    /// </summary>
    public static bool TryParse(JsonElement value, out BackgroundPreference preference)
    {
        preference = Default;
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object ||
            !source.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
            return false;
        BackgroundSourceKind sourceKind;
        string? assetId = null;
        switch (GatewayThemeProtocol.ReadString(kind))
        {
            case "none":
                sourceKind = BackgroundSourceKind.None;
                break;
            case "theme":
                sourceKind = BackgroundSourceKind.Theme;
                break;
            case "custom" when source.TryGetProperty("assetId", out var asset) && asset.ValueKind == JsonValueKind.String &&
                GatewayThemeProtocol.ReadString(asset) is { } id && s_assetId.IsMatch(id):
                sourceKind = BackgroundSourceKind.Custom;
                assetId = id;
                break;
            default:
                return false;
        }
        var presentation = BackgroundPresentation.Faded;
        if (value.TryGetProperty("presentation", out var style))
        {
            switch (style.ValueKind == JsonValueKind.String ? GatewayThemeProtocol.ReadString(style) : null)
            {
                case "faded":
                    break;
                case "full-bleed":
                    presentation = BackgroundPresentation.FullBleed;
                    break;
                default:
                    return false;
            }
        }
        if (!value.TryGetProperty("showOnNewSession", out var newSession) || newSession.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !value.TryGetProperty("showInSessions", out var sessions) || sessions.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !value.TryGetProperty("visibility", out var visibility) || visibility.ValueKind != JsonValueKind.Number ||
            !visibility.TryGetDouble(out var strength) || !double.IsFinite(strength) || strength is < 0 or > 1)
            return false;
        preference = new BackgroundPreference(sourceKind, assetId, presentation, newSession.GetBoolean(), sessions.GetBoolean(), strength);
        return true;
    }

    /// <summary>Writes the upstream wire shape.</summary>
    public void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteStartObject("source");
        writer.WriteString("kind", Source switch
        {
            BackgroundSourceKind.None => "none",
            BackgroundSourceKind.Custom => "custom",
            _ => "theme",
        });
        if (Source == BackgroundSourceKind.Custom && AssetId is not null)
            writer.WriteString("assetId", AssetId);
        writer.WriteEndObject();
        writer.WriteString("presentation", Presentation == BackgroundPresentation.FullBleed ? "full-bleed" : "faded");
        writer.WriteBoolean("showOnNewSession", ShowOnNewSession);
        writer.WriteBoolean("showInSessions", ShowInSessions);
        writer.WriteNumber("visibility", Visibility);
        writer.WriteEndObject();
    }
}

/// <summary>How a theme's background artwork is drawn on the Companion's chat surfaces.</summary>
public static class ThemeBackgrounds
{
    // ui/src/components/session-background.tsx FULL_BLEED_MAX_OPACITY.
    private const double FullBleedMaxOpacity = 0.7;

    private static readonly HashSet<string> s_builtinFamilies = new(StringComparer.Ordinal)
    {
        "claw", "knot", "dash", "absolutely", "tide", "beacon", "phosphor", "crt", "manuscript", "rose", "miami",
    };

    /// <summary>
    /// The artwork pair a theme uses (Assets/ThemeBackgrounds/&lt;family&gt;-&lt;mode&gt;.png): its own for a built-in,
    /// the neutral custom pair for plugin and imported themes, none for the Windows default look.
    /// </summary>
    public static string? ArtworkFamily(ResolvedTheme? theme) => theme switch
    {
        null => null,
        { Source: GatewayThemeSource.Builtin } when s_builtinFamilies.Contains(theme.Id) => theme.Id,
        { Source: GatewayThemeSource.Builtin } => null,
        _ => "custom",
    };

    /// <summary>
    /// The artwork's strength on <paramref name="surface"/>, 0 for none. A null preference is the Control UI before a
    /// person sets one: the theme artwork at full strength everywhere. A custom image isn't fetched, so it shows the
    /// plain theme canvas, as the Control UI does when the image is missing or offline.
    /// </summary>
    public static double Opacity(BackgroundPreference? preference, ThemeBackgroundSurface surface)
    {
        if (preference is null)
            return 1;
        var shown = surface == ThemeBackgroundSurface.NewSession ? preference.ShowOnNewSession : preference.ShowInSessions;
        if (preference.Source != BackgroundSourceKind.Theme || !shown || preference.Visibility <= 0)
            return 0;
        return preference.Presentation == BackgroundPresentation.FullBleed
            ? preference.Visibility * FullBleedMaxOpacity
            : Math.Min(1, preference.Visibility / BackgroundPreference.Default.Visibility);
    }

    /// <summary>True when the artwork fades into the canvas: faded style on the new-chat surface.</summary>
    public static bool Fades(BackgroundPreference? preference, ThemeBackgroundSurface surface) =>
        preference is { Presentation: BackgroundPresentation.Faded } && surface == ThemeBackgroundSurface.NewSession;
}
