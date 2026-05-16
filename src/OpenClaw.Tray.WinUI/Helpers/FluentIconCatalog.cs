using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace OpenClawTray.Helpers;

/// <summary>
/// Central catalog of Segoe Fluent Icons (PUA) glyphs used by the tray UI.
/// Each entry is a single-character string in the Private Use Area
/// (U+E000-U+F8FF) so call sites avoid magic literals and tests can verify
/// the catalog is well-formed.
///
/// Codepoints are taken from the published Segoe Fluent Icons list. Where
/// a semantic match was ambiguous the closest available glyph is used and
/// noted in a comment.
/// </summary>
public static class FluentIconCatalog
{
    // ── Status / state ─────────────────────────────────────────────
    public const string StatusOk = "\uE73E";       // CheckMark
    public const string StatusWarn = "\uE7BA";     // Warning
    public const string StatusErr = "\uEA39";      // ErrorBadge

    // ── Sections / categories ──────────────────────────────────────
    public const string Sessions = "\uE8BD";       // Message
    public const string Approvals = "\uE7BA";      // Warning (re-use)
    public const string Devices = "\uE772";        // Devices (two devices — section header)
    public const string Hostname = "\uE977";       // Devices/IT — single hostname/system info pill
    public const string Permissions = "\uEA18";    // Shield

    // ── Capabilities (per-permission glyphs) ───────────────────────
    public const string Browser = "\uE774";        // Globe
    public const string Camera = "\uE722";         // Camera
    public const string Canvas = "\uE790";         // Color (palette) - generated art canvas
    public const string Screen = "\uEB91";         // ScreenTime (screen capture/recording)
    public const string Location = "\uE707";       // MapPin (Globe2 alt)
    public const string Voice = "\uE767";          // Volume (speaker, for TTS)
    public const string Speech = "\uF12E";         // Dictate (speech-to-text)
    public const string System = "\uE839";         // PC1 — "this PC as a node" (per CDR-0001 — was TVMonitor \uE7F4)
    public const string Operator = "\uE77B";       // ContactInfo — operator role (a human controlling agents)

    // ── Actions ────────────────────────────────────────────────────
    public const string Dashboard = "\uE774";      // Globe
    public const string OpenInBrowser = "\uE8A7";  // OpenInNewWindow — top-right dashboard launcher icon
    public const string Chat = "\uE8BD";           // Message
    public const string CanvasAct = "\uE790";      // Color (palette) - matches Canvas permission glyph
    public const string VoiceAct = "\uE720";       // Microphone
    public const string Settings = "\uE713";       // Settings
    public const string QuickSend = "\uE724";      // Send (Mail variant) — closest universal Send glyph
    public const string Setup = "\uE825";          // Bank — Reconfigure / Setup wizard launcher
    public const string About = "\uE946";          // Info
    public const string Exit = "\uE711";           // Cancel (X) — used for "Close" menu item
    public const string Add = "\uE710";            // Add — "+ Add gateway" header button
    public const string Back = "\uE72B";           // Back — leading chevron on Back hyperlink
    public const string Sync = "\uE895";           // Sync — Connecting / Disconnecting transient
    public const string Lock = "\uE192";           // Lock — Setup code / pairing waiting
    public const string Plug = "\uE839";           // Plug/PC1 — Direct connection tile (alias of System; same glyph)
    public const string MoreOverflow = "\uE712";   // More — saved-row overflow ⋯ button

    // ── Glance chips (Connection page) ─────────────────────────────
    public const string People = "\uE716";         // People — N clients chip
    public const string Money = "\uE9D9";          // Money — $today chip
    public const string ServerEnvironment = "\uE968"; // ServerEnvironment — topology chip
    public const string CapabilityOff = "\uE894";  // RemoveFrom — disabled capability state
    public const string Channels = "\uEC05";       // CellularData/Tower — N/M channels chip

    // ── Affordances ────────────────────────────────────────────────
    public const string ChevronR = "\uE76C";       // ChevronRight
    public const string Check = "\uE73E";          // CheckMark

    // ── Brand placeholder (lobster emoji currently retained) ───────
    public const string Brand = "🦞";

    /// <summary>
    /// Builds a <see cref="FontIcon"/> for the given PUA glyph using the
    /// system-resolved <c>SymbolThemeFontFamily</c> so the icon honors
    /// the user's selected icon font (Segoe Fluent Icons on Win11, Segoe
    /// MDL2 Assets fallback on Win10).
    /// </summary>
    public static FontIcon Build(string glyph, double size = 16)
    {
        return new FontIcon
        {
            Glyph = glyph,
            FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
            FontSize = size,
        };
    }

    /// <summary>
    /// True when <paramref name="value"/> is a single character in the
    /// Unicode Private Use Area (U+E000-U+F8FF) — i.e. a Segoe Fluent
    /// Icons glyph rather than an emoji.
    /// </summary>
    public static bool IsPuaGlyph(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 1)
            return false;
        var c = value[0];
        return c >= '\uE000' && c <= '\uF8FF';
    }
}
