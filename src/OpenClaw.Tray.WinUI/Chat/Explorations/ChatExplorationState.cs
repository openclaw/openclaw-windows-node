using System;
using Microsoft.UI.Xaml.Media;

namespace OpenClawTray.Chat.Explorations;

// Enums mirror Kenny's v2 ChatExplorationPreview spec
// (openclaw-windows-node-v2/src/OpenClaw.Tray.WinUI/Controls/ChatExplorations/ChatExplorationPreview.xaml.cs).
// Source of truth for value names — keep in sync if the v2 spec evolves.

public enum ChatVariation
{
    /// <summary>Mica look-alike, large rounded bubbles, generous spacing.</summary>
    Calm,
    /// <summary>Acrylic look-alike, small bubbles, tight spacing.</summary>
    Compact,
    /// <summary>Solid surface, no bubble fill, thin accent left stroke + larger typography.</summary>
    Plain,
}

public enum ChatBackdropMode
{
    Mica,
    MicaAlt,
    Acrylic,
    Solid,
}

public enum ChatPaddingDensity
{
    Cozy,
    Comfortable,
    Compact,
}

/// <summary>
/// Tone used for the user (sent) chat bubble. <c>Accent</c> paints the
/// bubble with the system accent color at full weight (<c>AccentFillColorDefault</c>) —
/// classic iMessage-style bold brand-color bubble. <c>Secondary</c> uses the
/// softer accent variant (<c>AccentFillColorSecondary</c>) — still clearly
/// the accent color, but a step down in saturation so it doesn't compete
/// with the accent-colored avatar / send button. Both tones pair with
/// <c>TextOnAccentFillColorPrimaryBrush</c>, which Fluent guarantees meets
/// WCAG AA contrast in light, dark, and High Contrast themes.
/// </summary>
public enum ChatUserBubbleTone
{
    Accent,
    Secondary,
}

public enum ChatPreviewTheme
{
    System,
    Light,
    Dark,
}

public enum ChatAvatarMode
{
    Both,
    AgentOnly,
    None,
}

/// <summary>
/// Forces the chat surface into a specific lifecycle state for preview purposes.
/// <c>Live</c> = no override (use real provider data). The other values render
/// a synthesized version of each visual state so designers can compare them
/// without waiting for real backend conditions.
/// </summary>
public enum ChatPreviewState
{
    /// <summary>No override — render whatever the data provider says.</summary>
    Live,
    /// <summary>Force the "Connecting to gateway…" ProgressRing screen.</summary>
    Loading,
    /// <summary>
    /// Force the unified zero-state (welcome screen with app icon + prompt
    /// suggestions). Covers both "no thread selected" and "thread with zero
    /// messages" — they intentionally render identically because the
    /// distinction is a backend implementation detail, not a user-facing one.
    /// </summary>
    Empty,
    /// <summary>Timeline + inline "thinking" indicator (turn started, no assistant delta yet).</summary>
    Thinking,
    /// <summary>Composer shows the tool-permission banner with Allow/Deny.</summary>
    PendingPermission,
    /// <summary>
    /// Force the pre-connect "Reconnecting to your last conversation…"
    /// banner that <see cref="OpenClaw.Tray.WinUI.Chat.OpenClawChatRoot"/>
    /// renders when <c>effectiveThread</c> is null because the gateway
    /// handshake hasn't completed yet. Lets designers see the banner
    /// without having to actually disconnect.
    /// </summary>
    Reconnecting,
}

public enum ChatComposerLayout
{
    /// <summary>Three rows: dropdowns / textbox / actions. Mirrors production OpenClawComposer.</summary>
    ThreeRow,
    /// <summary>Two rows: textbox on top, then [borderless session·model pill] [actions] [Send].
    /// The pill opens a single MenuFlyout grouping Session / Model / Reasoning sections.</summary>
    InlinePill,
    /// <summary>Single row: textbox + Send. Everything else hides under a More menu.</summary>
    Minimal,
}

/// <summary>
/// Visual treatment for a contiguous run of <c>ToolCall</c> entries (a
/// "tool burst") that all belong to the same assistant turn. The burst is
/// rendered as a single unified card in <see cref="OpenClaw.Tray.WinUI.Chat.OpenClawChatTimeline"/>;
/// this enum controls how that card is framed at the *task* level.
/// </summary>
public enum ToolBurstStyle
{
    /// <summary>No task framing. Just rows + a single trailing "Tool · time" footer.</summary>
    Plain,
    /// <summary>Card-top header row "⚡ Task · N steps   [overall status]".
    /// Mirrors Cursor's "Tool calls (N steps)" treatment.</summary>
    TaskHeader,
    /// <summary>Single collapsed summary row "▸ ⚡ Task · N steps (a, b, c) [Done]".
    /// Click to expand the per-step list. Highest information density.</summary>
    CompactSummary,
    /// <summary>Plain rows but the trailing footer is reframed as
    /// "Task · N steps · time" so the task semantics are still surfaced.</summary>
    FooterReframe,
    /// <summary>Per-step list with a status icon per row: ✓ for done,
    /// spinning <c>ProgressRing</c> for in-progress, ✕ for errored.
    /// Mirrors the AgentRunCard "Running steps / Completed steps" pattern
    /// from native-chat-v2.</summary>
    TaskList,
    /// <summary>Smart default — picks per burst state:
    /// running bursts render Plain (per-step status visible);
    /// terminal multi-step bursts collapse to CompactSummary (1-line summary,
    /// click chevron to expand); single-step bursts stay Plain.
    /// Matches Scott's feedback: keep live progress visible, fold completed
    /// work into a tidy one-liner once the turn finishes.</summary>
    Auto,
}

/// <summary>
/// Process-wide live state for the chat exploration toggles. Mirrors the
/// dependency-property surface of v2's <c>ChatExplorationPreview</c> XAML
/// control, but as plain static properties so the native chat tree
/// (<see cref="OpenClawChatRoot"/>, <see cref="OpenClawChatTimeline"/>,
/// <see cref="OpenClawComposer"/>) can read them at render time and any
/// component can subscribe to <see cref="Changed"/> to invalidate.
///
/// Not persisted — resets every app launch (debug/exploration tool only).
/// Follows the same static + <c>Changed</c> event pattern as
/// <see cref="DebugChatSurfaceOverrides"/>.
/// </summary>
public static class ChatExplorationState
{
    // Defaults mirror v2 PropertyMetadata.

    private static ChatPreviewState _previewState = ChatPreviewState.Live;
    private static ChatVariation _variation = ChatVariation.Calm;
    // Defaults below were promoted from Kenny's `preset1` (the previous
    // per-user IsDefault preset under %APPDATA%\OpenClawTray) so a fresh
    // install lands on the same look without needing the JSON preset file.
    // Notable diffs vs the original code defaults:
    //   BackdropMode      Mica          → Acrylic
    //   PaddingDensity    Comfortable   → Cozy
    //   AvatarMode        Both          → AgentOnly
    //   ComposerIconSize  14            → 16
    //   SendButtonSize    32            → 40
    private static ChatBackdropMode _backdropMode = ChatBackdropMode.Acrylic;
    private static ChatPreviewTheme _previewTheme = ChatPreviewTheme.System;
    private static bool _usesHostBackdrop;

    private static double _bubbleCornerRadius = 16d;
    private static double _gutter = 64d;
    private static double _messageGap = 12d;
    private static ChatPaddingDensity _paddingDensity = ChatPaddingDensity.Cozy;
    private static ChatUserBubbleTone _userBubbleTone = ChatUserBubbleTone.Secondary;
    private static bool _showTimestamps = true;

    private static bool _showAvatars = true;
    private static ChatAvatarMode _avatarMode = ChatAvatarMode.AgentOnly;

    private static ChatComposerLayout _composerLayout = ChatComposerLayout.ThreeRow;
    private static double _composerCornerRadius = 8d;
    private static double _composerIconSize = 16d;
    private static double _sendButtonSize = 40d;

    private static Brush? _accentBrushOverride;
    private static Brush? _userBubbleBrushOverride;
    private static Brush? _assistantBubbleBrushOverride;
    private static Brush? _sendButtonBrushOverride;

    // ── v2 additions: bubble/tool visibility, size, footer details, icon customization ──
    private static bool _showAssistantBubbles = true;
    private static bool _showToolCalls = true;
    private static double _bubbleMaxWidth = 560d;
    private static double _bubbleSideMargin = 8d;

    private static bool _showSenderName = false;
    private static bool _showModelName = false;
    private static bool _showTokens = true;
    private static bool _showContextPercent = true;

    // Default icon glyphs match production OpenClawComposer.cs.
    private static string _sendIconGlyph   = "\uE724";
    private static bool   _sendIconShow    = true;
    private static string _attachIconGlyph = "\uE723";
    private static bool   _attachIconShow  = true;
    private static string _voiceIconGlyph  = "\uE720";
    private static bool   _voiceIconShow   = true;
    private static string _moreIconGlyph   = "\uE712";
    private static bool   _moreIconShow    = true;
    private static string _stopIconGlyph   = "\uE71A";
    private static bool   _stopIconShow    = true;

    // ---- Preview state override (G) ----

    /// <summary>
    /// When set to anything other than <see cref="ChatPreviewState.Live"/>,
    /// <see cref="OpenClawChatRoot"/> bypasses the real provider data and
    /// renders a synthesized version of the matching lifecycle state.
    /// </summary>
    public static ChatPreviewState PreviewState
    {
        get => _previewState;
        set { if (_previewState != value) { _previewState = value; RaiseChanged(); } }
    }

    // ---- Surface (A) ----

    public static ChatVariation Variation
    {
        get => _variation;
        set { if (_variation != value) { _variation = value; RaiseChanged(); } }
    }

    public static ChatBackdropMode BackdropMode
    {
        get => _backdropMode;
        set { if (_backdropMode != value) { _backdropMode = value; RaiseChanged(); } }
    }

    public static ChatPreviewTheme PreviewTheme
    {
        get => _previewTheme;
        set { if (_previewTheme != value) { _previewTheme = value; RaiseChanged(); } }
    }

    /// <summary>
    /// When true, the chat is hosted inside a window/page that already paints a
    /// SystemBackdrop, so backdrop changes are no-ops at the chat-surface level
    /// (the host owns the backdrop). Mirrors v2 <c>UsesHostBackdrop</c>.
    /// </summary>
    public static bool UsesHostBackdrop
    {
        get => _usesHostBackdrop;
        set { if (_usesHostBackdrop != value) { _usesHostBackdrop = value; RaiseChanged(); } }
    }

    // ---- Bubble / Layout (C) ----

    public static double BubbleCornerRadius
    {
        get => _bubbleCornerRadius;
        set { if (_bubbleCornerRadius != value) { _bubbleCornerRadius = value; RaiseChanged(); } }
    }

    public static double Gutter
    {
        get => _gutter;
        set { if (_gutter != value) { _gutter = value; RaiseChanged(); } }
    }

    public static double MessageGap
    {
        get => _messageGap;
        set { if (_messageGap != value) { _messageGap = value; RaiseChanged(); } }
    }

    public static ChatPaddingDensity PaddingDensity
    {
        get => _paddingDensity;
        set { if (_paddingDensity != value) { _paddingDensity = value; RaiseChanged(); } }
    }

    public static ChatUserBubbleTone UserBubbleTone
    {
        get => _userBubbleTone;
        set { if (_userBubbleTone != value) { _userBubbleTone = value; RaiseChanged(); } }
    }

    public static bool ShowTimestamps
    {
        get => _showTimestamps;
        set { if (_showTimestamps != value) { _showTimestamps = value; RaiseChanged(); } }
    }

    // ---- Avatar (D) ----

    public static bool ShowAvatars
    {
        get => _showAvatars;
        set { if (_showAvatars != value) { _showAvatars = value; RaiseChanged(); } }
    }

    public static ChatAvatarMode AvatarMode
    {
        get => _avatarMode;
        set { if (_avatarMode != value) { _avatarMode = value; RaiseChanged(); } }
    }

    // ---- Composer (E) ----

    public static ChatComposerLayout ComposerLayout
    {
        get => _composerLayout;
        set { if (_composerLayout != value) { _composerLayout = value; RaiseChanged(); } }
    }

    public static double ComposerCornerRadius
    {
        get => _composerCornerRadius;
        set { if (_composerCornerRadius != value) { _composerCornerRadius = value; RaiseChanged(); } }
    }

    public static double ComposerIconSize
    {
        get => _composerIconSize;
        set { if (_composerIconSize != value) { _composerIconSize = value; RaiseChanged(); } }
    }

    public static double SendButtonSize
    {
        get => _sendButtonSize;
        set { if (_sendButtonSize != value) { _sendButtonSize = value; RaiseChanged(); } }
    }

    // ---- Brush overrides (F). null = use theme/accent default. ----

    public static Brush? AccentBrushOverride
    {
        get => _accentBrushOverride;
        set { if (!ReferenceEquals(_accentBrushOverride, value)) { _accentBrushOverride = value; RaiseChanged(); } }
    }

    public static Brush? UserBubbleBrushOverride
    {
        get => _userBubbleBrushOverride;
        set { if (!ReferenceEquals(_userBubbleBrushOverride, value)) { _userBubbleBrushOverride = value; RaiseChanged(); } }
    }

    public static Brush? AssistantBubbleBrushOverride
    {
        get => _assistantBubbleBrushOverride;
        set { if (!ReferenceEquals(_assistantBubbleBrushOverride, value)) { _assistantBubbleBrushOverride = value; RaiseChanged(); } }
    }

    public static Brush? SendButtonBrushOverride
    {
        get => _sendButtonBrushOverride;
        set { if (!ReferenceEquals(_sendButtonBrushOverride, value)) { _sendButtonBrushOverride = value; RaiseChanged(); } }
    }

    /// <summary>
    /// Fires whenever any toggle or override changes. Subscribers should
    /// invalidate any cached visuals derived from these values.
    /// </summary>
    public static event EventHandler? Changed;

    private static void RaiseChanged() => Changed?.Invoke(null, EventArgs.Empty);

    // ──────────────────────────────────────────────────────────────────
    // v2 additions
    // ──────────────────────────────────────────────────────────────────

    // ---- Bubble / tool visibility + sizing (extends C) ----

    public static bool ShowAssistantBubbles
    {
        get => _showAssistantBubbles;
        set { if (_showAssistantBubbles != value) { _showAssistantBubbles = value; RaiseChanged(); } }
    }

    public static bool ShowToolCalls
    {
        get => _showToolCalls;
        set { if (_showToolCalls != value) { _showToolCalls = value; RaiseChanged(); } }
    }

    /// <summary>
    /// Monotonic counter incremented when all tool chip expanded states
    /// should be reset. The timeline checks this and clears its local
    /// <c>expandedToolChips</c> set when the value changes.
    /// </summary>
    public static int CollapseToolChipsVersion { get; internal set; }

    /// <summary>Signal all tool chip expanded states to collapse.</summary>
    public static void CollapseAllToolChips()
    {
        CollapseToolChipsVersion++;
        RaiseChanged();
    }

    public static double BubbleMaxWidth
    {
        get => _bubbleMaxWidth;
        set { if (_bubbleMaxWidth != value) { _bubbleMaxWidth = value; RaiseChanged(); } }
    }

    /// <summary>Distance between the bubble and the avatar (or the wall when avatar hidden).</summary>
    public static double BubbleSideMargin
    {
        get => _bubbleSideMargin;
        set { if (_bubbleSideMargin != value) { _bubbleSideMargin = value; RaiseChanged(); } }
    }

    // ---- Footer detail toggles (extends ShowTimestamps) ----

    public static bool ShowSenderName
    {
        get => _showSenderName;
        set { if (_showSenderName != value) { _showSenderName = value; RaiseChanged(); } }
    }

    public static bool ShowModelName
    {
        get => _showModelName;
        set { if (_showModelName != value) { _showModelName = value; RaiseChanged(); } }
    }

    public static bool ShowTokens
    {
        get => _showTokens;
        set { if (_showTokens != value) { _showTokens = value; RaiseChanged(); } }
    }

    public static bool ShowContextPercent
    {
        get => _showContextPercent;
        set { if (_showContextPercent != value) { _showContextPercent = value; RaiseChanged(); } }
    }

    // ---- Composer icon customization (extends E) ----

    public static string SendIconGlyph   { get => _sendIconGlyph;   set { if (_sendIconGlyph   != value) { _sendIconGlyph   = value ?? ""; RaiseChanged(); } } }
    public static bool   SendIconShow    { get => _sendIconShow;    set { if (_sendIconShow    != value) { _sendIconShow    = value;     RaiseChanged(); } } }
    public static string AttachIconGlyph { get => _attachIconGlyph; set { if (_attachIconGlyph != value) { _attachIconGlyph = value ?? ""; RaiseChanged(); } } }
    public static bool   AttachIconShow  { get => _attachIconShow;  set { if (_attachIconShow  != value) { _attachIconShow  = value;     RaiseChanged(); } } }
    public static string VoiceIconGlyph  { get => _voiceIconGlyph;  set { if (_voiceIconGlyph  != value) { _voiceIconGlyph  = value ?? ""; RaiseChanged(); } } }
    public static bool   VoiceIconShow   { get => _voiceIconShow;   set { if (_voiceIconShow   != value) { _voiceIconShow   = value;     RaiseChanged(); } } }
    public static string MoreIconGlyph   { get => _moreIconGlyph;   set { if (_moreIconGlyph   != value) { _moreIconGlyph   = value ?? ""; RaiseChanged(); } } }
    public static bool   MoreIconShow    { get => _moreIconShow;    set { if (_moreIconShow    != value) { _moreIconShow    = value;     RaiseChanged(); } } }
    public static string StopIconGlyph   { get => _stopIconGlyph;   set { if (_stopIconGlyph   != value) { _stopIconGlyph   = value ?? ""; RaiseChanged(); } } }
    public static bool   StopIconShow    { get => _stopIconShow;    set { if (_stopIconShow    != value) { _stopIconShow    = value;     RaiseChanged(); } } }

    // ---- Tool burst (H) ----

    private static ToolBurstStyle _toolBurstStyle = ToolBurstStyle.Auto;
    private static bool _showStepNumbers;

    /// <summary>
    /// Visual framing for a run of consecutive ToolCall entries (multi-step
    /// assistant turn). See <see cref="ToolBurstStyle"/> for the variants.
    /// </summary>
    public static ToolBurstStyle ToolBurstStyle
    {
        get => _toolBurstStyle;
        set { if (_toolBurstStyle != value) { _toolBurstStyle = value; RaiseChanged(); } }
    }

    /// <summary>
    /// When true, prefix each step row with its 1-based index ("1.", "2.", …)
    /// so the sequence is explicit. Has no effect on single-step bursts.
    /// </summary>
    public static bool ShowStepNumbers
    {
        get => _showStepNumbers;
        set { if (_showStepNumbers != value) { _showStepNumbers = value; RaiseChanged(); } }
    }
}
