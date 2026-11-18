namespace OpenClaw.Shared.Themes;

/// <summary>How the Companion shows that a reply is running (upstream <c>ThemeWorkingIndicator</c>).</summary>
public enum ThemeWorkingIndicator
{
    /// <summary>The animated claw (the Windows default).</summary>
    Claw,
    /// <summary>Three pulsing dots in the theme accent.</summary>
    Dots,
    /// <summary>The theme's brand icon.</summary>
    Brand,
    /// <summary>No glyph; the run status text stays visible instead.</summary>
    None,
}

/// <summary>
/// The parts of a theme's branding the Companion renders: whether the claw mascot appears, the brand icon
/// its brand spots show, and the working indicator. Resolution mirrors upstream <c>resolveThemeBranding</c>
/// (<c>packages/gateway-protocol/src/theme.ts</c>): a missing mascot is the claw; a missing brand icon is the
/// claw with the mascot and the mark without it; a missing working indicator is the claw with the mascot and
/// dots without it. Unknown or invalid values count as missing. The brand name, working phrases, lobsterdex and
/// community links are not rendered. A plugin artwork id as brand icon shows the mark, as the dashboard does
/// when that artwork is unavailable.
/// <example><code>
/// var branding = ThemeBranding.Resolve(mascot: "none", brandIcon: null, workingIndicator: null); // mark, dots
/// </code></example>
/// </summary>
public sealed record ThemeBranding(bool Mascot, string BrandIcon, ThemeWorkingIndicator WorkingIndicator)
{
    public const string ClawIcon = "claw";
    public const string MarkIcon = "mark";

    /// <summary>The Windows default and every theme without branding: the claw everywhere.</summary>
    public static ThemeBranding Default { get; } = new(true, ClawIcon, ThemeWorkingIndicator.Claw);

    /// <summary>True when brand spots show the claw; the mark and plugin artwork ids show the mark.</summary>
    public bool ShowsClaw => BrandIcon == ClawIcon;

    /// <summary>Upstream <c>mascot</c> wire value.</summary>
    public string MascotText => Mascot ? "claw" : "none";

    /// <summary>Upstream <c>workingIndicator</c> wire value.</summary>
    public string WorkingIndicatorText => WorkingIndicator switch
    {
        ThemeWorkingIndicator.Dots => "dots",
        ThemeWorkingIndicator.Brand => "brand",
        ThemeWorkingIndicator.None => "none",
        _ => "claw",
    };

    public static ThemeBranding Resolve(string? mascot, string? brandIcon, string? workingIndicator)
    {
        var hasMascot = mascot != "none";
        var icon = brandIcon is not null && GatewayThemeProtocol.IsArtworkId(brandIcon)
            ? brandIcon
            : hasMascot ? ClawIcon : MarkIcon;
        var indicator = workingIndicator switch
        {
            "claw" => ThemeWorkingIndicator.Claw,
            "dots" => ThemeWorkingIndicator.Dots,
            "brand" => ThemeWorkingIndicator.Brand,
            "none" => ThemeWorkingIndicator.None,
            _ => hasMascot ? ThemeWorkingIndicator.Claw : ThemeWorkingIndicator.Dots,
        };
        return new ThemeBranding(hasMascot, icon, indicator);
    }
}
