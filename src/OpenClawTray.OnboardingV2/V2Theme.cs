using System.Collections.Concurrent;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace OpenClawTray.Onboarding.V2;

/// <summary>
/// Centralised theme palette for the V2 onboarding redesign.
///
/// The designer mocks are dark-mode only. The dark values are taken
/// directly from those mocks (#202020 window, #2C2C2C card, #60C8F8
/// accent, etc.). The light variants are derived from Windows 11
/// Fluent defaults so the UI keeps the same visual rhythm in light
/// mode without forcing the designer to re-spec every colour.
///
/// All accessors take an <see cref="ElementTheme"/> (resolved to Light
/// or Dark — the V2 shell never passes Default). Brushes are memoised
/// per (role, theme) tuple so the resolver is allocation-free after
/// first use.
///
/// Pages should never instantiate <c>SolidColorBrush</c> with a hex
/// value directly. Instead they read <c>Props.EffectiveTheme</c> in
/// <c>Render()</c> and forward it to these helpers.
/// </summary>
public static class V2Theme
{
    private static readonly ConcurrentDictionary<(string, ElementTheme), SolidColorBrush> _cache = new();

    private static SolidColorBrush B(string role, ElementTheme theme, byte ad, byte rd, byte gd, byte bd, byte al, byte rl, byte gl, byte bl)
        => _cache.GetOrAdd((role, theme), _ =>
        {
            var c = theme == ElementTheme.Light
                ? ColorHelper.FromArgb(al, rl, gl, bl)
                : ColorHelper.FromArgb(ad, rd, gd, bd);
            return new SolidColorBrush(c);
        });

    private static SolidColorBrush B(string role, ElementTheme theme, uint dark, uint light)
        => B(role, theme,
             ad: (byte)((dark >> 24) & 0xFF), rd: (byte)((dark >> 16) & 0xFF), gd: (byte)((dark >> 8) & 0xFF), bd: (byte)(dark & 0xFF),
             al: (byte)((light >> 24) & 0xFF), rl: (byte)((light >> 16) & 0xFF), gl: (byte)((light >> 8) & 0xFF), bl: (byte)(light & 0xFF));

    // --- Surface & text ---

    /// <summary>Window background (the flat colour behind everything).</summary>
    public static SolidColorBrush WindowBackground(ElementTheme t) => B(nameof(WindowBackground), t, 0xFF202020, 0xFFF3F3F3);

    /// <summary>Default card background (Permissions rows, refresh button bg).</summary>
    public static SolidColorBrush CardBackground(ElementTheme t) => B(nameof(CardBackground), t, 0xFF2C2C2C, 0xFFFFFFFF);

    /// <summary>Card background when hovered.</summary>
    public static SolidColorBrush CardBackgroundHover(ElementTheme t) => B(nameof(CardBackgroundHover), t, 0xFF363636, 0xFFF5F5F5);

    /// <summary>Card background when pressed.</summary>
    public static SolidColorBrush CardBackgroundPressed(ElementTheme t) => B(nameof(CardBackgroundPressed), t, 0xFF424242, 0xFFEAEAEA);

    /// <summary>Primary body / heading text.</summary>
    public static SolidColorBrush TextPrimary(ElementTheme t) => B(nameof(TextPrimary), t, 0xFFE0E0E0, 0xFF1C1C1C);

    /// <summary>Heading-emphasis text (slightly brighter than primary on dark).</summary>
    public static SolidColorBrush TextStrong(ElementTheme t) => B(nameof(TextStrong), t, 0xFFF0F0F0, 0xFF000000);

    /// <summary>Secondary text (status descriptors, intro paragraphs).</summary>
    public static SolidColorBrush TextSecondary(ElementTheme t) => B(nameof(TextSecondary), t, 0xFFD0D0D0, 0xFF424242);

    /// <summary>Subtle text (subtitles, footnotes, gateway URL prefix).</summary>
    public static SolidColorBrush TextSubtle(ElementTheme t) => B(nameof(TextSubtle), t, 0xFFA0A0A0, 0xFF6B6B6B);

    /// <summary>Inactive step-indicator dot.</summary>
    public static SolidColorBrush StepDotInactive(ElementTheme t) => B(nameof(StepDotInactive), t, 0xFF5A5A5A, 0xFFC4C4C4);

    // --- Brand accents (theme-invariant unless tone needs adjusting) ---

    private static readonly SolidColorBrush _accentCyan = new(ColorHelper.FromArgb(0xFF, 0x60, 0xC8, 0xF8));
    private static readonly SolidColorBrush _accentCyanHover = new(ColorHelper.FromArgb(0xFF, 0x52, 0xB0, 0xDA));
    private static readonly SolidColorBrush _accentCyanPressed = new(ColorHelper.FromArgb(0xFF, 0x46, 0x99, 0xBC));
    private static readonly SolidColorBrush _accentCyanGlowHover = new(ColorHelper.FromArgb(40, 0x60, 0xC8, 0xF8));
    private static readonly SolidColorBrush _accentCyanGlowPressed = new(ColorHelper.FromArgb(60, 0x60, 0xC8, 0xF8));
    private static readonly SolidColorBrush _accentGreen = new(ColorHelper.FromArgb(0xFF, 0x6D, 0xC8, 0x68));
    private static readonly SolidColorBrush _badgeCheckGreen = new(ColorHelper.FromArgb(0xFF, 0x2B, 0xC3, 0x6F));
    private static readonly SolidColorBrush _badgeErrorPink = new(ColorHelper.FromArgb(0xFF, 0xF4, 0xA6, 0xB0));
    private static readonly SolidColorBrush _badgeWarningAmber = new(ColorHelper.FromArgb(0xFF, 0xE0, 0xA4, 0x22));
    private static readonly SolidColorBrush _onAccentText = new(ColorHelper.FromArgb(0xFF, 0x00, 0x00, 0x00));
    private static readonly SolidColorBrush _white = new(ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush _transparent = new(Colors.Transparent);

    /// <summary>Primary accent (CTA bg, active step dot, inline links).</summary>
    public static SolidColorBrush AccentCyan() => _accentCyan;
    /// <summary>Accent on hover.</summary>
    public static SolidColorBrush AccentCyanHover() => _accentCyanHover;
    /// <summary>Accent on press.</summary>
    public static SolidColorBrush AccentCyanPressed() => _accentCyanPressed;
    /// <summary>Translucent accent fill for transparent buttons (hover state).</summary>
    public static SolidColorBrush AccentCyanGlowHover() => _accentCyanGlowHover;
    /// <summary>Translucent accent fill for transparent buttons (pressed state).</summary>
    public static SolidColorBrush AccentCyanGlowPressed() => _accentCyanGlowPressed;
    /// <summary>Toggle-on accent green.</summary>
    public static SolidColorBrush AccentGreen() => _accentGreen;
    /// <summary>Status checkmark badge fill.</summary>
    public static SolidColorBrush BadgeCheckGreen() => _badgeCheckGreen;
    /// <summary>Status error badge fill (pink).</summary>
    public static SolidColorBrush BadgeErrorPink() => _badgeErrorPink;
    /// <summary>Node-mode warning badge fill (amber).</summary>
    public static SolidColorBrush BadgeWarningAmber() => _badgeWarningAmber;
    /// <summary>Foreground colour for text on top of the cyan / green accents (always near-black for contrast).</summary>
    public static SolidColorBrush OnAccentText() => _onAccentText;
    /// <summary>Pure white (toggle thumbs, etc.).</summary>
    public static SolidColorBrush White() => _white;
    /// <summary>Transparent (for borders we want invisible).</summary>
    public static SolidColorBrush Transparent() => _transparent;

    // --- Error / failure card (Dialog-6) ---

    /// <summary>Error card background (deep maroon dark / soft pink light).</summary>
    public static SolidColorBrush ErrorCardBackground(ElementTheme t) => B(nameof(ErrorCardBackground), t, 0xFF3D1818, 0xFFFCE4E8);

    /// <summary>Error card foreground text.</summary>
    public static SolidColorBrush ErrorCardForeground(ElementTheme t) => B(nameof(ErrorCardForeground), t, 0xFFE8E0E0, 0xFF4A1620);

    /// <summary>"Try again" button background.</summary>
    public static SolidColorBrush ErrorButtonBackground(ElementTheme t) => B(nameof(ErrorButtonBackground), t, 0xFF551B20, 0xFFC53848);

    /// <summary>"Try again" button hover.</summary>
    public static SolidColorBrush ErrorButtonHover(ElementTheme t) => B(nameof(ErrorButtonHover), t, 0xFF65252A, 0xFFB22F3D);

    /// <summary>"Try again" button pressed.</summary>
    public static SolidColorBrush ErrorButtonPressed(ElementTheme t) => B(nameof(ErrorButtonPressed), t, 0xFF451518, 0xFF982634);

    /// <summary>"Try again" button border (used as the focused outline).</summary>
    public static SolidColorBrush ErrorButtonBorder(ElementTheme t) => B(nameof(ErrorButtonBorder), t, 0xFF441010, 0xFF8A1F2C);

    // --- Warning / Node-mode card (Dialog-4) ---

    /// <summary>Warning card background (dark amber / pale amber).</summary>
    public static SolidColorBrush WarningCardBackground(ElementTheme t) => B(nameof(WarningCardBackground), t, 0xFF5C4413, 0xFFFFF4D6);

    /// <summary>Warning card hover.</summary>
    public static SolidColorBrush WarningCardHover(ElementTheme t) => B(nameof(WarningCardHover), t, 0xFF332810, 0xFFFFEDC2);

    /// <summary>Warning card foreground text.</summary>
    public static SolidColorBrush WarningCardForeground(ElementTheme t) => B(nameof(WarningCardForeground), t, 0xFFE8E0CC, 0xFF4A3508);

    // --- Transparent button overlays (for ghost / link buttons) ---

    /// <summary>Hover overlay tint for transparent buttons over a neutral background.</summary>
    public static SolidColorBrush ButtonOverlayHover(ElementTheme t) => B(nameof(ButtonOverlayHover), t, 0x30FFFFFF, 0x10000000);

    /// <summary>Pressed overlay tint for transparent buttons over a neutral background.</summary>
    public static SolidColorBrush ButtonOverlayPressed(ElementTheme t) => B(nameof(ButtonOverlayPressed), t, 0x50FFFFFF, 0x20000000);
}
