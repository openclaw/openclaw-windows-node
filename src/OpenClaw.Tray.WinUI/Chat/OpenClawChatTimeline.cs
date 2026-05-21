using OpenClaw.Chat;
using OpenClawTray.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using OpenClawTray.Chat.Explorations;
using Windows.UI;
using static OpenClawTray.FunctionalUI.Factories;
using static OpenClawTray.FunctionalUI.Core.Theme;

namespace OpenClawTray.Chat;

/// <summary>
/// Extension of <see cref="ChatTimelineProps"/> with OpenClaw-specific
/// per-entry metadata (<see cref="ChatEntryMetadata"/>) and sender/model
/// labels used in the per-message footer rendering. Created by
/// <c>OpenClawChatRoot</c>.
/// </summary>
/// <param name="EntryMetadata">
/// Optional per-entry metadata snapshot keyed by <c>ChatTimelineItem.Id</c>.
/// Renderer falls back to defaults when an entry isn't present.
/// </param>
/// <param name="UserSenderLabel">Sender label shown below user bubbles.</param>
/// <param name="AssistantSenderLabel">Sender label shown below assistant cards.</param>
/// <param name="DefaultModel">Fallback model name when an entry's metadata doesn't carry one.</param>
/// <param name="ShowThinkingIndicator">
/// When true, renders an inline "<c>&lt;agent&gt; is thinking…</c>" placeholder
/// at the bottom of the timeline. Used by callers to bridge the gap between
/// turn-start and the first assistant delta arriving.
/// </param>
public record OpenClawChatTimelineProps(
    string? SessionId,
    IReadOnlyList<ChatTimelineItem> Entries,
    bool HasMoreHistory,
    Action? OnLoadMoreHistory,
    IReadOnlyDictionary<string, ChatEntryMetadata>? EntryMetadata = null,
    string UserSenderLabel = "OpenClaw Windows Tray",
    string AssistantSenderLabel = "Field",
    string? DefaultModel = null,
    bool ShowThinkingIndicator = false,
    Func<string, Task>? OnReadAloud = null);

/// <summary>
/// OpenClaw-skinned variant of <see cref="ChatTimeline"/> from the vendored
/// chat sample. Reuses the same scroll/follow/load-more behavior but renames
/// the per-entry rendering to better match the web Control UI:
///
/// <list type="bullet">
///   <item>User messages: right-aligned pink bubble with avatar glyph and a
///         "<c>&lt;sender&gt; · &lt;time&gt;</c>" footer.</item>
///   <item>Assistant messages: left-aligned subtle card with ★ avatar glyph
///         and a "<c>&lt;agent&gt; · &lt;time&gt; · &lt;model&gt;</c>" footer.</item>
///   <item>Tool calls: prominent compact rounded card matching the web's
///         "Tool call exec" affordance, with a small footer for time.</item>
///   <item>Reasoning / status entries: muted styling as in upstream.</item>
/// </list>
/// </summary>
public class OpenClawChatTimeline : Component<OpenClawChatTimelineProps>
{
    const double FollowThreshold = 60;

    // SECURITY (chat-rubber-duck HIGH 1 / MEDIUM 3): chat-bubble Markdown is
    // rendered as sanitized inert text that:
    //   1. Renders images as inert ``[Image: <alt>]`` text (no Uri fetch) —
    //      blocks SSRF / tracking-pixel beacons triggered by a compromised
    //      gateway, malicious tool output, or a prompt-injected model.
    //   2. Pre-strips inline link / image / ref-def syntax via
    //      <see cref="ChatMarkdownSanitizer.Sanitize(string?)"/> so explicit
    //      ``[text](url)`` syntax never reaches the parser.
    //   3. Renders raw HTML blocks as selectable plain text.
    //      Net effect: no click-to-navigate hyperlink or network-fetching
    //      image can be manufactured by untrusted Markdown inside a chat bubble.
    private static Element SafeMarkdownText(string? text) =>
        TextBlock(string.Empty)
            .Set(t =>
            {
                t.TextWrapping = TextWrapping.Wrap;
                t.IsTextSelectionEnabled = true;
                ApplySafeMarkdownInlines(t, text);
            });

    // Cache parsed markdown text per TextBlock to avoid re-clearing and
    // rebuilding Inlines on every re-render when message content is stable.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, string>
        s_markdownCache = new();

    private static void ApplySafeMarkdownInlines(TextBlock textBlock, string? text)
    {
        // Skip re-parsing only when text is unchanged AND inlines are still
        // present. ConfigureTextBlock sets control.Text="" which clears
        // Inlines, so we must re-apply even if the source text matches.
        if (textBlock.Inlines.Count > 0
            && s_markdownCache.TryGetValue(textBlock, out var cached)
            && cached == text)
            return;
        s_markdownCache.AddOrUpdate(textBlock, text ?? "");

        textBlock.Inlines.Clear();

        foreach (var segment in ChatMarkdownSanitizer.SanitizeAndSplitStrongEmphasis(text))
        {
            if (segment.Text.Length == 0)
                continue;

            if (segment.IsStrong)
            {
                var bold = new Bold();
                bold.Inlines.Add(new Run { Text = segment.Text });
                textBlock.Inlines.Add(bold);
            }
            else
            {
                textBlock.Inlines.Add(new Run { Text = segment.Text });
            }
        }
    }

    static string FormatToolLabel(ChatTimelineItem e)    {
        var text = e.Text ?? "";
        return e.ToolName switch
        {
            "bash" or "powershell" => $"$ {text}",
            "read" or "view" => text,
            "edit" or "create" => text,
            "grep" => $"🔍 {text}",
            "glob" => $"📂 {text}",
            "web_fetch" => $"🌐 {text}",
            "web_search" => $"🔎 {text}",
            "task" => text,
            "report_intent" => text,
            _ => text == e.ToolName || string.IsNullOrEmpty(text) ? e.ToolName ?? "tool" : $"{e.ToolName}: {text}"
        };
    }

    /// <summary>
    /// Title-case a single token: <c>"exec"</c> → <c>"Exec"</c>. Used by the
    /// tool-chip inner header to mirror the web's <c>Exec</c>/<c>Process</c>
    /// styling. Returns the empty string for null/empty input.
    /// </summary>
    static string CapitalizeFirst(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return char.ToUpperInvariant(s[0]) + (s.Length > 1 ? s[1..] : string.Empty);
    }

    /// <summary>
    /// If <paramref name="text"/> looks like a JSON object/array, pretty-print
    /// it with 2-space indentation. Otherwise return the string verbatim.
    /// Used so tool chips render gateway action blobs (<c>{"action":"poll"…}</c>)
    /// the same way the web does, without affecting plain shell output.
    /// </summary>
    static string TryFormatJsonForDisplay(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0) return text;
        var first = trimmed[0];
        if (first != '{' && first != '[') return text;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
            return System.Text.Json.JsonSerializer.Serialize(doc.RootElement,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return text;
        }
    }

    static bool ContainsEntryId(IReadOnlyList<ChatTimelineItem> entries, string id)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Id == id)
                return true;
        }

        return false;
    }

    static double ClampOffset(double offset, double max) =>
        Math.Max(0, Math.Min(offset, max));

    public override Element Render()
    {
        // Subscribe to ChatExplorationState so toggles live-rerender the
        // timeline. Same inline pattern as OpenClawComposer (UseState +
        // UseEffect — extension methods can't access protected hooks).
        var explorationRev = UseState(0, threadSafe: true);
        UseEffect((Func<Action>)(() =>
        {
            EventHandler h = (_, _) => explorationRev.Set(explorationRev.Value + 1);
            ChatExplorationState.Changed += h;
            return () => ChatExplorationState.Changed -= h;
        }));

        // Live values from ChatExplorationState (groups C/D/F).
        var bubbleRadius     = ChatVisualResolver.BubbleCornerRadius();
        var bubblePadding    = ChatVisualResolver.BubbleInnerPadding();
        var bubbleSideMargin = ChatVisualResolver.BubbleSideMargin();
        var showAsstBubbles  = ChatVisualResolver.ShowAssistantBubbles();
        var showToolCalls    = ChatVisualResolver.ShowToolCalls();
        var gutter           = ChatExplorationState.Gutter;
        var messageGap       = ChatExplorationState.MessageGap;
        var showUserAvatar   = ChatVisualResolver.ShowUserAvatar();
        var showAssistAvatar = ChatVisualResolver.ShowAssistantAvatar();
        var showTimestamps   = ChatVisualResolver.ShowTimestamps();

        var scrollViewRef = UseRef<Microsoft.UI.Xaml.Controls.ScrollViewer?>(null);
        var isFollowingRef = UseRef(true);
        var contentRef = UseRef<Microsoft.UI.Xaml.Controls.StackPanel?>(null);
        var prevEntryCountRef = UseRef(0);
        var prevSessionIdRef = UseRef<string?>(null);
        var prevFirstEntryIdRef = UseRef<string?>(null);
        var prevLastEntryIdRef = UseRef<string?>(null);
        var lastVerticalOffsetRef = UseRef(0.0);
        var lastScrollableHeightRef = UseRef(0.0);
        var suppressAutoFollowRef = UseRef(false);
        var sessionOffsetsRef = UseRef<Dictionary<string, double>>(new());
        var hasMoreHistoryRef = UseRef(Props.HasMoreHistory);
        var loadMoreHistoryRef = UseRef<Action?>(Props.OnLoadMoreHistory);
        var loadMoreRequestedForCountRef = UseRef(-1);

        // Per-entry expand state for tool chips. Tokens are
        // "{entryId}:call" and "{entryId}:out" so call and output
        // toggle independently. HashSet so the empty default is "all
        // collapsed" — matches the web's default-collapsed look.
        var expandedToolChips = UseState<HashSet<string>>(new HashSet<string>(), threadSafe: true);

        // Hover state — set of entry ids currently under the pointer. Used to
        // reveal the trash / speak action icons beside user / assistant
        // bubbles. Re-renders the whole timeline on hover transitions; that's
        // fine for the entry counts we deal with (typically <100 visible).
        var hoveredEntries = UseState<HashSet<string>>(new HashSet<string>(), threadSafe: true);

        // Thinking-row dot animation. Cycles 0→1→2→3→0 every 400ms while the
        // ShowThinkingIndicator prop is true; drives the trailing "." / ".." /
        // "..." in the "<name> is thinking" caption so the row visibly pulses
        // without needing a ProgressRing (which renders awkwardly at small
        // sizes). DispatcherTimer fires on the UI thread so the reducer call
        // is safe. UseReducer (not UseState) because the timer-tick closure
        // re-reads on each fire — UseState.Value is a render-time snapshot,
        // so a long-lived timer would forever advance from the same stale
        // value. (Same reason as the AckAction reducer below.)
        var (thinkingDotPhase, thinkingDotPhaseUpdate) = UseReducer<int>(0, threadSafe: true);
        UseEffect((Func<Action>)(() =>
        {
            if (!Props.ShowThinkingIndicator)
                return () => { };
            var timer = new Microsoft.UI.Xaml.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            timer.Tick += (_, _) => thinkingDotPhaseUpdate(prev => (prev + 1) % 4);
            timer.Start();
            return () => timer.Stop();
        }), Props.ShowThinkingIndicator);

        // Acknowledged actions — set of "entryId|actionKey" strings briefly
        // marked after a click so the icon can swap to a checkmark for ~1.2s
        // before reverting. Gives the user immediate "done" feedback for
        // Copy / Read aloud / Delete without a toast.
        // UseReducer (not UseState) so the updater always sees the LIVE
        // hook value — UseState's `.Value` is a render-time snapshot, so
        // a delayed continuation that reads it later sees a stale set and
        // bails out, leaving the ack glyph stuck.
        var (ackedActionsValue, ackUpdate) = UseReducer<HashSet<string>>(new HashSet<string>(), threadSafe: true);
        async void AckAction(string entryId, string actionKey)
        {
            var key = entryId + "|" + actionKey;
            ackUpdate(prev =>
            {
                if (prev.Contains(key)) return prev;
                return new HashSet<string>(prev) { key };
            });
            var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            await Task.Delay(700);
            void Clear() => ackUpdate(prev =>
            {
                if (!prev.Contains(key)) return prev;
                var nxt = new HashSet<string>(prev);
                nxt.Remove(key);
                return nxt;
            });
            if (dq is null) Clear();
            else dq.TryEnqueue(Clear);
        }

        hasMoreHistoryRef.Current = Props.HasMoreHistory;
        loadMoreHistoryRef.Current = Props.OnLoadMoreHistory;

        var entryCount = Props.Entries.Count;
        var firstEntryId = entryCount > 0 ? Props.Entries[0].Id : null;
        var lastEntryId = entryCount > 0 ? Props.Entries[entryCount - 1].Id : null;
        var previousSessionId = prevSessionIdRef.Current;
        var previousEntryCount = prevEntryCountRef.Current;
        var previousFirstEntryId = prevFirstEntryIdRef.Current;
        var previousLastEntryId = prevLastEntryIdRef.Current;
        var sessionChanged = Props.SessionId != previousSessionId;
        var initialLoad = !sessionChanged && previousEntryCount == 0 && entryCount > 0;
        var prependedHistory = !sessionChanged
            && previousEntryCount > 0
            && entryCount > previousEntryCount
            && previousFirstEntryId is not null
            && firstEntryId != previousFirstEntryId
            && lastEntryId == previousLastEntryId
            && ContainsEntryId(Props.Entries, previousFirstEntryId);
        var appendedEntries = !sessionChanged
            && entryCount > previousEntryCount
            && !prependedHistory;

        void StoreSessionOffset(string? sessionId, double offset)
        {
            if (sessionId is { Length: > 0 })
                sessionOffsetsRef.Current[sessionId] = offset;
        }

        void UpdateScrollMetrics(Microsoft.UI.Xaml.Controls.ScrollViewer sv)
        {
            lastVerticalOffsetRef.Current = sv.VerticalOffset;
            lastScrollableHeightRef.Current = sv.ScrollableHeight;
            isFollowingRef.Current = sv.ScrollableHeight - sv.VerticalOffset <= FollowThreshold;
            StoreSessionOffset(prevSessionIdRef.Current, sv.VerticalOffset);
        }

        void QueueScrollToOffset(Microsoft.UI.Xaml.Controls.ScrollViewer sv, string? sessionId, double targetOffset, bool disableAnimation, bool suppressAutoFollow)
        {
            suppressAutoFollowRef.Current = suppressAutoFollow;
            sv.DispatcherQueue.TryEnqueue(() =>
            {
                var target = ClampOffset(targetOffset, sv.ScrollableHeight);
                sv.ChangeView(null, target, null, disableAnimation);
                lastVerticalOffsetRef.Current = target;
                lastScrollableHeightRef.Current = sv.ScrollableHeight;
                isFollowingRef.Current = sv.ScrollableHeight - target <= FollowThreshold;
                StoreSessionOffset(sessionId, target);

                if (suppressAutoFollow)
                    sv.DispatcherQueue.TryEnqueue(() => suppressAutoFollowRef.Current = false);
            });
        }

        void QueueScrollToBottom(Microsoft.UI.Xaml.Controls.ScrollViewer sv, string? sessionId, bool disableAnimation)
        {
            isFollowingRef.Current = true;
            sv.DispatcherQueue.TryEnqueue(() =>
            {
                var bottom = sv.ScrollableHeight;
                sv.ChangeView(null, bottom, null, disableAnimation);
                lastVerticalOffsetRef.Current = bottom;
                lastScrollableHeightRef.Current = sv.ScrollableHeight;
                isFollowingRef.Current = true;
                StoreSessionOffset(sessionId, bottom);
            });
        }

        void QueuePreservePrependOffset(Microsoft.UI.Xaml.Controls.ScrollViewer sv, string? sessionId, double oldOffset, double oldScrollableHeight)
        {
            suppressAutoFollowRef.Current = true;
            sv.DispatcherQueue.TryEnqueue(() =>
            {
                var delta = sv.ScrollableHeight - oldScrollableHeight;
                var target = ClampOffset(oldOffset + delta, sv.ScrollableHeight);
                sv.ChangeView(null, target, null, disableAnimation: true);
                lastVerticalOffsetRef.Current = target;
                lastScrollableHeightRef.Current = sv.ScrollableHeight;
                isFollowingRef.Current = sv.ScrollableHeight - target <= FollowThreshold;
                StoreSessionOffset(sessionId, target);
                sv.DispatcherQueue.TryEnqueue(() => suppressAutoFollowRef.Current = false);
            });
        }

        // Load more button — outside the repeated items
        var loadMoreButton = Props.HasMoreHistory
            ? Button(LocalizationHelper.GetString("Chat_Timeline_LoadEarlier"), () => Props.OnLoadMoreHistory?.Invoke())
                .HAlign(HorizontalAlignment.Center)
                .Set(b => { b.Padding = new Thickness(16, 8, 16, 8); b.CornerRadius = new CornerRadius(4); })
                .Resources(r => r
                    .Set("ButtonBackground", Ref("SubtleFillColorTransparentBrush"))
                    .Set("ButtonBackgroundPointerOver", Ref("SubtleFillColorSecondaryBrush"))
                    .Set("ButtonBackgroundPressed", Ref("SubtleFillColorTertiaryBrush"))
                    .Set("ButtonBorderBrush", Ref("SubtleFillColorTransparentBrush")))
                .Margin(0, 8, 0, 8)
            : (Element)Empty();

        static Element TimelineInset(Element child, double top = 2, double bottom = 2) =>
            Border(child).Padding(36, top, 24, bottom);

        // ── OpenClaw skin: bubbled user vs. left-aligned assistant card ──

        var userSender = Props.UserSenderLabel;
        var assistantSender = Props.AssistantSenderLabel;
        var defaultModel = Props.DefaultModel;
        var meta = Props.EntryMetadata;

        // ── Web Control UI palette: "dash-light" theme (verified against the
        // bundled assets/index-*.css — dash-light is what the user runs).
        // Colors here mirror the CSS variables exactly so bubbles/avatars
        // look identical to the web at http://localhost:18789/chat.
        // ──────────────────────────────────────────────────────────────
        // ── Kenny Hong palette (kenehong/native-chat-v2): Microsoft Fluent
        // ``AccentFillColorDefaultBrush`` for the user bubble (white text on
        // accent), ``SubtleFillColorSecondaryBrush`` for the assistant bubble
        // and page background. All looked up from the theme so they react to
        // light/dark mode and high-contrast settings without manual swaps.
        // ─────────────────────────────────────────────────────────────────
        Brush themeBrush(string key) => (Brush)Microsoft.UI.Xaml.Application.Current.Resources[key];
        // When the host window paints a non-Solid SystemBackdrop (Mica / MicaAlt /
        // Acrylic), let it show through by using a transparent chat-page fill.
        // Otherwise fall back to the subtle layer color so Solid mode still
        // reads as a flat surface.
        var chatPageBg = ChatExplorationState.BackdropMode == ChatBackdropMode.Solid
            ? themeBrush("SubtleFillColorSecondaryBrush")
            : (Brush)new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var assistantBubbleBg   = ChatVisualResolver.AssistantBubbleBrush(themeBrush("SubtleFillColorSecondaryBrush"));
        var assistantBubbleBdr  = themeBrush("ControlStrokeColorDefaultBrush");
        var userBubbleBg        = ChatVisualResolver.UserBubbleBrush(themeBrush("AccentFillColorDefaultBrush"));
        var userBubbleBdr       = themeBrush("AccentFillColorDefaultBrush");
        var userBubbleFg        = themeBrush("TextOnAccentFillColorPrimaryBrush");
        var avatarPanelBg       = themeBrush("SubtleFillColorTertiaryBrush");
        var avatarBorder        = themeBrush("ControlStrokeColorDefaultBrush");
        var assistantAvatarFg   = themeBrush("TextFillColorSecondaryBrush");
        var userAvatarBg        = ChatVisualResolver.AccentBrush(themeBrush("AccentFillColorDefaultBrush"));
        var userAvatarFg        = themeBrush("TextOnAccentFillColorPrimaryBrush");
        // a11y: timestamps and "is thinking" caption sit directly on the
        // window backdrop. On Mica/Acrylic the system tint is translucent,
        // so Tertiary text can fall below WCAG AA. Bump to Secondary when
        // the chat surface is transparent over a host backdrop.
        var chatStampFg         = ChatExplorationState.BackdropMode == ChatBackdropMode.Solid
            ? themeBrush("TextFillColorTertiaryBrush")
            : themeBrush("TextFillColorSecondaryBrush");
        var chatTextFg          = themeBrush("TextFillColorPrimaryBrush");
        // Tool chips: very subtle background tint + light border so they
        // read as a secondary surface distinct from the filled assistant
        // bubble without looking like an empty outlined box.
        var toolCardBgBrush     = themeBrush("LayerOnAcrylicFillColorDefaultBrush");
        var toolCardBorderBrush = themeBrush("ControlStrokeColorDefaultBrush");

        // Avatar: 36×36 circle (Kenny uses circular avatars). Same constructor
        // as before but radius defaults to half the size for a perfect circle.
        Element AvatarBox(string glyph, Brush bg, Brush border, Brush fg, double size = 36, double radius = 18) =>
            Border(
                TextBlock(glyph)
                    .Set(t =>
                    {
                        t.HorizontalAlignment = HorizontalAlignment.Center;
                        t.VerticalAlignment = VerticalAlignment.Center;
                        t.FontSize = 13;
                        t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                        t.Foreground = fg;
                    })
            ).Background(bg).Size(size, size).CornerRadius(radius)
             .WithBorder(border, 1);

        // Assistant avatar: 36×36 circle showing the OpenClaw app icon (the
        // same PNG used by the tray and chat-window title bar) so the agent
        // identity is visually consistent across surfaces.
        Element AssistantAvatar(double size = 36, double radius = 18) =>
            Border(
                Image("ms-appx:///Assets/Square44x44Logo.targetsize-256_altform-unplated.png")
                    .Set(im =>
                    {
                        im.Stretch = Stretch.UniformToFill;
                        im.HorizontalAlignment = HorizontalAlignment.Stretch;
                        im.VerticalAlignment = VerticalAlignment.Stretch;
                    })
            ).Background(avatarPanelBg).Size(size, size).CornerRadius(radius)
             .WithBorder(avatarBorder, 1)
             .Set(b => b.Padding = new Thickness(0))
             .AutomationName($"{assistantSender} avatar");

        // Helper to format a timestamp as the web does: "h:mm tt" in local time.
        static string FormatTime(DateTimeOffset? ts) =>
            ts is { } v ? v.ToLocalTime().ToString("h:mm tt") : "";

        ChatEntryMetadata? MetaFor(string id) =>
            meta is not null && meta.TryGetValue(id, out var m) ? m : null;

        Element FooterCaption(string text, HorizontalAlignment align) =>
            Caption(text)
                .Foreground(chatStampFg)
                .Set(t => t.FontSize = 11)
                .HAlign(align);

        // Hover-revealed action icon (copy / read aloud / trash). Opacity 0
        // and not hit-testable until the entry is hovered, then fades in
        // and becomes clickable. Soft pill radius + Light weight glyph so
        // it feels friendlier than the standard MDL2 button look. When the
        // matching action is acknowledged (briefly after click) the glyph
        // swaps to a checkmark for instant visual feedback.
        Element HoverIcon(string entryId, string actionKey, string glyph, string ackGlyph,
            string tip, Action onClick)
        {
            var visible = hoveredEntries.Value.Contains(entryId);
            var acked = ackedActionsValue.Contains(entryId + "|" + actionKey);
            var shownGlyph = acked ? ackGlyph : glyph;
            var shownColor = acked ? themeBrush("SystemFillColorSuccessBrush") : chatStampFg;
            return Button(
                TextBlock(shownGlyph)
                    .Set(t =>
                    {
                        t.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
                        t.FontSize = 14;
                        t.FontWeight = Microsoft.UI.Text.FontWeights.Light;
                        t.Foreground = shownColor;
                    }),
                onClick
            ).Set(b =>
            {
                b.Padding = new Thickness(7, 5, 7, 5);
                b.MinWidth = 30; b.MinHeight = 26;
                b.CornerRadius = new CornerRadius(13);
                // Hide together with hover — once the pointer leaves the
                // bubble, the icon (whether ack'd or not) goes away too.
                b.Opacity = visible ? 1.0 : 0.0;
                b.IsHitTestVisible = visible;
            })
            .Resources(r => r
                .Set("ButtonBackground", new SolidColorBrush(Colors.Transparent))
                .Set("ButtonBackgroundPointerOver", themeBrush("SubtleFillColorSecondaryBrush"))
                .Set("ButtonBackgroundPressed", themeBrush("SubtleFillColorTertiaryBrush"))
                .Set("ButtonBorderBrush", new SolidColorBrush(Colors.Transparent))
                .Set("ButtonBorderBrushPointerOver", new SolidColorBrush(Colors.Transparent))
                .Set("ButtonBorderBrushPressed", new SolidColorBrush(Colors.Transparent)))
            .AutomationName(tip);
        }

        // Wrap a row with hover handlers that flip the entry id in
        // hoveredEntries on PointerEntered/Exited. Callers should wrap the
        // row in a Border with a transparent background so the WHOLE
        // bounding box (including the gap between bubble and footer) is
        // hit-testable — otherwise moving the pointer down to a
        // hover-revealed action button briefly exits the hover area and
        // hides the icon before the click lands.
        T WithHoverHandlers<T>(T el, string entryId) where T : Element
        {
            return el
                .OnPointerEntered((_, _) =>
                {
                    var current = hoveredEntries.Value;
                    if (current.Contains(entryId)) return;
                    var next = new HashSet<string>(current) { entryId };
                    hoveredEntries.Set(next);
                })
                .OnPointerExited((_, _) =>
                {
                    var current = hoveredEntries.Value;
                    if (current.Contains(entryId))
                    {
                        var next = new HashSet<string>(current);
                        next.Remove(entryId);
                        hoveredEntries.Set(next);
                    }
                    // Drop any pending ack glyph for this entry so the next
                    // hover starts fresh with the original copy/speak/trash
                    // icon instead of a stale checkmark.
                    var prefix = entryId + "|";
                    ackUpdate(prev =>
                    {
                        if (!prev.Any(k => k.StartsWith(prefix, StringComparison.Ordinal)))
                            return prev;
                        return new HashSet<string>(prev.Where(k => !k.StartsWith(prefix, StringComparison.Ordinal)));
                    });
                });
        }

        // Build the WebView-style multi-pill footer:
        //   "Field   7:54 PM   ↑1475   ↓12   R45.4k   23% ctx   gpt-5.5"
        // Each pill is a small caption; missing pieces (e.g. token counts not
        // reported) are silently skipped so the footer just shows what's
        // known. Not clickable yet — that's deferred until the gateway surfaces
        // the corresponding metadata.
        static string FormatTokenCount(int n) =>
            n >= 1000 ? $"{n / 1000.0:0.#}k" : n.ToString();

        // Copy assistant message text to the system clipboard. Strips a
        // light amount of markdown noise (fenced code backticks) so the
        // clipboard payload reads naturally when pasted into prose.
        static void CopyToClipboard(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                ClipboardHelper.CopyText(text, flush: true);
            }
            catch { /* clipboard contention — silently ignore */ }
        }

        async void ReadAloud(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (Props.OnReadAloud is not { } onReadAloud) return;

            await onReadAloud(StripMarkdownForSpeech(text));
        }

        // Very light markdown stripper so the synthesizer doesn't read
        // backticks, asterisks, link brackets, etc. Markdown rendering is
        // already done visually; this only cleans the spoken transcript.
        static string StripMarkdownForSpeech(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var s = System.Text.RegularExpressions.Regex.Replace(text, @"```[\s\S]*?```", " code block ");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"`([^`]+)`", "$1");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"!\[[^\]]*\]\([^)]*\)", " image ");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\[([^\]]+)\]\([^)]*\)", "$1");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"[*_#>]+", " ");
            return s;
        }

        Element BuildAssistantFooter(string sender, string time, string? model,
            int? inputTokens, int? outputTokens, int? responseTokens, int? contextPct,
            Brush stampFg,
            string entryId, string entryText)
        {
            // Honor per-field toggles from ChatExplorationState.
            var showSender   = ChatExplorationState.ShowSenderName;
            var showModel    = ChatExplorationState.ShowModelName;
            var showTokens   = ChatExplorationState.ShowTokens;
            var showCtxPct   = ChatExplorationState.ShowContextPercent;

            var parts = new List<Element>();
            void AddPill(string text)
            {
                if (string.IsNullOrEmpty(text)) return;
                parts.Add(Caption(text).Foreground(stampFg)
                    .Set(t => t.FontSize = 11)
                    .VAlign(VerticalAlignment.Center));
            }

            // Hover actions — Copy + Read aloud. Placed at the END of the
            // footer so the timestamp/sender stay anchored on the left and
            // the empty space (when not hovered) trails off harmlessly to
            // the right instead of leaving an awkward gap before the time.
            if (showSender && !string.IsNullOrEmpty(sender))
                parts.Add(Caption(sender).Foreground(stampFg)
                    .Set(t => { t.FontSize = 11; t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; })
                    .VAlign(VerticalAlignment.Center));

            AddPill(time);
            if (showTokens && inputTokens   is int inN)   AddPill($"↑{FormatTokenCount(inN)}");
            if (showTokens && outputTokens  is int outN)  AddPill($"↓{FormatTokenCount(outN)}");
            if (showTokens && responseTokens is int respN) AddPill($"R{FormatTokenCount(respN)}");
            if (showCtxPct && contextPct    is int pct)   AddPill($"{pct}% ctx");
            if (showModel) AddPill(model ?? "");

            parts.Add(HoverIcon(entryId, "copy", "\uE8C8", "\uE73E",
                LocalizationHelper.GetString("Chat_Assistant_Action_Copy"),
                () => { CopyToClipboard(entryText); AckAction(entryId, "copy"); }).VAlign(VerticalAlignment.Center));
            parts.Add(HoverIcon(entryId, "speak", "\uE767", "\uE73E",
                LocalizationHelper.GetString("Chat_Assistant_Action_ReadAloud"),
                () => { ReadAloud(entryText); AckAction(entryId, "speak"); }).VAlign(VerticalAlignment.Center));

            return (FlexRow(parts.ToArray()) with { ColumnGap = 8 })
                .HAlign(HorizontalAlignment.Left);
        }

        // User-bubble footer mirrors the assistant footer UX so the same
        // hover affordance shows up on both sides. Order is reversed for
        // the user side: hover actions sit on the LEFT and the timestamp
        // anchors the FAR RIGHT (closest to the bubble corner) — matches
        // the user's reading direction when the bubble is right-aligned.
        Element BuildUserFooter(string sender, string time, Brush stampFg,
            string entryId, string entryText)
        {
            var showSender = ChatExplorationState.ShowSenderName;
            var parts = new List<Element>
            {
                HoverIcon(entryId, "copy", "\uE8C8", "\uE73E",
                    LocalizationHelper.GetString("Chat_Assistant_Action_Copy"),
                    () => { CopyToClipboard(entryText); AckAction(entryId, "copy"); }).VAlign(VerticalAlignment.Center),
                // TODO: Restore this delete action once the chat provider can remove
                // prompts from both the local timeline and gateway history. Leaving
                // the no-op action visible is misleading because AckAction flashes
                // success even though nothing is deleted.
                // HoverIcon(entryId, "delete", "\uE74D", "\uE73E",
                //     LocalizationHelper.GetString("Chat_User_Action_Delete"),
                //     () => { /* TODO: wire to provider */ AckAction(entryId, "delete"); }).VAlign(VerticalAlignment.Center),
            };

            if (showSender && !string.IsNullOrEmpty(sender))
                parts.Add(Caption(sender).Foreground(stampFg)
                    .Set(t => { t.FontSize = 11; t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; })
                    .VAlign(VerticalAlignment.Center));

            if (!string.IsNullOrEmpty(time))
                parts.Add(Caption(time).Foreground(stampFg)
                    .Set(t => t.FontSize = 11)
                    .VAlign(VerticalAlignment.Center));

            return (FlexRow(parts.ToArray()) with { ColumnGap = 8 })
                .HAlign(HorizontalAlignment.Right);
        }

        Element RenderUserEntry(ChatTimelineItem entry, bool startsBurst, bool endsBurst)
        {
            // Detect attachment indicators injected by the send path.
            // Format: "\u200B🖼️ filename.png" or "\u200B📎 file.md" on its own line.
            // The zero-width space prefix prevents false positives from normal text.
            // When present, split into message text + attachment cards.
            var text = entry.Text ?? "";
            var lines = text.Split('\n');
            var messageLines = new List<string>();
            var attachmentNames = new List<(string Icon, string Name, bool IsImage)>();

            foreach (var line in lines)
            {
                var trimLine = line.Trim();
                if (trimLine.StartsWith("\u200B🖼️ "))
                    attachmentNames.Add(("🖼️", trimLine.Substring(4).Trim(), true));
                else if (trimLine.StartsWith("\u200B📎 "))
                    attachmentNames.Add(("📎", trimLine.Substring(3).Trim(), false));
                // Also match legacy format without zero-width space for backward compat
                else if (trimLine.StartsWith("🖼️ ") && trimLine.Length < 260)
                    attachmentNames.Add(("🖼️", trimLine.Substring(3).Trim(), true));
                else if (trimLine.StartsWith("📎 ") && trimLine.Length < 260)
                    attachmentNames.Add(("📎", trimLine.Substring(2).Trim(), false));
                else
                    messageLines.Add(line);
            }

            var messageText = string.Join('\n', messageLines).Trim();
            var hasMessage = !string.IsNullOrEmpty(messageText);
            var hasAttachments = attachmentNames.Count > 0;

            // Build attachment card(s) — distinct from the text bubble with
            // a subtle background, file icon, and filename. Right-aligned
            // like user bubbles but visually differentiated.
            Element attachmentCards = Empty();
            if (hasAttachments)
            {
                var cards = new List<Element>();
                foreach (var (icon, name, isImage) in attachmentNames)
                {
                    var fileGlyph = isImage ? "\uEB9F" : "\uE8A5"; // Photo / Page
                    var card = Border(
                        Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
                            Border(
                                TextBlock(fileGlyph)
                                    .Set(t =>
                                    {
                                        t.FontFamily = new FontFamily("Segoe Fluent Icons");
                                        t.FontSize = 16;
                                        t.Foreground = userBubbleFg;
                                        t.VerticalAlignment = VerticalAlignment.Center;
                                        t.HorizontalAlignment = HorizontalAlignment.Center;
                                    })
                            ).Size(32, 32)
                             .CornerRadius(6)
                             .Background(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)))
                             .Grid(row: 0, column: 0),
                            TextBlock(name)
                                .Set(t =>
                                {
                                    t.TextWrapping = TextWrapping.NoWrap;
                                    t.TextTrimming = TextTrimming.CharacterEllipsis;
                                    t.FontSize = 13;
                                    t.Foreground = userBubbleFg;
                                    t.VerticalAlignment = VerticalAlignment.Center;
                                    t.Margin = new Thickness(8, 0, 0, 0);
                                    t.MaxWidth = 240;
                                })
                                .Grid(row: 0, column: 1)
                        )
                    ).Set(b =>
                    {
                        b.CornerRadius = bubbleRadius;
                        b.Padding = new Thickness(10, 8, 14, 8);
                        b.VerticalAlignment = VerticalAlignment.Center;
                        b.BorderThickness = new Thickness(1);
                        b.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
                    }).Background(ChatVisualResolver.UserBubbleBrush(
                        themeBrush("AccentFillColorSecondaryBrush")));

                    cards.Add(card.HAlign(HorizontalAlignment.Right));
                }
                attachmentCards = VStack(4, cards.ToArray());
            }

            // Standard text bubble (only when there's actual text).
            Element bubble = Empty();
            if (hasMessage)
            {
                bubble = Border(
                    TextBlock(messageText)
                        .Set(t =>
                        {
                            t.TextWrapping = TextWrapping.Wrap;
                            t.FontSize = 14;
                            t.Foreground = userBubbleFg;
                            t.IsTextSelectionEnabled = true;
                        })
                ).Background(userBubbleBg)
                 .Set(b =>
                 {
                     b.CornerRadius = bubbleRadius;
                     b.Padding = bubblePadding;
                     b.VerticalAlignment = VerticalAlignment.Center;
                 });
            }

            // Combine: text bubble above attachment card(s).
            Element content;
            if (hasMessage && hasAttachments)
                content = VStack(4, bubble.HAlign(HorizontalAlignment.Right), attachmentCards);
            else if (hasAttachments)
                content = attachmentCards;
            else
                content = bubble.HAlign(HorizontalAlignment.Right);

            // Avatar shown only on the LAST entry of a same-sender burst,
            // and only when ChatExplorationState.AvatarMode allows. When
            // avatars are hidden entirely we drop the slot; mid-burst entries
            // still get a spacer so they stay aligned with the first bubble.
            Element rightSlot = !showUserAvatar
               ? Empty()
               : (endsBurst
                   ? AvatarBox("🧑", userAvatarBg, userBubbleBdr, userAvatarFg).VAlign(VerticalAlignment.Center)
                   : Border(Empty()).Size(36, 36));

            var bubbleRow = Grid(
                [GridSize.Star(), GridSize.Auto],
                [GridSize.Auto],
                content.HAlign(HorizontalAlignment.Right).Grid(row: 0, column: 0),
                rightSlot.Grid(row: 0, column: 1).Margin(showUserAvatar ? bubbleSideMargin : 0, 0, 0, 0)
            ).HAlign(HorizontalAlignment.Stretch);

            Element footer = Empty();
            if (endsBurst && showTimestamps)
            {
                var entryMeta = MetaFor(entry.Id);
                var timeStr = FormatTime(entryMeta?.Timestamp);
                var rightInset = showUserAvatar ? (36 + bubbleSideMargin) : 0;
                rightInset += (int)bubblePadding.Right;
                footer = BuildUserFooter(userSender, timeStr, chatStampFg, entry.Id, entry.Text ?? "")
                    .Margin(0, 2, rightInset, 0);
            }

            var topMargin = startsBurst ? 4.0 : 1.0;
            var bottomMargin = endsBurst ? 4.0 : 1.0;
            return WithHoverHandlers(
                Border(
                    VStack(2, bubbleRow, footer)
                        .HAlign(HorizontalAlignment.Stretch)
                ).Background(new SolidColorBrush(Colors.Transparent))
                 .Margin(gutter, topMargin, 16, bottomMargin),
                entry.Id);
        }

        Element RenderAssistantEntry(ChatTimelineItem entry, bool startsBurst, bool endsBurst, bool showAvatar)
        {
            if (string.IsNullOrEmpty(entry.Text))
                return Empty();

            // Hidden by user toggle — collapses entire assistant block.
            if (!showAsstBubbles)
                return Empty();

            // Avatar shown only on the FIRST entry of a contiguous agent-side
            // run. Continuation entries get no spacer — they align flush with
            // the tool burst cards above (which also sit at the left inset),
            // so the agent column reads as a single vertical edge.
            Element leftSlot = !showAssistAvatar || !showAvatar
                ? Empty()
                : AssistantAvatar().VAlign(VerticalAlignment.Top);

            // Assistant bubble — subtle gray with primary text. Radius/Padding
            // come from ChatExplorationState (BubbleCornerRadius + PaddingDensity).
            var card = Border(
                SafeMarkdownText(entry.Text)
            ).Background(assistantBubbleBg)
             .Set(b =>
             {
                 b.CornerRadius = bubbleRadius;
                 b.Padding = bubblePadding;
             });

            var bubbleRow = Grid(
                [GridSize.Auto, GridSize.Star()],
                [GridSize.Auto],
                leftSlot.Grid(row: 0, column: 0).Margin(0, 0, showAssistAvatar && showAvatar ? bubbleSideMargin : 0, 0),
                card.HAlign(HorizontalAlignment.Left).Grid(row: 0, column: 1)
            ).HAlign(HorizontalAlignment.Stretch);

            Element footer = Empty();
            if (endsBurst && showTimestamps)
            {
                var entryMeta = MetaFor(entry.Id);
                var timeStr = FormatTime(entryMeta?.Timestamp);
                var modelStr = entryMeta?.Model ?? defaultModel;
                footer = BuildAssistantFooter(assistantSender, timeStr, modelStr,
                    entryMeta?.InputTokens, entryMeta?.OutputTokens,
                    entryMeta?.ResponseTokens, entryMeta?.ContextPercent,
                    chatStampFg, entry.Id, entry.Text ?? "");
                var leftInset = (showAssistAvatar && showAvatar) ? (36 + bubbleSideMargin) : 0;
                leftInset += (int)bubblePadding.Left;
                footer = footer.Margin(leftInset, 2, 0, 0);
            }

            var topMargin = startsBurst ? 4.0 : 1.0;
            var bottomMargin = endsBurst ? 4.0 : 1.0;
            return WithHoverHandlers(
                Border(
                    VStack(2, bubbleRow, footer)
                        .HAlign(HorizontalAlignment.Stretch)
                        .AutomationName(entry.Text ?? "")
                ).Background(new SolidColorBrush(Colors.Transparent))
                 .Margin(16, topMargin, gutter, bottomMargin),
                entry.Id);
        }

        // Tool burst: a *contiguous* run of ToolCall entries (one assistant
        // turn may chain multiple tool invocations) is rendered as a SINGLE
        // unified card with one row per tool. Each row collapses call+output
        // into `▸ ⚡ <ToolName> · <summary>  [Done]`; click expands the row
        // to reveal the original args + raw output (the previous chip body).
        // A single trailing `Tool · <time>` footer sits below the card.
        Element RenderToolBurst(System.Collections.Generic.IReadOnlyList<ChatTimelineItem> entries, bool showAvatar)
        {
            // Status pill / glyph color resolved per entry (each row has its
            // own status). The body styling (block bg/border) is shared across
            // the burst.
            var blockBg     = themeBrush("ControlFillColorTertiaryBrush");
            var blockBorder = themeBrush("ControlStrokeColorDefaultBrush");
            var blockHeaderBg = themeBrush("SubtleFillColorSecondaryBrush");

            (string text, Brush bg, Brush fg) ResolveStatus(ChatTimelineItem entry)
            {
                // Same palette as the previous chip — keeps continuity with
                // Kenny's Cat04 tool cards. Running orange / Done green /
                // Error critical / Interrupted grey.
                switch (entry.ToolResult)
                {
                    case ChatToolCallStatus.Success:
                        var ok = new SolidColorBrush(Color.FromArgb(0xFF, 0x28, 0xA0, 0x50));
                        return (LocalizationHelper.GetString("Chat_Status_Done"), ok, ok);
                    case ChatToolCallStatus.Error:
                        var err = themeBrush("SystemFillColorCriticalBrush");
                        return (LocalizationHelper.GetString("Chat_Status_Error"), err, err);
                    case ChatToolCallStatus.Interrupted:
                        var grey = themeBrush("TextFillColorTertiaryBrush");
                        return (LocalizationHelper.GetString("Chat_Status_Interrupted"), grey, grey);
                    default:
                        var run = new SolidColorBrush(Color.FromArgb(0xFF, 0xDC, 0x78, 0x1E));
                        return (LocalizationHelper.GetString("Chat_Status_Running"), run, themeBrush("TextFillColorTertiaryBrush"));
                }
            }

            // Brief one-line summary for the collapsed row. Prefer the output
            // (truncated first line) so users see the *result* at a glance;
            // fall back to the args / tool kind when output isn't in yet.
            static string ShortSummary(ChatTimelineItem entry)
            {
                string Truncate(string s, int max)
                {
                    s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
                    return s.Length > max ? s.Substring(0, max - 1) + "…" : s;
                }
                if (!string.IsNullOrWhiteSpace(entry.ToolOutput)) return Truncate(entry.ToolOutput!, 80);
                if (!string.IsNullOrWhiteSpace(entry.Text) && entry.Text != entry.ToolName) return Truncate(entry.Text!, 80);
                return string.Empty;
            }

            Element BuildRow(ChatTimelineItem entry, bool isFirst, bool isLast, string? stepPrefix)
            {
                var kindLabel = entry.ToolName ?? "tool";
                var (statusText, statusBg, statusFg) = ResolveStatus(entry);
                var summary = ShortSummary(entry);

                var token = $"{entry.Id}:burst";
                var isExpanded = expandedToolChips.Value.Contains(token);
                var chevron = isExpanded ? "▾" : "▸";

                // Optional step prefix ("1.", "2.", …) — surfaced when the
                // explorations panel asks for explicit numbering and the burst
                // contains more than one tool.
                var labelText = string.IsNullOrEmpty(stepPrefix)
                    ? kindLabel
                    : $"{stepPrefix} {kindLabel}";

                var headerRow = Border(
                    Grid(
                        [GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto],
                        [GridSize.Auto],
                        Caption(chevron).Foreground(TertiaryText)
                            .Set(t => { t.FontSize = 12; })
                            .VAlign(VerticalAlignment.Center)
                            .Grid(row: 0, column: 0),
                        Caption("⚡").Foreground(statusFg)
                            .Set(t => { t.FontSize = 12; })
                            .VAlign(VerticalAlignment.Center)
                            .Margin(6, 0, 0, 0)
                            .Grid(row: 0, column: 1),
                        Caption(labelText).Foreground(SecondaryText)
                            .Set(t =>
                            {
                                t.FontFamily = new FontFamily("Cascadia Code, Cascadia Mono, Consolas");
                                t.FontSize = 12;
                                t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                            })
                            .VAlign(VerticalAlignment.Center)
                            .Margin(6, 0, 0, 0)
                            .Grid(row: 0, column: 2),
                        Caption(string.IsNullOrEmpty(summary) ? string.Empty : "· " + summary)
                            .Foreground(TertiaryText)
                            .Set(t =>
                            {
                                t.FontSize = 12;
                                t.TextTrimming = TextTrimming.CharacterEllipsis;
                                t.MaxLines = 1;
                            })
                            .VAlign(VerticalAlignment.Center)
                            .Margin(6, 0, 12, 0)
                            .Grid(row: 0, column: 3),
                        Border(
                            Caption(statusText)
                                .Foreground(new SolidColorBrush(Colors.White))
                                .Set(t => { t.FontSize = 10; t.LineHeight = 16; })
                                .VAlign(VerticalAlignment.Center)
                        ).Background(statusBg)
                         .CornerRadius(10)
                         .Padding(8, 0, 8, 0)
                         .Set(b => b.MinHeight = 18)
                         .VAlign(VerticalAlignment.Center)
                         .HAlign(HorizontalAlignment.Right)
                         .Grid(row: 0, column: 4)
                    ).HAlign(HorizontalAlignment.Stretch).Padding(bubblePadding.Left, bubblePadding.Top, bubblePadding.Right, bubblePadding.Bottom)
                ).Set(b => b.MinHeight = 32);

                Element body = Empty();
                if (isExpanded)
                {
                    var sections = new System.Collections.Generic.List<Element>();
                    Element BuildSection(string sectionLabel, string contentText)
                    {
                        var displayText = TryFormatJsonForDisplay(contentText);
                        var sectionRow = (FlexRow(
                            Caption("⚡").Foreground(TertiaryText)
                                .Set(t => { t.FontSize = 11; })
                                .VAlign(VerticalAlignment.Center),
                            Caption(sectionLabel.ToUpperInvariant())
                                .Foreground(TertiaryText)
                                .Set(t =>
                                {
                                    t.FontSize = 11;
                                    t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                                    t.CharacterSpacing = 60;
                                })
                                .VAlign(VerticalAlignment.Center)
                        ) with { ColumnGap = 6 }).Padding(12, 6, 12, 6);

                        var codeBlock = Border(
                            ScrollView(
                                TextBlock(displayText)
                                    .Set(t =>
                                    {
                                        t.FontFamily = new FontFamily("Cascadia Code, Cascadia Mono, Consolas");
                                        t.FontSize = 11;
                                        t.TextWrapping = TextWrapping.Wrap;
                                        t.IsTextSelectionEnabled = true;
                                        t.LineHeight = 16;
                                    })
                                    .Foreground(SecondaryText)
                                    .Padding(11, 8, 11, 10)
                            ).Set(sv =>
                            {
                                sv.MaxHeight = 240;
                                sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                                sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                            })
                        ).Background(blockBg)
                         .CornerRadius(6)
                         .WithBorder(blockBorder, 1)
                         .Margin(12, 0, 12, 8);

                        return VStack(0, sectionRow, codeBlock);
                    }

                    var callContent = !string.IsNullOrEmpty(entry.Text) && entry.Text != entry.ToolName
                        ? entry.Text!
                        : kindLabel;
                    if (!string.IsNullOrEmpty(callContent))
                        sections.Add(BuildSection(LocalizationHelper.GetString("Chat_Tool_InputSection"), callContent));
                    if (!string.IsNullOrEmpty(entry.ToolOutput))
                    {
                        var outLabel = entry.ToolResult == ChatToolCallStatus.Error
                            ? LocalizationHelper.GetString("Chat_Tool_ErrorLabel")
                            : LocalizationHelper.GetString("Chat_Tool_OutputLabel");
                        sections.Add(BuildSection(outLabel, entry.ToolOutput!));
                    }

                    body = Border(VStack(0, sections.ToArray())).Background(blockHeaderBg);
                }

                Action toggle = () =>
                {
                    var next = new HashSet<string>(expandedToolChips.Value);
                    if (!next.Add(token)) next.Remove(token);
                    expandedToolChips.Set(next);
                };

                // 1px separator between rows (skipped on the first row). The
                // separator lives inside the row so collapsed/expanded heights
                // both keep continuity with the next row above.
                var rowContent = VStack(0, headerRow, body);
                var rowWithSeparator = isFirst
                    ? (Element)rowContent
                    : Border(rowContent)
                        .Set(b =>
                        {
                            b.BorderThickness = new Thickness(0, 1, 0, 0);
                            b.BorderBrush = toolCardBorderBrush;
                        });

                return Button(rowWithSeparator, toggle)
                    .Set(b =>
                    {
                        b.HorizontalAlignment = HorizontalAlignment.Stretch;
                        b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                        b.Padding = new Thickness(0);
                        // Round only the outer corners so rows blend into the
                        // wrapping card without leaving gaps.
                        b.CornerRadius = new CornerRadius(
                            isFirst ? 8 : 0,
                            isFirst ? 8 : 0,
                            isLast ? 8 : 0,
                            isLast ? 8 : 0);
                    })
                    .Resources(r => r
                        .Set("ButtonBackground", new SolidColorBrush(Colors.Transparent))
                        .Set("ButtonBackgroundPointerOver", new SolidColorBrush(Color.FromArgb(0x10, 0x00, 0x00, 0x00)))
                        .Set("ButtonBackgroundPressed", new SolidColorBrush(Color.FromArgb(0x1C, 0x00, 0x00, 0x00)))
                        .Set("ButtonBorderBrush", new SolidColorBrush(Colors.Transparent))
                        .Set("ButtonBorderBrushPointerOver", new SolidColorBrush(Colors.Transparent))
                        .Set("ButtonBorderBrushPressed", new SolidColorBrush(Colors.Transparent)));
            }

            // ── Style-aware composition ──────────────────────────────
            // Read the live exploration state for the burst variant.
            // Defaults to Plain when not explicitly toggled.
            var style = ChatExplorationState.ToolBurstStyle;
            var showStepNumbers = ChatExplorationState.ShowStepNumbers && entries.Count > 1;
            var stepCount = entries.Count;

            // Aggregate burst status: Error if any errored, Running if any
            // not-yet-finished, Interrupted if any interrupted (but not running),
            // otherwise Done. Drives the task header pill.
            ChatToolCallStatus aggregateStatus = ChatToolCallStatus.Success;
            foreach (var e in entries)
            {
                if (e.ToolResult == ChatToolCallStatus.Error) { aggregateStatus = ChatToolCallStatus.Error; break; }
                if (e.ToolResult == ChatToolCallStatus.InProgress) { aggregateStatus = ChatToolCallStatus.InProgress; }
                else if (e.ToolResult == ChatToolCallStatus.Interrupted && aggregateStatus == ChatToolCallStatus.Success)
                    aggregateStatus = ChatToolCallStatus.Interrupted;
            }
            var (taskStatusText, taskStatusBg, _) = ResolveStatus(new ChatTimelineItem(
                Id: "agg", Kind: ChatTimelineItemKind.ToolCall, Text: null,
                ToolName: null, ToolResult: aggregateStatus, ToolOutput: null));

            string? StepPrefix(int i) => showStepNumbers ? $"{i + 1}." : null;

            // Footer (when shown) reflects the *last* entry's timestamp —
            // that's when the burst finished from the user's POV.
            var lastEntry = entries[entries.Count - 1];
            var entryMeta = MetaFor(lastEntry.Id);
            var timeStr = FormatTime(entryMeta?.Timestamp);
            // "Task · 3 steps · 8:16 PM" — used by FooterReframe + as the
            // companion line under the TaskHeader card. Keeps the time so
            // users still get the chronology.
            string TaskFooter()
            {
                var stepsLabel = $"Task · {stepCount} step{(stepCount == 1 ? "" : "s")}";
                return string.IsNullOrEmpty(timeStr) ? stepsLabel : $"{stepsLabel} · {timeStr}";
            }

            Element CardOf(Element[] rowEls) => Border(VStack(0, rowEls))
                .Background(toolCardBgBrush)
                .WithBorder(toolCardBorderBrush, 1)
                .Set(b => { b.CornerRadius = bubbleRadius; b.MaxWidth = 720; b.HorizontalAlignment = HorizontalAlignment.Left; });

            // Build the per-step rows once — used by Plain, TaskHeader, and
            // CompactSummary (when expanded).
            var rows = new Element[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                rows[i] = BuildRow(entries[i],
                    isFirst: i == 0,
                    isLast: i == entries.Count - 1,
                    stepPrefix: StepPrefix(i));
            }

            // CompactSummary: a single collapsed-by-default row showing the
            // task summary; clicking expands the per-step list. Only worth it
            // for multi-step bursts — single-step falls back to plain.
            if (style == ToolBurstStyle.CompactSummary && entries.Count > 1)
            {
                var summaryToken = $"{entries[0].Id}:burst-summary";
                var summaryExpanded = expandedToolChips.Value.Contains(summaryToken);
                var summaryChevron = summaryExpanded ? "▾" : "▸";
                var toolList = string.Join(", ", entries.Select(e => e.ToolName ?? "tool"));

                Action toggleSummary = () =>
                {
                    var next = new HashSet<string>(expandedToolChips.Value);
                    if (!next.Add(summaryToken)) next.Remove(summaryToken);
                    expandedToolChips.Set(next);
                };

                var summaryHeader = Border(
                    (FlexRow(
                        Caption(summaryChevron).Foreground(TertiaryText)
                            .Set(t => { t.FontSize = 12; }).VAlign(VerticalAlignment.Center),
                        Caption("⚡").Foreground(taskStatusBg)
                            .Set(t => { t.FontSize = 12; }).VAlign(VerticalAlignment.Center),
                        Caption($"Task · {stepCount} steps").Foreground(SecondaryText)
                            .Set(t => { t.FontSize = 12; t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; })
                            .VAlign(VerticalAlignment.Center),
                        Caption("· " + toolList).Foreground(TertiaryText)
                            .Set(t => { t.FontSize = 12; t.TextTrimming = TextTrimming.CharacterEllipsis; t.MaxLines = 1; })
                            .VAlign(VerticalAlignment.Center).Flex(grow: 1),
                        Border(
                            Caption(taskStatusText).Foreground(new SolidColorBrush(Colors.White))
                                .Set(t => { t.FontSize = 10; t.LineHeight = 16; })
                                .VAlign(VerticalAlignment.Center)
                        ).Background(taskStatusBg).CornerRadius(10).Padding(8, 0, 8, 0)
                         .Set(b => b.MinHeight = 18).VAlign(VerticalAlignment.Center)
                    ) with { ColumnGap = 6 }).Padding(12, 8, 12, 8)
                ).Set(b => b.MinHeight = 22);

                var summaryButton = Button(summaryHeader, toggleSummary).Set(b =>
                {
                    b.HorizontalAlignment = HorizontalAlignment.Stretch;
                    b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    b.Padding = new Thickness(0);
                    b.CornerRadius = new CornerRadius(bubbleRadius.TopLeft, bubbleRadius.TopRight, summaryExpanded ? 0 : bubbleRadius.BottomRight, summaryExpanded ? 0 : bubbleRadius.BottomLeft);
                }).Resources(r => r
                    .Set("ButtonBackground", new SolidColorBrush(Colors.Transparent))
                    .Set("ButtonBackgroundPointerOver", new SolidColorBrush(Color.FromArgb(0x10, 0x00, 0x00, 0x00)))
                    .Set("ButtonBackgroundPressed", new SolidColorBrush(Color.FromArgb(0x1C, 0x00, 0x00, 0x00)))
                    .Set("ButtonBorderBrush", new SolidColorBrush(Colors.Transparent))
                    .Set("ButtonBorderBrushPointerOver", new SolidColorBrush(Colors.Transparent))
                    .Set("ButtonBorderBrushPressed", new SolidColorBrush(Colors.Transparent)));

                var pieces = new System.Collections.Generic.List<Element> { summaryButton };
                if (summaryExpanded)
                {
                    // Mark each row with a top separator since they sit below
                    // the summary header inside the same card.
                    pieces.AddRange(rows);
                }
                return VStack(2,
                    CardOf(pieces.ToArray()),
                    FooterCaption(timeStr ?? string.Empty, HorizontalAlignment.Left).Margin(0, 2, 0, 0)
                ).HAlign(HorizontalAlignment.Stretch).Margin(36, 6, gutter, 6);
            }

            // TaskHeader: prepend a non-clickable header row to the card.
            if (style == ToolBurstStyle.TaskHeader && entries.Count > 1)
            {
                var taskHeader = Border(
                    Border(
                        (FlexRow(
                            Caption("⚡").Foreground(taskStatusBg)
                                .Set(t => { t.FontSize = 12; }).VAlign(VerticalAlignment.Center),
                            Caption($"Task · {stepCount} steps").Foreground(SecondaryText)
                                .Set(t => { t.FontSize = 12; t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; })
                                .VAlign(VerticalAlignment.Center),
                            Caption(string.Empty).Flex(grow: 1),
                            Border(
                                Caption(taskStatusText).Foreground(new SolidColorBrush(Colors.White))
                                    .Set(t => { t.FontSize = 10; t.LineHeight = 16; })
                                    .VAlign(VerticalAlignment.Center)
                            ).Background(taskStatusBg).CornerRadius(10).Padding(8, 0, 8, 0)
                             .Set(b => b.MinHeight = 18).VAlign(VerticalAlignment.Center)
                        ) with { ColumnGap = 6 }).Padding(12, 8, 12, 8)
                    ).Set(b => b.MinHeight = 22)
                ).Background(themeBrush("SubtleFillColorSecondaryBrush"));

                var combined = new Element[entries.Count + 1];
                combined[0] = taskHeader;
                Array.Copy(rows, 0, combined, 1, rows.Length);

                return VStack(2,
                    CardOf(combined),
                    FooterCaption(timeStr ?? string.Empty, HorizontalAlignment.Left).Margin(0, 2, 0, 0)
                ).HAlign(HorizontalAlignment.Stretch).Margin(36, 6, gutter, 6);
            }

            // TaskList: per-step rows with a status icon (✓ / spinner / ✕)
            // mirroring the AgentRunCard "Running steps / Completed steps"
            // pattern from native-chat-v2. Now agent-grouped:
            //  • left assistant avatar slot (matches RenderAssistantEntry)
            //  • collapsible header showing aggregate status + a result-focused
            //    one-line summary
            //  • auto-expanded while any step is InProgress, auto-collapsed
            //    when the whole burst is Success/Error. Click chevron to flip.
            if (style == ToolBurstStyle.TaskList)
            {
                Element StatusGlyph(ChatToolCallStatus status, double size = 14)
                {
                    switch (status)
                    {
                        case ChatToolCallStatus.Success:
                            return Caption("\uE73E")
                                .Foreground(new SolidColorBrush(Color.FromArgb(0xFF, 0x28, 0xA0, 0x50)))
                                .Set(t => { t.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"); t.FontSize = size; })
                                .HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center);
                        case ChatToolCallStatus.Error:
                            return Caption("\uE711")
                                .Foreground(themeBrush("SystemFillColorCriticalBrush"))
                                .Set(t => { t.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"); t.FontSize = size; })
                                .HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center);
                        case ChatToolCallStatus.Interrupted:
                            return Caption("\uE738")
                                .Foreground(themeBrush("TextFillColorTertiaryBrush"))
                                .Set(t => { t.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"); t.FontSize = size; })
                                .HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center);
                        default:
                            return ProgressRing()
                                .Width(size).Height(size)
                                .Set(p => { p.IsActive = true; p.Foreground = themeBrush("AccentFillColorDefaultBrush"); })
                                .HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center);
                    }
                }

                // Result-focused one-liner: prefer the LAST entry's output
                // (truncated). Fall back to status-specific text when output
                // isn't available yet (e.g. mid-flight or error).
                string SummaryLine()
                {
                    string Truncate(string s, int max)
                    {
                        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
                        return s.Length > max ? s.Substring(0, max - 1) + "…" : s;
                    }
                    var last = entries[entries.Count - 1];
                    if (aggregateStatus == ChatToolCallStatus.InProgress)
                    {
                        var name = last.ToolName ?? "tool";
                        return $"Working on {name}…";
                    }
                    if (aggregateStatus == ChatToolCallStatus.Error)
                    {
                        var errEntry = entries.FirstOrDefault(e => e.ToolResult == ChatToolCallStatus.Error) ?? last;
                        var name = errEntry.ToolName ?? "tool";
                        var msg = !string.IsNullOrWhiteSpace(errEntry.ToolOutput) ? errEntry.ToolOutput! : "failed";
                        return Truncate($"{name} failed: {msg}", 80);
                    }
                    if (!string.IsNullOrWhiteSpace(last.ToolOutput)) return Truncate(last.ToolOutput!, 80);
                    return $"Ran {entries.Count} step{(entries.Count == 1 ? "" : "s")}";
                }
                var summaryLine = SummaryLine();

                // Auto state: expand while running, collapse when finished.
                // expandedToolChips token marks "user override" — when present,
                // flip the default. Simple flip-from-default (no value map).
                var taskListToken = $"{entries[0].Id}:tasklist";
                var defaultExpanded = aggregateStatus == ChatToolCallStatus.InProgress;
                var hasOverride = expandedToolChips.Value.Contains(taskListToken);
                var effectiveExpanded = hasOverride ? !defaultExpanded : defaultExpanded;
                // ▲ when expanded (action: click to collapse up),
                // ▼ when collapsed (action: click to expand down).
                var chevron = effectiveExpanded ? "▲" : "▼";
                var stepCountLabel = $"{stepCount} step{(stepCount == 1 ? "" : "s")}";

                Action toggleTaskList = () =>
                {
                    var next = new HashSet<string>(expandedToolChips.Value);
                    if (!next.Add(taskListToken)) next.Remove(taskListToken);
                    expandedToolChips.Set(next);
                };

                // Per-step rows (used only when expanded).
                var stepRows = new System.Collections.Generic.List<Element>();
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    var status = e.ToolResult ?? ChatToolCallStatus.InProgress;
                    var label = e.ToolName ?? "tool";
                    var prefix = StepPrefix(i);
                    var labelText = string.IsNullOrEmpty(prefix) ? label : $"{prefix} {label}";
                    var summary = ShortSummary(e);

                    stepRows.Add(
                        (FlexRow(
                            Border(StatusGlyph(status))
                                .Width(20).HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center),
                            VStack(2,
                                Caption(labelText).Foreground(themeBrush("TextFillColorPrimaryBrush"))
                                    .Set(t => { t.FontSize = 12; t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; })
                                    .VAlign(VerticalAlignment.Center),
                                string.IsNullOrEmpty(summary)
                                    ? Empty()
                                    : Caption(summary).Foreground(TertiaryText)
                                        .Set(t => { t.FontSize = 11; t.TextTrimming = TextTrimming.CharacterEllipsis; t.MaxLines = 1; })
                            ).Flex(grow: 1)
                        ) with { ColumnGap = 10 })
                    );
                }

                // Header content: aggregate status (only for in-progress / error)
                // · summary · step count · chevron. When the burst finished
                // successfully, drop the leading ✓ — the summary line is
                // already past-tense and the avatar carries identity.
                var headerStatusSlot = aggregateStatus == ChatToolCallStatus.Success
                    ? Empty()
                    : (Element)Border(StatusGlyph(aggregateStatus, 14))
                        .Width(20).HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center);
                var headerContent = (FlexRow(
                    headerStatusSlot,
                    Caption(summaryLine).Foreground(themeBrush("TextFillColorPrimaryBrush"))
                        .Set(t =>
                        {
                            t.FontSize = 12;
                            t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                            t.TextTrimming = TextTrimming.CharacterEllipsis;
                            t.MaxLines = 1;
                        })
                        .VAlign(VerticalAlignment.Center).Flex(grow: 1),
                    Caption(stepCountLabel).Foreground(TertiaryText)
                        .Set(t => { t.FontSize = 11; }).VAlign(VerticalAlignment.Center),
                    Caption(chevron).Foreground(TertiaryText)
                        .Set(t => { t.FontSize = 10; }).VAlign(VerticalAlignment.Center)
                ) with { ColumnGap = 8 }).Margin(0, 0, 0, 0);

                var headerButton = Button(headerContent, toggleTaskList).Set(b =>
                {
                    b.HorizontalAlignment = HorizontalAlignment.Stretch;
                    b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    b.Padding = bubblePadding;
                    b.CornerRadius = new CornerRadius(bubbleRadius.TopLeft, bubbleRadius.TopRight, effectiveExpanded ? 0 : bubbleRadius.BottomRight, effectiveExpanded ? 0 : bubbleRadius.BottomLeft);
                }).Resources(r => r
                    .Set("ButtonBackground", new SolidColorBrush(Colors.Transparent))
                    .Set("ButtonBackgroundPointerOver", new SolidColorBrush(Color.FromArgb(0x10, 0x00, 0x00, 0x00)))
                    .Set("ButtonBackgroundPressed", new SolidColorBrush(Color.FromArgb(0x1C, 0x00, 0x00, 0x00)))
                    .Set("ButtonBorderBrush", new SolidColorBrush(Colors.Transparent))
                    .Set("ButtonBorderBrushPointerOver", new SolidColorBrush(Colors.Transparent))
                    .Set("ButtonBorderBrushPressed", new SolidColorBrush(Colors.Transparent)));

                var cardChildren = new System.Collections.Generic.List<Element> { headerButton };
                if (effectiveExpanded)
                {
                    // Body sits inside the same card; thin top border so the
                    // header + body read as one unit but the divide is clear.
                    cardChildren.Add(
                        Border(VStack(8, stepRows.ToArray()))
                            .Set(b =>
                            {
                                b.Padding = bubblePadding;
                                b.BorderBrush = toolCardBorderBrush;
                                b.BorderThickness = new Thickness(0, 1, 0, 0);
                            })
                    );
                }

                var listCard = Border(
                    VStack(0, cardChildren.ToArray())
                ).Background(toolCardBgBrush)
                 .WithBorder(toolCardBorderBrush, 1)
                 .Set(b => { b.CornerRadius = bubbleRadius; b.MaxWidth = 720; b.HorizontalAlignment = HorizontalAlignment.Left; });

                // Wrap with the assistant avatar slot so the burst visually
                // anchors to the agent that produced it (and lines up with the
                // assistant bubble that follows below). When this task card
                // continues an agent-side run that already showed the avatar
                // above, render an empty 36×36 spacer to keep alignment.
                Element leftSlot = !showAssistAvatar
                    ? Empty()
                    : (showAvatar
                        ? AssistantAvatar().VAlign(VerticalAlignment.Top)
                        : Border(Empty()).Size(36, 36));

                var burstRow = Grid(
                    [GridSize.Auto, GridSize.Star()],
                    [GridSize.Auto],
                    leftSlot.Grid(row: 0, column: 0).Margin(0, 0, showAssistAvatar && showAvatar ? bubbleSideMargin : 0, 0),
                    listCard.HAlign(HorizontalAlignment.Left).Grid(row: 0, column: 1)
                ).HAlign(HorizontalAlignment.Stretch);

                // Match assistant bubble's outer inset so user/assistant/tool
                // share the same left edge. Avatar slot lives inside burstRow.
                return burstRow.HAlign(HorizontalAlignment.Stretch).Margin(16, 6, gutter, 6);
            }

            // FooterReframe keeps the "Task · N steps · time" caption.
            // Plain drops the footer entirely — the assistant follow-up
            // bubble below carries the timestamp for the whole turn, and
            // labelling each tool card with "Tool · time" added visual noise.
            if (style == ToolBurstStyle.FooterReframe)
            {
                return VStack(2,
                    CardOf(rows),
                    FooterCaption(TaskFooter(), HorizontalAlignment.Left).Margin(0, 2, 0, 0)
                ).HAlign(HorizontalAlignment.Stretch)
                 .Margin(16, 6, gutter, 6);
            }

            return CardOf(rows).HAlign(HorizontalAlignment.Left)
                .Margin(16, 6, gutter, 6);
        }

        // Legacy single-entry RenderToolEntry removed — all ToolCall rendering
        // goes through RenderToolBurst from the outer loop. The RenderEntry
        // switch returns Empty() for ToolCall since the outer loop coalesces
        // the burst itself.

        Element RenderEntry(ChatTimelineItem entry, bool startsBurst, bool endsBurst, bool showAvatar) => entry.Kind switch
        {
            ChatTimelineItemKind.User => RenderUserEntry(entry, startsBurst, endsBurst),
            ChatTimelineItemKind.Assistant => RenderAssistantEntry(entry, startsBurst, endsBurst, showAvatar),
            ChatTimelineItemKind.ToolCall => Empty(),

            // Reasoning — use a WinUI Expander with a "🧠 Thinking" header,
            // matching Kenny's ComponentLibrary Cat03/NativeChatThread design.
            // Collapsed by default so the model thought trace doesn't crowd
            // the conversation; click to peek.
            ChatTimelineItemKind.Reasoning => entry.Text is { Length: > 0 }
                ? TimelineInset(
                    Border(
                        Expander(
                            LocalizationHelper.GetString("Chat_Reasoning_ThinkingHeader"),
                            TextBlock(entry.Text)
                                .Set(t =>
                                {
                                    t.FontSize = 12;
                                    t.TextWrapping = TextWrapping.Wrap;
                                    t.FontFamily = new FontFamily("Cascadia Code, Cascadia Mono, Consolas");
                                })
                                .Foreground(TertiaryText)
                                .Padding(0, 4, 0, 4),
                            isExpanded: false)
                        .Set(e =>
                        {
                            e.HorizontalAlignment = HorizontalAlignment.Stretch;
                            e.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                        })
                    ).Background(Ref("SubtleFillColorTertiaryBrush"))
                     .CornerRadius(8)
                     .WithBorder(new SolidColorBrush(Color.FromArgb(0xFF, 0x64, 0x8C, 0xB4)), 1)
                     .Margin(8, 2, 40, 2),
                    top: 4,
                    bottom: 4)
                : TimelineInset(
                    Caption(LocalizationHelper.GetString("Chat_Reasoning_ThinkingEllipsis")).Foreground(TertiaryText)
                        .Set(t => { t.FontStyle = global::Windows.UI.Text.FontStyle.Italic; t.FontSize = 12; })),

            // Filtered status — drop transient connection chatter.
            ChatTimelineItemKind.Status when entry.Text.Contains("Restored") || entry.Text.Contains("Connecting to") || entry.Text.Contains("Connected") || entry.Text.Contains("Resuming") => Empty(),

            // Error status — centered red pill (Kenny's Cat10 system-notice
            // pattern: small bordered capsule, tinted background, glyph + text).
            ChatTimelineItemKind.Status when entry.Tone == ChatTone.Error =>
                Border(
                    Border(
                        (FlexRow(
                            Caption("⚠").Foreground(themeBrush("SystemFillColorCriticalBrush"))
                                .Set(t => { t.FontSize = 12; })
                                .VAlign(VerticalAlignment.Center),
                            Caption(entry.Text).Foreground(themeBrush("SystemFillColorCriticalBrush"))
                                .Set(t => { t.FontSize = 12; t.TextWrapping = TextWrapping.Wrap; })
                                .VAlign(VerticalAlignment.Center)
                        ) with { ColumnGap = 6 })
                    ).Background(new SolidColorBrush(Color.FromArgb(0x2E, 0xC8, 0x32, 0x32)))  // crimson @ ~18%
                     .CornerRadius(12)
                     .Padding(10, 4, 10, 4)
                     .HAlign(HorizontalAlignment.Center)
                ).Margin(0, 4, 0, 4),

            // Generic status — small dim centered pill at 18% tint.
            ChatTimelineItemKind.Status =>
                Border(
                    Border(
                        (FlexRow(
                            Caption("ℹ").Foreground(TertiaryText)
                                .Set(t => { t.FontSize = 12; })
                                .VAlign(VerticalAlignment.Center),
                            Caption(entry.Text).Foreground(TertiaryText)
                                .Set(t => { t.FontSize = 12; t.TextWrapping = TextWrapping.Wrap; })
                                .VAlign(VerticalAlignment.Center)
                        ) with { ColumnGap = 6 })
                    ).Background(themeBrush("SubtleFillColorTertiaryBrush"))
                     .CornerRadius(12)
                     .Padding(10, 4, 10, 4)
                     .HAlign(HorizontalAlignment.Center)
                ).Margin(0, 4, 0, 4),

             _ => Empty()
        };

        // Render entries — compute "burst" boundaries so consecutive
        // messages from the same role share a single avatar+footer.
        // A burst is delimited by a Kind change (User↔Assistant, or any
        // Tool/Status/Reasoning entry breaks both).
        static bool SameBurstKind(ChatTimelineItemKind a, ChatTimelineItemKind b) =>
            a == b && (a == ChatTimelineItemKind.User
                       || a == ChatTimelineItemKind.Assistant
                       || a == ChatTimelineItemKind.ToolCall);

        // Agent-side kinds share a single avatar across a contiguous run so
        // a task card followed by an assistant bubble (or back-to-back
        // assistant bubbles) reads as one speaker — only the topmost item
        // carries the avatar; subsequent items render an aligned spacer.
        static bool IsAgentSide(ChatTimelineItemKind k) =>
            k == ChatTimelineItemKind.Assistant || k == ChatTimelineItemKind.ToolCall;

        var renderedEntries = new Element[Props.Entries.Count];
        for (int i = 0; i < Props.Entries.Count; i++)
        {
            var entry = Props.Entries[i];
            var prevKind = i > 0 ? Props.Entries[i - 1].Kind : (ChatTimelineItemKind?)null;
            var nextKind = i < Props.Entries.Count - 1 ? Props.Entries[i + 1].Kind : (ChatTimelineItemKind?)null;
            var startsBurst = prevKind is null || !SameBurstKind(prevKind.Value, entry.Kind);
            var endsBurst = nextKind is null || !SameBurstKind(entry.Kind, nextKind.Value);
            var showAvatar = !(prevKind is { } pk && IsAgentSide(pk) && IsAgentSide(entry.Kind));

            // Coalesce contiguous ToolCall entries into a single unified
            // burst card so a multi-step assistant turn reads as one tidy
            // block instead of N separate chips with repeated footers.
            if (entry.Kind == ChatTimelineItemKind.ToolCall)
            {
                if (!showToolCalls)
                {
                    renderedEntries[i] = Empty().WithKey(entry.Id);
                    continue;
                }
                if (!startsBurst)
                {
                    // Non-start tool entries collapsed into the burst rendered
                    // at startsBurst; render Empty here to avoid duplication.
                    renderedEntries[i] = Empty().WithKey(entry.Id);
                    continue;
                }
                var burst = new System.Collections.Generic.List<ChatTimelineItem> { entry };
                int j = i + 1;
                while (j < Props.Entries.Count && Props.Entries[j].Kind == ChatTimelineItemKind.ToolCall)
                {
                    burst.Add(Props.Entries[j]);
                    j++;
                }
                renderedEntries[i] = RenderToolBurst(burst, showAvatar).WithKey(entry.Id);
                continue;
            }

            renderedEntries[i] = RenderEntry(entry, startsBurst, endsBurst, showAvatar).WithKey(entry.Id);
        }

        // Inline "thinking" indicator rendered just below the last entry
        // when caller signals we're between turn-start and the first byte.
        Element thinkingIndicator = Empty();
        if (Props.ShowThinkingIndicator)
        {
            // Format string typically ends in "…" (or "..."); strip the trailing
            // ellipsis chars so we can append our own animated dots that grow
            // 0→3 in lockstep with the DispatcherTimer above. Pad the suffix
            // with NBSPs to a fixed 3-char width so the caption doesn't jitter
            // horizontally as the dots cycle.
            var rawText = string.Format(LocalizationHelper.GetString("Chat_Timeline_AssistantThinkingFormat"), assistantSender);
            var trimmed = rawText.TrimEnd('…', '.', ' ');
            var dots = thinkingDotPhase;
            var animatedSuffix = new string('.', dots) + new string('\u00A0', 3 - dots);
            thinkingIndicator = Border(
                (FlexRow(
                    AssistantAvatar().VAlign(VerticalAlignment.Center),
                    Caption(trimmed + animatedSuffix)
                        .Foreground(chatStampFg)
                        .Set(t => { t.FontStyle = global::Windows.UI.Text.FontStyle.Italic; t.FontSize = 13; })
                        .VAlign(VerticalAlignment.Center)
                ) with { ColumnGap = 8 })
            ).Margin(12, 4, 60, 4)
             .LiveRegion(Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        }

        return Grid([GridSize.Star()], [GridSize.Star()],
            // Page background matches dash-light --bg so bubbles stand out.
            Border(
                ScrollView(
                    Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Auto],
                        loadMoreButton.Grid(row: 0, column: 0),
                        Border(Empty()).Height(20).Grid(row: 1, column: 0),
                        VStack(2, renderedEntries).Set(sp =>
                        {
                            if (contentRef.Current != sp)
                            {
                                contentRef.Current = (Microsoft.UI.Xaml.Controls.StackPanel)sp;
                                sp.SizeChanged += (_, _) =>
                                {
                                    if (!suppressAutoFollowRef.Current && isFollowingRef.Current && scrollViewRef.Current is { } sv)
                                        QueueScrollToBottom(sv, prevSessionIdRef.Current, disableAnimation: true);
                                };
                            }
                        }).Grid(row: 2, column: 0),
                        thinkingIndicator.Grid(row: 3, column: 0),
                        Border(Empty()).Height(24).Grid(row: 4, column: 0)
                    )
                ).Set(sv =>
            {
                sv.HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Disabled;
                sv.HorizontalScrollMode = Microsoft.UI.Xaml.Controls.ScrollMode.Disabled;
                sv.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                if (scrollViewRef.Current != sv)
                {
                    scrollViewRef.Current = sv;
                    sv.ViewChanged += (_, _) =>
                    {
                        UpdateScrollMetrics(sv);

                        if (sv.ScrollableHeight > 0
                            && sv.VerticalOffset <= FollowThreshold
                            && hasMoreHistoryRef.Current
                            && loadMoreRequestedForCountRef.Current != prevEntryCountRef.Current)
                        {
                            loadMoreRequestedForCountRef.Current = prevEntryCountRef.Current;
                            loadMoreHistoryRef.Current?.Invoke();
                        }
                    };
                }

                if (entryCount != previousEntryCount)
                    loadMoreRequestedForCountRef.Current = -1;

                if (sessionChanged)
                {
                    StoreSessionOffset(previousSessionId, lastVerticalOffsetRef.Current);

                    if (entryCount > 0)
                    {
                        // Always scroll to bottom when switching sessions
                        QueueScrollToBottom(sv, Props.SessionId, disableAnimation: true);
                    }
                }
                else if (prependedHistory)
                {
                    QueuePreservePrependOffset(sv, Props.SessionId, lastVerticalOffsetRef.Current, lastScrollableHeightRef.Current);
                }
                else if (initialLoad)
                {
                    if (Props.SessionId is not null && sessionOffsetsRef.Current.TryGetValue(Props.SessionId, out var savedOffset))
                        QueueScrollToOffset(sv, Props.SessionId, savedOffset, disableAnimation: true, suppressAutoFollow: true);
                    else
                        QueueScrollToBottom(sv, Props.SessionId, disableAnimation: true);
                }
                else if (appendedEntries && isFollowingRef.Current)
                {
                    QueueScrollToBottom(sv, Props.SessionId, disableAnimation: false);
                }

                prevSessionIdRef.Current = Props.SessionId;
                prevFirstEntryIdRef.Current = firstEntryId;
                prevLastEntryIdRef.Current = lastEntryId;
                prevEntryCountRef.Current = entryCount;
            })
            ).Background(chatPageBg).Grid(row: 0, column: 0)
        );
    }
}
