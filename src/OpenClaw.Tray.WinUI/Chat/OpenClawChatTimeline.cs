using OpenClaw.Chat;
using OpenClaw.Shared;
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
/// When true, renders an italic "<c>&lt;agent&gt; is thinking…</c>" placeholder
/// inside an assistant bubble. Used by callers to bridge the gap between
/// turn-start and the first assistant delta arriving.
/// </param>
/// <param name="ShowUsageMetadata">
/// When true, allows gateway-reported token/context metadata in assistant
/// footers. Production chat keeps this false so debug exploration presets
/// cannot expose usage details in the main surface.
/// </param>
/// <param name="EnableExplorationControls">
/// When true, honors Chat Exploration debug visibility controls. Production
/// chat keeps this false so saved exploration presets cannot hide assistant
/// bubbles or tool-call progress.
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
    bool ShowUsageMetadata = false,
    bool EnableExplorationControls = false,
    Func<string, Task>? OnReadAloud = null,
    Action? OnStopSpeaking = null,
    int ScrollToBottomToken = 0);

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

    /// <summary>
    /// Static scroll-offset store shared across all timeline instances so that
    /// scroll position survives page navigation (which recreates the page and
    /// component instances). Bounded to avoid unbounded memory growth.
    /// </summary>
    private static readonly Dictionary<string, double> s_sessionOffsets = new();
    private const int MaxSessionOffsets = 50;

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
    private static Element SafeMarkdownText(string? text)
    {
        // Fast path: bubbles with no block-level markdown (the common case)
        // keep the lightweight inline sanitizer to avoid the parser cost.
        if (!Markdown.ChatMarkdownRenderer.ContainsBlockMarkdown(text))
        {
            return TextBlock(string.Empty)
                .Set(t =>
                {
                    t.TextWrapping = TextWrapping.Wrap;
                    t.IsTextSelectionEnabled = true;
                    ApplySafeMarkdownInlines(t, text);
                });
        }
        return Markdown.ChatMarkdownRenderer.Render(text)
               ?? TextBlock(text ?? string.Empty)
                    .Set(t => { t.TextWrapping = TextWrapping.Wrap; t.IsTextSelectionEnabled = true; });
    }

    // Cache plain (non-markdown) text per TextBlock so we can reuse the
    // assistant-bubble's Inlines-based render path for user prompts without
    // re-clearing/rebuilding the run on every re-render. Going through
    // Inlines (instead of the TextBlock.Text property) avoids a WinUI quirk
    // where setting Text on a selection-enabled TextBlock during a parent
    // re-render that fires immediately after the user finishes selecting can
    // leave the glyph layer visually empty until the next focus change.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, string>
        s_plainCache = new();

    // FontFamily instances are immutable, but `new FontFamily(...)` per
    // render allocates a fresh CLR object whose reference does not equal
    // the previous one. Reassigning a referentially-different FontFamily
    // to a TextBlock invalidates its inline runs even when the source
    // string is identical, which (in the tool-output panel) makes
    // multi-line wrapped text vanish during a pointer-exit re-render.
    // Caching sidesteps both the GC pressure and the invalidation.
    //
    // FontFamily is a DependencyObject with thread affinity, so a single
    // process-wide singleton would crash with RPC_E_WRONG_THREAD if a
    // second window on a different dispatcher ever tried to read it.
    // Keying by DispatcherQueue mirrors the brush cache above: one
    // shared instance per window, collected with its dispatcher.
    // Off-dispatcher callers (tests, design-time) get a one-shot
    // uncached instance — correct, just not reused.
    private const string MonoFontFamilySource = "Cascadia Code, Cascadia Mono, Consolas";
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        Microsoft.UI.Dispatching.DispatcherQueue, FontFamily> s_monoFontByDispatcher = new();
    private static FontFamily s_monoFontFamily
    {
        get
        {
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dispatcher is null)
            {
                return new FontFamily(MonoFontFamilySource);
            }
            if (!s_monoFontByDispatcher.TryGetValue(dispatcher, out var family))
            {
                family = new FontFamily(MonoFontFamilySource);
                s_monoFontByDispatcher.Add(dispatcher, family);
            }
            return family;
        }
    }

    // Per-DispatcherQueue selection-highlight brushes for the user
    // bubble. The bubble background is the user's chosen system accent
    // (which may be red, green, purple, …), so a hardcoded color would
    // clash whenever the accent is non-blue. SystemAccentColorDark2 is
    // the OS-defined "darker shade of the current accent" — guaranteed
    // darker than the bubble's AccentFillColorDefault background and
    // high-contrast against the bubble's white foreground for every
    // accent. In High Contrast the bubble switches to
    // SystemColorHighlight (often near-black), so we fall back to the
    // OS-guaranteed SystemColorHighlightColor for the band there.
    //
    // SolidColorBrush is a DependencyObject with thread affinity, so a
    // single static instance would crash with RPC_E_WRONG_THREAD if a
    // second window on a different dispatcher ever tried to use it.
    // Keying by DispatcherQueue keeps one shared brush per window while
    // still avoiding per-render allocation. ConditionalWeakTable lets a
    // closing window's brush be collected with its dispatcher. The
    // brush's Color is mutated in place when the source color changes
    // (e.g. user switches their accent in Windows Settings) so
    // already-rendered TextBlocks update atomically.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        Microsoft.UI.Dispatching.DispatcherQueue, SolidColorBrush> s_accentDarkByDispatcher = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        Microsoft.UI.Dispatching.DispatcherQueue, SolidColorBrush> s_hcHighlightByDispatcher = new();
    // AccessibilitySettings is a WinRT object with DispatcherQueue
    // affinity: an instance created on one dispatcher cannot reliably
    // be read from another. We deliberately avoid Lazy<>: Lazy
    // permanently caches the factory's result, so a single failed
    // construction would cache null forever and silently disable the
    // High Contrast code path. Per-dispatcher cache keyed by
    // ConditionalWeakTable lets each window have its own instance,
    // collected when its dispatcher dies. On any thrown exception we
    // drop the cached instance so the next render retries from scratch.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        Microsoft.UI.Dispatching.DispatcherQueue,
        global::Windows.UI.ViewManagement.AccessibilitySettings> s_a11yByDispatcher = new();

    private static bool TryDetectHighContrast()
    {
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (dispatcher is null)
        {
            // Off-thread caller (tests, design-time). One-shot, no caching.
            try { return new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast; }
            catch { return false; }
        }
        if (!s_a11yByDispatcher.TryGetValue(dispatcher, out var settings))
        {
            try
            {
                settings = new global::Windows.UI.ViewManagement.AccessibilitySettings();
                s_a11yByDispatcher.Add(dispatcher, settings);
            }
            catch { return false; }
        }
        try { return settings.HighContrast; }
        catch
        {
            // Drop the cached instance so the next call retries.
            s_a11yByDispatcher.Remove(dispatcher);
            return false;
        }
    }

    private static SolidColorBrush GetUserBubbleSelectionBrush(bool isHighContrast)
    {
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var table = isHighContrast ? s_hcHighlightByDispatcher : s_accentDarkByDispatcher;
        var color = isHighContrast
            ? TryGetThemeColor("SystemColorHighlightColor", Microsoft.UI.Colors.Blue)
            : TryGetThemeColor("SystemAccentColorDark2", Microsoft.UI.Colors.DarkBlue);

        // No dispatcher means we're being called off-thread (e.g.
        // from a unit test). Allocate a one-shot brush — it can't be
        // safely cached without a dispatcher to key it on.
        if (dispatcher is null)
            return new SolidColorBrush(color);

        if (!table.TryGetValue(dispatcher, out var brush))
        {
            brush = new SolidColorBrush(color);
            table.Add(dispatcher, brush);
        }
        else if (brush.Color != color)
        {
            // Mutate in place rather than reallocating: TextBlocks
            // rendered earlier hold a reference to this brush, so
            // updating .Color updates them atomically without waiting
            // for the next render pass.
            brush.Color = color;
        }
        return brush;
    }

    private static Color TryGetThemeColor(string key, Color fallback)
    {
        try
        {
            var app = Application.Current;
            if (app is null) return fallback;
            if (app.Resources.TryGetValue(key, out var v))
            {
                // Theme dictionaries usually store Color, but a custom
                // theme override can supply a SolidColorBrush under the
                // same key. Accept either rather than silently falling
                // back to DarkBlue / Blue when the resource is present
                // but wrapped in a brush.
                if (v is Color c) return c;
                if (v is SolidColorBrush brush) return brush.Color;
            }
        }
        catch { /* resource lookup can throw in unpackaged/test hosts */ }
        return fallback;
    }

    private static void ApplyPlainSelectableInlines(TextBlock textBlock, string? text)
    {
        var normalized = text ?? string.Empty;
        // ConfigureTextBlock may set Text="" and clear Inlines before this
        // setter runs again, so only skip when the cached run is still present.
        if (textBlock.Inlines.Count > 0
            && s_plainCache.TryGetValue(textBlock, out var cached)
            && cached == normalized)
            return;
        s_plainCache.AddOrUpdate(textBlock, normalized);
        textBlock.Inlines.Clear();
        if (normalized.Length > 0)
            textBlock.Inlines.Add(new Run { Text = normalized });
    }

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

    /// <summary>
    /// Process-wide cache so we decode each cached image only once. Keyed by
    /// byte-array reference so cache invalidates automatically when bytes
    /// are replaced. BitmapImage instances are UI-thread-affine but read
    /// access from other threads through ImageBrush is safe.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], Microsoft.UI.Xaml.Media.Imaging.BitmapImage> _bitmapCache = new();

    /// <summary>
    /// Decodes <paramref name="bytes"/> into a <see cref="Microsoft.UI.Xaml.Media.Imaging.BitmapImage"/>,
    /// caching the result so repeated renders of the same image don't re-run
    /// the decoder. Returns <c>null</c> on any decode failure (renderer will
    /// fall back to a filename chip).
    /// </summary>
    static Microsoft.UI.Xaml.Media.Imaging.BitmapImage? TryDecodeBitmap(byte[] bytes)
    {
        if (_bitmapCache.TryGetValue(bytes, out var existing))
            return existing;
        try
        {
            var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new global::Windows.Storage.Streams.DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                writer.DetachStream();
            }
            stream.Seek(0);
            var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            bmp.SetSource(stream);
            _bitmapCache.Add(bytes, bmp);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public override Element Render()
    {
        // Subscribe to ChatExplorationState so toggles live-rerender the
        // timeline. Same inline pattern as OpenClawComposer (UseState +
        // UseEffect — extension methods can't access protected hooks).
        var explorationRev = UseState(0, threadSafe: true);
        var explorationRevRef = UseRef(0);
        UseEffect((Func<Action>)(() =>
        {
            if (!Props.EnableExplorationControls)
                return () => { };

            // Use a Ref for the counter to avoid stale-closure: the effect
            // runs once, so explorationRev.Value would be stuck at 0. The
            // Ref's .Current is always live, ensuring every Changed event
            // produces a unique value → always triggers a re-render.
            EventHandler h = (_, _) =>
            {
                explorationRevRef.Current++;
                explorationRev.Set(explorationRevRef.Current);
            };
            ChatExplorationState.Changed += h;
            return () => ChatExplorationState.Changed -= h;
        }));

        // Production chat uses stable visual defaults. Tool-call visibility is
        // the one user-facing composer toggle that applies outside preview.
        var bubbleRadius     = Props.EnableExplorationControls ? ChatVisualResolver.BubbleCornerRadius() : new CornerRadius(16);
        var bubblePadding    = Props.EnableExplorationControls ? ChatVisualResolver.BubbleInnerPadding() : new Thickness(16, 12, 16, 12);
        var bubbleSideMargin = Props.EnableExplorationControls ? ChatVisualResolver.BubbleSideMargin() : 8;
        var showAsstBubbles  = !Props.EnableExplorationControls || ChatVisualResolver.ShowAssistantBubbles();
        var showToolCalls    = ChatVisualResolver.ShowToolCalls();
        var gutter           = Props.EnableExplorationControls ? ChatExplorationState.Gutter : 64;
        var messageGap       = Props.EnableExplorationControls ? ChatExplorationState.MessageGap : 12;
        var showUserAvatar   = Props.EnableExplorationControls && ChatVisualResolver.ShowUserAvatar();
        var showAssistAvatar = !Props.EnableExplorationControls || ChatVisualResolver.ShowAssistantAvatar();
        var showTimestamps   = !Props.EnableExplorationControls || ChatVisualResolver.ShowTimestamps();

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
        var prevScrollToBottomTokenRef = UseRef(0);
        var hasMoreHistoryRef = UseRef(Props.HasMoreHistory);
        var loadMoreHistoryRef = UseRef<Action?>(Props.OnLoadMoreHistory);
        var loadMoreRequestedForCountRef = UseRef(-1);
        // Pending offset to restore after layout completes (SizeChanged).
        // Set during initialLoad when ScrollableHeight is still 0.
        var pendingRestoreOffsetRef = UseRef<double?>(null);

        // Per-entry expand state for tool chips. Tokens are
        // "{entryId}:call" and "{entryId}:out" so call and output
        // toggle independently. HashSet so the empty default is "all
        // collapsed" — matches the web's default-collapsed look.
        var expandedToolChips = UseState<HashSet<string>>(new HashSet<string>(), threadSafe: true);

        // Track the last-seen CollapseToolChipsVersion so we clear expanded
        // state when the user toggles tool calls off (collapsed view should
        // start fresh when re-shown).
        var collapseToolChipsVersion = ChatExplorationState.CollapseToolChipsVersion;
        var lastCollapseVersion = UseRef(collapseToolChipsVersion);
        if (lastCollapseVersion.Current != collapseToolChipsVersion)
        {
            lastCollapseVersion.Current = collapseToolChipsVersion;
            if (expandedToolChips.Value.Count > 0)
                expandedToolChips.Set(new HashSet<string>());
        }

        // When showToolCalls changes, pre-clear the native StackPanel so the
        // reconciler (SyncChildren) only does inserts into an empty panel
        // instead of expensive per-element RemoveAt calls that cascade
        // Unloaded events through deep visual subtrees.
        var prevShowToolCallsRef = UseRef(showToolCalls);
        if (prevShowToolCallsRef.Current != showToolCalls)
        {
            prevShowToolCallsRef.Current = showToolCalls;
            contentRef.Current?.Children.Clear();
        }

        // Hover state — set of entry ids currently under the pointer. Used to
        // reveal the trash / speak action icons beside user / assistant
        // bubbles. Re-renders the whole timeline on hover transitions; that's
        // fine for the entry counts we deal with (typically <100 visible).
        var hoveredEntries = UseState<HashSet<string>>(new HashSet<string>(), threadSafe: true);

        // Thinking-bubble dot animation. Cycles 0→1→2→3→0 every 400ms while the
        // ShowThinkingIndicator prop is true; drives the trailing "." / ".." /
        // "..." in the "<name> is thinking" text so the bubble visibly pulses
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

        // Track which entry is currently being read aloud so the button
        // can toggle to stop playback on a second press.
        var speakingEntryId = UseState<string?>(null, threadSafe: true);

        void AckAction(string entryId, string actionKey) =>
            AsyncEventHandlerGuard.Run(
                () => AckActionAsync(entryId, actionKey),
                new OpenClawTray.AppLogger(),
                nameof(AckAction));

        async Task AckActionAsync(string entryId, string actionKey)
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
        var isFirstMount = sessionChanged && previousSessionId is null;
        var initialLoad = isFirstMount
            ? entryCount > 0
            : (!sessionChanged && previousEntryCount == 0 && entryCount > 0);
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
            {
                sessionOffsetsRef.Current[sessionId] = offset;
                s_sessionOffsets[sessionId] = offset;
                // Evict oldest entries when cache exceeds bound
                if (s_sessionOffsets.Count > MaxSessionOffsets)
                {
                    var first = s_sessionOffsets.Keys.First();
                    s_sessionOffsets.Remove(first);
                }
            }
        }

        void UpdateScrollMetrics(Microsoft.UI.Xaml.Controls.ScrollViewer sv)
        {
            if (sv.ViewportHeight <= 0) return;

            lastVerticalOffsetRef.Current = sv.VerticalOffset;
            lastScrollableHeightRef.Current = sv.ScrollableHeight;
            isFollowingRef.Current = sv.ScrollableHeight - sv.VerticalOffset <= FollowThreshold;
            StoreSessionOffset(prevSessionIdRef.Current, sv.VerticalOffset);
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
        var chatPageBg = Props.EnableExplorationControls && ChatExplorationState.BackdropMode == ChatBackdropMode.Solid
            ? themeBrush("SubtleFillColorSecondaryBrush")
            : (Brush)new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var assistantBubbleBg   = Props.EnableExplorationControls
            ? ChatVisualResolver.AssistantBubbleBrush(themeBrush("SubtleFillColorSecondaryBrush"))
            : themeBrush("SubtleFillColorSecondaryBrush");
        var assistantBubbleBdr  = themeBrush("ControlStrokeColorDefaultBrush");
        // User bubble brushes vary with the configured tone. Accent → bold
        // brand-color bubble with white text (classic iMessage feel).
        // Secondary → ``AccentFillColorSecondaryBrush`` — the same accent
        // color at a softer fill weight. Both modes pair with
        // ``TextOnAccentFillColorPrimaryBrush``, which Fluent guarantees
        // meets WCAG AA contrast against any accent-tinted fill in both
        // light and dark themes (Microsoft's Fluent design token spec).
        var userToneIsAccent    = Props.EnableExplorationControls && ChatExplorationState.UserBubbleTone == ChatUserBubbleTone.Accent;
        var userBubbleBg        = Props.EnableExplorationControls
            ? ChatVisualResolver.UserBubbleBrush(themeBrush(userToneIsAccent ? "AccentFillColorDefaultBrush" : "AccentFillColorSecondaryBrush"))
            : themeBrush("AccentFillColorSecondaryBrush");
        var userBubbleBdr       = themeBrush(userToneIsAccent ? "AccentFillColorDefaultBrush" : "AccentFillColorSecondaryBrush");
        var userBubbleFg        = themeBrush("TextOnAccentFillColorPrimaryBrush");
        var avatarPanelBg       = themeBrush("SubtleFillColorTertiaryBrush");
        var avatarBorder        = themeBrush("ControlStrokeColorDefaultBrush");
        var assistantAvatarFg   = themeBrush("TextFillColorSecondaryBrush");
        var userAvatarBg        = Props.EnableExplorationControls
            ? ChatVisualResolver.AccentBrush(themeBrush("AccentFillColorDefaultBrush"))
            : themeBrush("AccentFillColorDefaultBrush");
        var userAvatarFg        = themeBrush("TextOnAccentFillColorPrimaryBrush");
        // a11y: timestamps and "is thinking" caption sit directly on the
        // window backdrop. On Mica/Acrylic the system tint is translucent,
        // so Tertiary text can fall below WCAG AA. Bump to Secondary when
        // the chat surface is transparent over a host backdrop.
        var chatStampFg         = Props.EnableExplorationControls && ChatExplorationState.BackdropMode == ChatBackdropMode.Solid
            ? themeBrush("TextFillColorTertiaryBrush")
            : themeBrush("TextFillColorSecondaryBrush");
        var chatTextFg          = themeBrush("TextFillColorPrimaryBrush");
        // Tool chips: very subtle background tint + light border so they
        // read as a secondary surface distinct from the filled assistant
        // bubble without looking like an empty outlined box.
        // CardBackgroundFillColorDefaultBrush is the right semantic key —
        // the bubble surface below is opaque (Mica/acrylic isn't being
        // used directly), so the LayerOnAcrylic family would render
        // incorrectly in dark/HC themes.
        var toolCardBgBrush     = themeBrush("CardBackgroundFillColorDefaultBrush");
        var toolCardBorderBrush = themeBrush("ControlStrokeColorDefaultBrush");
        // High-contrast themes need a thicker border to render at all
        // (WinUI guidance: 2px minimum). Detect once at render time so the
        // tool card border stays visible when HC is on, normal 1px otherwise.
        double toolCardBorderThickness = TryDetectHighContrast() ? 2 : 1;

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

        void ReadAloud(string entryId, string text) =>
            AsyncEventHandlerGuard.Run(
                () => ReadAloudAsync(entryId, text),
                new OpenClawTray.AppLogger(),
                nameof(ReadAloud));

        async Task ReadAloudAsync(string entryId, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            // Toggle: if this entry is currently being read, stop it.
            if (speakingEntryId.Value == entryId)
            {
                speakingEntryId.Set(null);
                Props.OnStopSpeaking?.Invoke();
                return;
            }

            if (Props.OnReadAloud is not { } onReadAloud) return;

            speakingEntryId.Set(entryId);
            try
            {
                await onReadAloud(StripMarkdownForSpeech(text));
            }
            finally
            {
                // Clear speaking state when playback finishes or fails
                speakingEntryId.Set(null);
            }
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
            var showSender   = Props.EnableExplorationControls && ChatExplorationState.ShowSenderName;
            var showModel    = Props.EnableExplorationControls && ChatExplorationState.ShowModelName;
            var showTokens   = Props.ShowUsageMetadata && Props.EnableExplorationControls && ChatExplorationState.ShowTokens;
            var showCtxPct   = Props.ShowUsageMetadata && Props.EnableExplorationControls && ChatExplorationState.ShowContextPercent;

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

            var isSpeaking = speakingEntryId.Value == entryId;
            var speakGlyph = isSpeaking ? "\uE71A" : "\uE767"; // Stop vs Speaker
            var speakTip = isSpeaking ? "Stop" : LocalizationHelper.GetString("Chat_Assistant_Action_ReadAloud");
            parts.Add(HoverIcon(entryId, "speak", speakGlyph, "\uE73E",
                speakTip,
                () => { ReadAloud(entryId, entryText); if (!isSpeaking) AckAction(entryId, "speak"); }).VAlign(VerticalAlignment.Center));

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
            var showSender = Props.EnableExplorationControls && ChatExplorationState.ShowSenderName;
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

            // Build attachment elements. Images become real thumbnail previews
            // by pulling the original bytes from OpenClawChatDataProvider's
            // ImagePreviewCache (populated on Send). Non-image attachments
            // remain as compact icon+name chips. Both are placed *inside* the
            // same bubble as the message text so the user sees a single
            // unified message — matching how Slack/iMessage/etc. show
            // image-with-caption posts.
            var attachmentElements = new List<Element>();
            if (hasAttachments)
            {
                foreach (var (_, name, isImage) in attachmentNames)
                {
                    if (isImage && OpenClawChatDataProvider.ImagePreviewCache.TryGetValue(name, out var bytes))
                    {
                        var bmp = TryDecodeBitmap(bytes);
                        if (bmp is not null)
                        {
                            const double maxW = 280;
                            const double maxH = 200;
                            var pw = bmp.PixelWidth > 0 ? bmp.PixelWidth : (int)maxW;
                            var ph = bmp.PixelHeight > 0 ? bmp.PixelHeight : (int)maxH;
                            var scale = Math.Min(Math.Min(maxW / pw, maxH / ph), 1.0);
                            var w = pw * scale;
                            var h = ph * scale;

                            attachmentElements.Add(
                                Border(Empty())
                                    .CornerRadius(8)
                                    .Set(b =>
                                    {
                                        b.Width = w;
                                        b.Height = h;
                                        b.Background = new ImageBrush
                                        {
                                            ImageSource = bmp,
                                            Stretch = Stretch.UniformToFill,
                                        };
                                        b.HorizontalAlignment = HorizontalAlignment.Right;
                                    }));
                            continue;
                        }
                    }

                    // Fallback chip (file attachment or missing image bytes).
                    var fileGlyph = isImage ? "\uEB9F" : "\uE8A5"; // Photo / Page
                    attachmentElements.Add(Border(
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
                        b.CornerRadius = new CornerRadius(6);
                        b.Padding = new Thickness(8, 6, 12, 6);
                        b.BorderThickness = new Thickness(1);
                        b.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
                        b.Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
                    }));
                }
            }

            // Build the unified bubble: attachments stacked at the top, text
            // below them. A single Border with the user-bubble background +
            // bubbleRadius wraps both so they read as one message.
            var bubbleChildren = new List<Element>();
            foreach (var ae in attachmentElements) bubbleChildren.Add(ae);
            if (hasMessage)
            {
                // Resolve HC + selection brush once per render method call
                // rather than per Set-lambda re-run. HC state cannot change
                // mid-render, and the brush is cached per-dispatcher so
                // every user bubble in this render shares the same instance.
                bool isHighContrast = TryDetectHighContrast();
                var selectionHighlightBrush = GetUserBubbleSelectionBrush(isHighContrast);
                bubbleChildren.Add(
                    TextBlock(string.Empty)
                        .Set(t =>
                        {
                            t.TextWrapping = TextWrapping.Wrap;
                            t.FontSize = 14;
                            t.Foreground = userBubbleFg;
                            t.IsTextSelectionEnabled = true;
                            // The default SelectionHighlightColor is the
                            // system accent — which equals the user bubble's
                            // background — so the highlight band is invisible
                            // against the bubble, and WinUI does NOT auto-
                            // invert an explicitly-set Foreground for
                            // selected glyphs. Outside High Contrast, use a
                            // darker shade of the current accent
                            // (SystemAccentColorDark2) so the band tracks
                            // whichever accent the user picked while keeping
                            // the white foreground readable. In High Contrast
                            // the bubble background switches to
                            // SystemColorHighlight (often near-black), where
                            // an accent-derived band may drop below WCAG
                            // 3:1, so fall back to the system selection
                            // color the OS guarantees contrasts with both
                            // surfaces.
                            t.SelectionHighlightColor = selectionHighlightBrush;
                            // Render through Inlines (a single Run) rather
                            // than the .Text property. This matches the
                            // assistant bubble's selection-safe path and
                            // sidesteps a WinUI bug where setting Text on a
                            // selection-enabled TextBlock during a re-render
                            // triggered by the mouse-up that ends a drag-
                            // select leaves the glyph layer visually empty.
                            ApplyPlainSelectableInlines(t, messageText);
                        }));
            }

            Element content;
            if (bubbleChildren.Count > 0)
            {
                content = Border(
                    VStack(8, bubbleChildren.ToArray())
                ).Background(userBubbleBg)
                 .Set(b =>
                 {
                     b.CornerRadius = bubbleRadius;
                     // When the bubble contains only an image, tighten the
                     // padding so the thumbnail nearly fills the bubble.
                     b.Padding = (hasAttachments && !hasMessage)
                         ? new Thickness(6, 6, 6, 6)
                         : bubblePadding;
                     b.VerticalAlignment = VerticalAlignment.Center;
                 })
                 .HAlign(HorizontalAlignment.Right);
            }
            else
            {
                content = Empty();
            }

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
                 .Margin(gutter, topMargin, 20, bottomMargin),
                entry.Id);
        }

        // Per-turn shared reference between the assistant bubble and any
        // tool cards rendered below it. The tool card binds its Width to
        // bubble.ActualWidth - toolIndent so the two cards' right edges
        // (and left indent) stay exactly parallel as the bubble grows
        // with content. Single-element Border[] used as a mutable slot
        // since these are local functions (no nested class allowed).
        Element RenderAssistantEntry(
            ChatTimelineItem entry,
            bool startsBurst,
            bool endsBurst,
            bool showAvatar,
            Microsoft.UI.Xaml.Controls.Border[]? bubbleSlot = null,
            Element? nestedTool = null,
            Element? overrideBubbleContent = null,
            bool suppressFooter = false,
            bool forceVisible = false)
        {
            if (string.IsNullOrEmpty(entry.Text) && nestedTool is null && overrideBubbleContent is null)
                return Empty();

            // Hidden by user toggle — collapses entire assistant block.
            if (!showAsstBubbles && !forceVisible)
                return Empty();

            // Avatar shown only on the FIRST entry of a contiguous agent-side
            // run. Continuation entries reserve a 36×36 spacer so the bubble's
            // left edge stays aligned with the first entry (matches the user
            // burst path above and the tool burst path below).
            Element leftSlot = !showAssistAvatar
                ? Empty()
                : (showAvatar
                    ? AssistantAvatar().VAlign(VerticalAlignment.Top)
                    : Border(Empty()).Size(36, 36));

            // Assistant bubble — subtle gray with primary text. Radius/Padding
            // come from ChatExplorationState (BubbleCornerRadius + PaddingDensity).
            // HAlign=Left keeps the bubble anchored next to the avatar/timestamp
            // column. MaxWidth=720 caps the growth so long messages stop where
            // the tool burst card's max right edge lands.
            // When `nestedTool` is supplied, the tool burst (single chip OR
            // collapsed multi-step summary) is rendered INSIDE the bubble's
            // content area — directly below the assistant text with a small
            // top gap — so it visually reads as a child of the bubble.
            Element bubbleContent = overrideBubbleContent ?? SafeMarkdownText(entry.Text);
            if (nestedTool != null)
            {
                // Top gap (markdown bottom → tool card top) needs to be a
                // little larger than the bubble's bottom padding so the
                // optical spacing matches the gap from the tool card to the
                // bubble's bottom edge — Markdown text has very tight
                // line-height with no trailing descender, so a literal-equal
                // gap reads as visibly tighter on top.
                var nestedTopGap = (int)Math.Round(bubblePadding.Bottom + 4);
                bubbleContent = VStack(nestedTopGap, bubbleContent, nestedTool);
            }
            var card = Border(
                bubbleContent
            ).Background(assistantBubbleBg)
             .Set(b =>
             {
                 b.CornerRadius = bubbleRadius;
                 b.Padding = bubblePadding;
                 b.MaxWidth = 720;
                 if (bubbleSlot != null) bubbleSlot[0] = b;
             });

            var bubbleRow = Grid(
                [GridSize.Auto, GridSize.Star()],
                [GridSize.Auto],
                leftSlot.Grid(row: 0, column: 0).Margin(0, 0, showAssistAvatar ? bubbleSideMargin : 0, 0),
                card.HAlign(HorizontalAlignment.Left).Grid(row: 0, column: 1)
            ).HAlign(HorizontalAlignment.Stretch);
            Element footer = Empty();
            if (endsBurst && showTimestamps && !suppressFooter)
            {
                var entryMeta = MetaFor(entry.Id);
                var timeStr = FormatTime(entryMeta?.Timestamp);
                var modelStr = entryMeta?.Model ?? defaultModel;
                footer = BuildAssistantFooter(assistantSender, timeStr, modelStr,
                    entryMeta?.InputTokens, entryMeta?.OutputTokens,
                    entryMeta?.ResponseTokens, entryMeta?.ContextPercent,
                    chatStampFg, entry.Id, entry.Text ?? "");
                var leftInset = showAssistAvatar ? (36 + bubbleSideMargin) : 0;
                leftInset += (int)bubblePadding.Left;
                footer = footer.Margin(leftInset, 2, 0, 0);
            }

            var topMargin = startsBurst ? 4.0 : 1.0;
            var bottomMargin = endsBurst ? 4.0 : 1.0;
            // AutomationName: when the bubble nests a tool burst inside it,
            // UIA would treat the named container as a leaf and hide the
            // nested tool card from screen readers. Drop the bubble-level
            // name in the nested case — the markdown text inside is read
            // out by UIA on its own, and Narrator can then traverse into
            // the nested tool card as a sibling child.
            var stack = VStack(2, bubbleRow, footer).HAlign(HorizontalAlignment.Stretch);
            if (nestedTool == null)
                stack = stack.AutomationName(entry.Text ?? "");
            return WithHoverHandlers(
                Border(stack).Background(new SolidColorBrush(Colors.Transparent))
                 .Margin(16, topMargin, gutter, bottomMargin),
                entry.Id);
        }

        // Tool burst: a *contiguous* run of ToolCall entries (one assistant
        // turn may chain multiple tool invocations) is rendered as a SINGLE
        // unified card with one row per tool. Each row collapses call+output
        // into `▸ ⚡ <ToolName> · <summary>  [Done]`; click expands the row
        // to reveal the original args + raw output (the previous chip body).
        // A single trailing `Tool · <time>` footer sits below the card.
        Element RenderToolBurst(System.Collections.Generic.IReadOnlyList<ChatTimelineItem> entries, bool showAvatar, Microsoft.UI.Xaml.Controls.Border[]? bubbleSlot = null, bool nested = false)
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
                        var ok = themeBrush("SystemFillColorSuccessBrush");
                        return (LocalizationHelper.GetString("Chat_Status_Done"), ok, ok);
                    case ChatToolCallStatus.Error:
                        var err = themeBrush("SystemFillColorCriticalBrush");
                        return (LocalizationHelper.GetString("Chat_Status_Error"), err, err);
                    case ChatToolCallStatus.Interrupted:
                        var grey = themeBrush("TextFillColorTertiaryBrush");
                        return (LocalizationHelper.GetString("Chat_Status_Interrupted"), grey, grey);
                    default:
                        var run = themeBrush("SystemFillColorCautionBrush");
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
                        // Outer 2-col layout: Star = left content (chevron /
                        // ⚡ / label / summary), Auto = status pill on the
                        // right. The Auto column always renders at its
                        // natural width, and the Star column is forced to
                        // ``parent_width - pill_width`` during measure — so
                        // Done can never be clipped by the card's rounded
                        // corner. Inside the Star column an inner Grid
                        // arranges the four left elements with summary in
                        // its own Star sub-column, so the summary trims
                        // with an ellipsis when the card is narrow.
                        [GridSize.Star(), GridSize.Auto],
                        [GridSize.Auto],
                        Grid(
                            [GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Star()],
                            [GridSize.Auto],
                            Caption(chevron).Foreground(TertiaryText)
                                .VAlign(VerticalAlignment.Center)
                                .Grid(row: 0, column: 0),
                            Caption("⚡").Foreground(statusFg)
                                .VAlign(VerticalAlignment.Center)
                                .Margin(6, 0, 0, 0)
                                .Grid(row: 0, column: 1),
                            Caption(labelText).Foreground(SecondaryText)
                                .Set(t =>
                                {
                                    t.FontFamily = s_monoFontFamily;
                                    t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                                })
                                .VAlign(VerticalAlignment.Center)
                                .Margin(6, 0, 0, 0)
                                .Grid(row: 0, column: 2),
                            Caption(string.IsNullOrEmpty(summary) ? string.Empty : "· " + summary)
                                .Foreground(TertiaryText)
                                .Set(t =>
                                {
                                    // TextWrapping=Wrap is required so the
                                    // TextBlock honors the Star sub-column
                                    // width during measure; combined with
                                    // MaxLines=1 + TextTrimming this gives
                                    // a single-line ellipsis.
                                    t.TextWrapping = TextWrapping.Wrap;
                                    t.TextTrimming = TextTrimming.CharacterEllipsis;
                                    t.MaxLines = 1;
                                })
                                .VAlign(VerticalAlignment.Center)
                                .Margin(6, 0, 12, 0)
                                .Grid(row: 0, column: 3)
                        ).Grid(row: 0, column: 0),
                        Border(
                            Caption(statusText)
                                .Foreground(themeBrush("TextOnAccentFillColorPrimaryBrush"))
                                .Set(t => { t.FontSize = 11; t.LineHeight = 16; })
                                .VAlign(VerticalAlignment.Center)
                        ).Background(statusBg)
                         .CornerRadius(10)
                         .Padding(8, 0, 8, 0)
                         .Set(b => b.MinHeight = 18)
                         .VAlign(VerticalAlignment.Center)
                         .HAlign(HorizontalAlignment.Right)
                         .Grid(row: 0, column: 1)
                    ).HAlign(HorizontalAlignment.Stretch).Padding(bubblePadding.Left, bubblePadding.Top, bubblePadding.Right, bubblePadding.Bottom)
                ).Set(b => b.MinHeight = 32);

                Element body = Empty();
                bool hasExpandedBody = false;
                if (isExpanded)
                {
                    var sections = new System.Collections.Generic.List<Element>();
                    Element BuildSection(string sectionLabel, string contentText)
                    {
                        var displayText = TryFormatJsonForDisplay(contentText);

                        // Phantom chevron + trailing 6px margin matches the
                        // header row's chevron column width + chevron→⚡ gap so
                        // the section ⚡ in col 1 starts at the same x as the
                        // header ⚡ above. Using the actual chevron glyph at
                        // the same FontSize keeps the alignment stable under
                        // font/density changes.
                        Element PhantomChevron() => Caption("▸")
                            .Foreground(new SolidColorBrush(Colors.Transparent))
                            .Margin(0, 0, 6, 0)
                            .VAlign(VerticalAlignment.Center);

                        var labelRow = (FlexRow(
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
                        ) with { ColumnGap = 6 });

                        var codeBlock = Border(
                            ScrollView(
                                // Use Text="" + Inlines populated by
                                // ApplyPlainSelectableInlines so that the
                                // ConditionalWeakTable cache short-circuits
                                // re-population on hover-out re-renders.
                                // Setting Text directly (even value-guarded)
                                // leaves the rendered run unanchored, which
                                // lets WinUI drop the text glyphs during a
                                // pointer-exit re-render while keeping any
                                // active selection rectangles around. This
                                // mirrors the working markdown-bubble path.
                                TextBlock("")
                                    .Set(t =>
                                    {
                                        // Hoisted static FontFamily is the
                                        // critical bit — reassigning the same
                                        // reference is a DP-equality no-op,
                                        // so this setter is safe to re-run on
                                        // every render without invalidating
                                        // the selection.
                                        t.FontFamily = s_monoFontFamily;
                                        t.FontSize = 11;
                                        t.TextWrapping = TextWrapping.Wrap;
                                        t.IsTextSelectionEnabled = true;
                                        t.LineHeight = 16;
                                        ApplyPlainSelectableInlines(t, displayText);
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
                         .Margin(0, 4, 0, 0);

                        // Outer Grid: phantom chevron in col 0 reserves the
                        // header-chevron width + 6px gap (via the chevron's
                        // right margin); col 1 (label + code block) starts
                        // exactly where the header ⚡ does.
                        var sectionGrid = Grid(
                            [GridSize.Auto, GridSize.Star()],
                            [GridSize.Auto, GridSize.Auto],
                            PhantomChevron().Grid(row: 0, column: 0),
                            labelRow.Grid(row: 0, column: 1),
                            codeBlock.Grid(row: 1, column: 1)
                        );

                        return Border(sectionGrid)
                            .Padding(bubblePadding.Left, 6, bubblePadding.Right, 8);
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

                    hasExpandedBody = sections.Count > 0;
                    if (hasExpandedBody)
                    {
                        var bodyBorder = Border(VStack(0, sections.ToArray())).Background(blockHeaderBg);
                        // When this row is the last in the card AND expanded
                        // with actual content, the body sits at the bottom
                        // edge — it must own the bottom rounded corners so
                        // the row blends into the card instead of showing a
                        // square edge clipped by the card's rounding.
                        if (isLast)
                        {
                            bodyBorder = bodyBorder.Set(b => b.CornerRadius = new CornerRadius(0, 0, 8, 8));
                        }
                        body = bodyBorder;
                    }
                    // sections.Count == 0 leaves `body` as the default
                    // Empty() above so no phantom bordered strip stacks under
                    // the header. The header keeps its own bottom rounding
                    // via hasExpandedBody=false.
                }

                Action toggle = () =>
                {
                    var next = new HashSet<string>(expandedToolChips.Value);
                    if (!next.Add(token)) next.Remove(token);
                    suppressAutoFollowRef.Current = true;
                    expandedToolChips.Set(next);
                };

                // Scope the toggle Button to the header only — the body
                // contains selectable TextBlocks (TOOL OUTPUT / CALL), and
                // wrapping body in the Button caused unhandled PointerReleased
                // events inside the body's padding/whitespace to bubble up
                // and fire Click → collapse the section while the user was
                // trying to select text.
                var headerButton = Button(headerRow, toggle)
                    .Set(b =>
                    {
                        b.HorizontalAlignment = HorizontalAlignment.Stretch;
                        b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                        b.Padding = new Thickness(0);
                        // Top corners follow isFirst. Bottom corners follow
                        // isLast unless the expanded body below owns them
                        // (i.e., expanded AND has at least one section).
                        b.CornerRadius = new CornerRadius(
                            isFirst ? 8 : 0,
                            isFirst ? 8 : 0,
                            (isLast && !hasExpandedBody) ? 8 : 0,
                            (isLast && !hasExpandedBody) ? 8 : 0);
                    })
                    .Resources(r => r
                        .Set("ButtonBackground", new SolidColorBrush(Colors.Transparent))
                        // Subtler-than-default hover for tool cards (Scott
                        // feedback): Tertiary on hover, Secondary on press —
                        // one step lighter than the icon-button family. The
                        // themed brushes adapt to light/dark/HC automatically,
                        // unlike the previous 0x10/0x1C black-alpha overlays
                        // which vanished on dark surfaces.
                        .Set("ButtonBackgroundPointerOver", themeBrush("SubtleFillColorTertiaryBrush"))
                        .Set("ButtonBackgroundPressed", themeBrush("SubtleFillColorSecondaryBrush"))
                        .Set("ButtonBorderBrush", new SolidColorBrush(Colors.Transparent))
                        .Set("ButtonBorderBrushPointerOver", new SolidColorBrush(Colors.Transparent))
                        .Set("ButtonBorderBrushPressed", new SolidColorBrush(Colors.Transparent)));

                // 1px separator between rows (skipped on the first row). The
                // separator lives inside the row so collapsed/expanded heights
                // both keep continuity with the next row above.
                var rowStack = VStack(0, headerButton, body);
                var rowWithSeparator = isFirst
                    ? (Element)rowStack
                    : Border(rowStack)
                        .Set(b =>
                        {
                            b.BorderThickness = new Thickness(0, 1, 0, 0);
                            b.BorderBrush = toolCardBorderBrush;
                        });

                return rowWithSeparator;
            }

            // ── Style-aware composition ──────────────────────────────
            // Read the live exploration state for the burst variant only in
            // the debug preview. Production chat uses stable Auto behavior.
            // Defaults to Auto, which picks the best variant per burst:
            //   - single-step  → Plain (one inline row, nothing to fold)
            //   - multi-step   → CompactSummary (1-line collapsed summary,
            //                    click chevron to expand the steps)
            // CompactSummary applies even while a step is in-flight: the
            // header's aggregate status pill flips to "Running" (and back
            // to "Done") so the user sees live progress without the group
            // momentarily expanding to show the running row. Expanding
            // mid-burst would yank the per-step list back into view every
            // time the agent invoked another tool, which is exactly what
            // collapsed mode is supposed to avoid.
            var style = Props.EnableExplorationControls ? ChatExplorationState.ToolBurstStyle : ToolBurstStyle.Auto;
            if (style == ToolBurstStyle.Auto)
            {
                style = entries.Count >= 2
                    ? ToolBurstStyle.CompactSummary
                    : ToolBurstStyle.Plain;
            }
            var showStepNumbers = Props.EnableExplorationControls && ChatExplorationState.ShowStepNumbers && entries.Count > 1;
            var stepCount = entries.Count;

            // Tool burst alignment: align outer left to the assistant bubble's
            // outer left edge + a small indent so the card visually reads as
            // "owned by" the assistant bubble above. Width math:
            //   bubble outer left = gutter(16) + avatar(36) + sideMargin
            //   tool card outer left = bubble outer left + indent
            // When avatars are globally hidden, drop the avatar slot but still
            // keep the indent so the tool card sits inside the bubble's reading
            // column rather than aligning to the gutter.
            // Exception: when there's no assistant bubble in this turn yet
            // (i.e. the tool ran while the agent is still "thinking…" and no
            // reply has streamed), drop the indent so the card aligns flush
            // under the thinking indicator instead of hanging extra-right.
            bool hasBubbleAbove = bubbleSlot != null;
            int toolIndent = hasBubbleAbove ? 16 : 0;
            var toolAvatarSlot = showAssistAvatar ? (36 + (int)bubbleSideMargin) : 0;
            var toolLeftMargin = 16 + toolAvatarSlot + toolIndent;

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
                Id: "agg", Kind: ChatTimelineItemKind.ToolCall, Text: string.Empty,
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
            Element CardOf(Element[] rowEls) => Border(VStack(0, rowEls))
                .Background(toolCardBgBrush)
                .WithBorder(toolCardBorderBrush, toolCardBorderThickness)
                .Set(b =>
                {
                    // CornerRadius is uniform across the card (BubbleCornerRadius
                    // is a single value broadcast to all four corners by
                    // ChatVisualResolver). Setting CornerRadius directly works
                    // because rounding nests under the Border's BorderThickness.
                    b.CornerRadius = bubbleRadius;

                    if (nested)
                    {
                        // Inside the assistant bubble: stretch to bubble's
                        // content width — exactly 100%. No MinWidth, no
                        // MaxWidth, no margin. The bubble's Padding already
                        // creates the visual inset from the rounded corners,
                        // and the headerRow's own Padding insets the Done
                        // pill from this card's edge. Anything extra (like
                        // MinWidth=360 we tried before) would force the
                        // card wider than the bubble in narrow viewports
                        // and push the pill past the bubble's right edge.
                        b.HorizontalAlignment = HorizontalAlignment.Stretch;
                        return;
                    }

                    b.MaxWidth = 720 - toolIndent;
                    // Floor the tool card width so the 5-column header grid
                    // (chevron / ⚡ / label / summary / status pill) doesn't
                    // clip when the assistant reply is short (e.g. "Done.")
                    // and the bubble sizes itself to 80–120 DIPs.
                    b.MinWidth = 360;
                    b.HorizontalAlignment = HorizontalAlignment.Left;

                    // If an assistant bubble was rendered earlier in this turn,
                    // bind the tool card's Width to the bubble's ActualWidth
                    // minus the indent so the right edges stay parallel and
                    // the tool card visually nests inside the bubble's reading
                    // column — regardless of how the bubble's content sized it.
                    var slotBubble = bubbleSlot != null ? bubbleSlot[0] : null;
                    if (slotBubble != null)
                    {
                        void Sync()
                        {
                            var w = slotBubble.ActualWidth - toolIndent;
                            // Clamp to MinWidth so the 5-column header grid
                            // (chevron / ⚡ / label / summary / status pill)
                            // never overflows when the assistant bubble is
                            // narrow (e.g. "Done."). Without this clamp the
                            // Star summary column shrinks to 0 but Auto
                            // columns still take natural width, pushing the
                            // status pill past the card's rounded-corner
                            // clip and visually truncating "Done".
                            if (w > 0) b.Width = Math.Max(360, w);
                        }
                        // Keep the SizeChanged subscription paired with the
                        // card's lifetime: every re-render creates a fresh
                        // Border, so without the Unloaded detach the bubble
                        // would accumulate handlers on every timeline
                        // re-render (one per turn). Capture the handler in
                        // a local so we have a stable reference to remove.
                        Microsoft.UI.Xaml.SizeChangedEventHandler onBubbleSize = (_, _) => Sync();
                        b.Loaded += (_, _) =>
                        {
                            slotBubble.SizeChanged += onBubbleSize;
                            Sync();
                        };
                        b.Unloaded += (_, _) =>
                        {
                            slotBubble.SizeChanged -= onBubbleSize;
                        };
                        Sync();
                    }
                });

            // Wrap a finished card with the appropriate outer layout. In nested
            // mode the card is returned as a stretchable child element with a
            // small top gap (the assistant bubble's padding takes care of the
            // sides). In external mode the card is anchored to toolLeftMargin
            // so its right edge stays parallel to the bubble's, with 6/6
            // vertical breathing room and the gutter on the right.
            // Nested mode stays inside the assistant bubble, but keep a small
            // radius-aware right inset so status pills avoid the parent
            // bubble's rounded-corner clip in cozy/high-contrast layouts.
            var nestedSideInset = (int)Math.Round(Math.Min(bubbleRadius.TopRight, bubblePadding.Right));
            Element Wrap(Element card) => nested
                ? card.HAlign(HorizontalAlignment.Stretch).Margin(0, 0, nestedSideInset, 0)
                : AnchorLeft(card).HAlign(HorizontalAlignment.Stretch).Margin(toolLeftMargin, 6, gutter, 6);

            // Wrap the card in a left-anchored single-Star Grid so the card
            // is measured with the **finite** parent slot width instead of
            // infinity. Previously this used [Auto, Star] with the card in
            // the Auto column, which gave the card unbounded measure — and
            // when the summary text was long, the card grew to its MaxWidth
            // (720) and overflowed narrow chat viewports, clipping the
            // status pill on the right. With a single Star column the
            // card.HAlign=Left anchors it to the left at its natural width
            // up to the column's actual width, so it can never exceed the
            // chat surface.
            Element AnchorLeft(Element card) => Grid(
                [GridSize.Star()],
                [GridSize.Auto],
                card.Grid(row: 0, column: 0)
            ).HAlign(HorizontalAlignment.Stretch);

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
                    var expanding = next.Add(summaryToken);
                    if (!expanding) next.Remove(summaryToken);
                    // Suppress auto-follow so the scroll position stays put
                    // while the card unfurls — the SizeChanged handler would
                    // otherwise chase the new bottom.
                    suppressAutoFollowRef.Current = true;
                    expandedToolChips.Set(next);
                };

                var summaryHeader = Border(
                    Grid(
                        // 2-col layout (left content Star + status pill Auto)
                        // — see headerRow above for the rationale.
                        [GridSize.Star(), GridSize.Auto],
                        [GridSize.Auto],
                        Grid(
                            [GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Star()],
                            [GridSize.Auto],
                            Caption(summaryChevron).Foreground(TertiaryText)
                                .VAlign(VerticalAlignment.Center)
                                .Grid(row: 0, column: 0),
                            Caption("⚡").Foreground(taskStatusBg)
                                .VAlign(VerticalAlignment.Center)
                                .Margin(6, 0, 0, 0)
                                .Grid(row: 0, column: 1),
                            Caption($"Task · {stepCount} steps").Foreground(SecondaryText)
                                .Set(t =>
                                {
                                    t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                                })
                                .VAlign(VerticalAlignment.Center)
                                .Margin(6, 0, 0, 0)
                                .Grid(row: 0, column: 2),
                            Caption("· " + toolList).Foreground(TertiaryText)
                                .Set(t =>
                                {
                                    t.TextWrapping = TextWrapping.Wrap;
                                    t.TextTrimming = TextTrimming.CharacterEllipsis;
                                    t.MaxLines = 1;
                                })
                                .VAlign(VerticalAlignment.Center)
                                .Margin(6, 0, 12, 0)
                                .Grid(row: 0, column: 3)
                        ).Grid(row: 0, column: 0),
                        Border(
                            Caption(taskStatusText).Foreground(themeBrush("TextOnAccentFillColorPrimaryBrush"))
                                .Set(t => { t.FontSize = 11; t.LineHeight = 16; })
                                .VAlign(VerticalAlignment.Center)
                        ).Background(taskStatusBg)
                         .CornerRadius(10)
                         .Padding(8, 0, 8, 0)
                         .Set(b => b.MinHeight = 18)
                         .VAlign(VerticalAlignment.Center)
                         .HAlign(HorizontalAlignment.Right)
                         .Grid(row: 0, column: 1)
                    ).HAlign(HorizontalAlignment.Stretch).Padding(bubblePadding.Left, bubblePadding.Top, bubblePadding.Right, bubblePadding.Bottom)
                ).Set(b => b.MinHeight = 32);

                var summaryButton = Button(summaryHeader, toggleSummary).Set(b =>
                {
                    b.HorizontalAlignment = HorizontalAlignment.Stretch;
                    b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    b.Padding = new Thickness(0);
                    b.CornerRadius = new CornerRadius(bubbleRadius.TopLeft, bubbleRadius.TopRight, summaryExpanded ? 0 : bubbleRadius.BottomRight, summaryExpanded ? 0 : bubbleRadius.BottomLeft);
                }).Resources(r => r
                    .Set("ButtonBackground", new SolidColorBrush(Colors.Transparent))
                    .Set("ButtonBackgroundPointerOver", themeBrush("SubtleFillColorTertiaryBrush"))
                    .Set("ButtonBackgroundPressed", themeBrush("SubtleFillColorSecondaryBrush"))
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
                return Wrap(CardOf(pieces.ToArray()));
            }

            // TaskHeader: prepend a non-clickable header row to the card.
            if (style == ToolBurstStyle.TaskHeader && entries.Count > 1)
            {
                var taskHeader = Border(
                    Border(
                        (FlexRow(
                            Caption("⚡").Foreground(taskStatusBg)
                                .VAlign(VerticalAlignment.Center),
                            Caption($"Task · {stepCount} steps").Foreground(SecondaryText)
                                .Set(t => { t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; })
                                .VAlign(VerticalAlignment.Center),
                            Caption(string.Empty).Flex(grow: 1),
                            Border(
                                Caption(taskStatusText).Foreground(themeBrush("TextOnAccentFillColorPrimaryBrush"))
                                    .Set(t => { t.FontSize = 11; t.LineHeight = 16; })
                                    .VAlign(VerticalAlignment.Center)
                            ).Background(taskStatusBg).CornerRadius(10).Padding(8, 0, 8, 0)
                             .Set(b => b.MinHeight = 18).VAlign(VerticalAlignment.Center)
                        ) with { ColumnGap = 6 }).Padding(12, 8, 12, 8)
                    ).Set(b => b.MinHeight = 22)
                ).Background(themeBrush("SubtleFillColorSecondaryBrush"));

                var combined = new Element[entries.Count + 1];
                combined[0] = taskHeader;
                Array.Copy(rows, 0, combined, 1, rows.Length);

                return Wrap(CardOf(combined));
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
                                .Foreground(themeBrush("SystemFillColorSuccessBrush"))
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
                    suppressAutoFollowRef.Current = true;
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
                                    .Set(t => { t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; })
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
                            t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                            t.TextTrimming = TextTrimming.CharacterEllipsis;
                            t.MaxLines = 1;
                        })
                        .VAlign(VerticalAlignment.Center).Flex(grow: 1),
                    Caption(stepCountLabel).Foreground(TertiaryText)
                        .Set(t => { t.FontSize = 11; }).VAlign(VerticalAlignment.Center),
                    Caption(chevron).Foreground(TertiaryText)
                        .Set(t => { t.FontSize = 11; }).VAlign(VerticalAlignment.Center)
                ) with { ColumnGap = 8 }).Margin(0, 0, 0, 0);

                var headerButton = Button(headerContent, toggleTaskList).Set(b =>
                {
                    b.HorizontalAlignment = HorizontalAlignment.Stretch;
                    b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    b.Padding = bubblePadding;
                    b.CornerRadius = new CornerRadius(bubbleRadius.TopLeft, bubbleRadius.TopRight, effectiveExpanded ? 0 : bubbleRadius.BottomRight, effectiveExpanded ? 0 : bubbleRadius.BottomLeft);
                }).Resources(r => r
                    .Set("ButtonBackground", new SolidColorBrush(Colors.Transparent))
                    .Set("ButtonBackgroundPointerOver", themeBrush("SubtleFillColorTertiaryBrush"))
                    .Set("ButtonBackgroundPressed", themeBrush("SubtleFillColorSecondaryBrush"))
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
                 .WithBorder(toolCardBorderBrush, toolCardBorderThickness)
                 // CornerRadius is uniform (single value from ChatVisualResolver
                 // broadcast to all four corners); safe to assign directly.
                 .Set(b =>
                 {
                     b.CornerRadius = bubbleRadius;
                     if (nested)
                     {
                         b.HorizontalAlignment = HorizontalAlignment.Stretch;
                     }
                     else
                     {
                         b.MaxWidth = 720;
                         b.HorizontalAlignment = HorizontalAlignment.Left;
                     }
                 });

                if (nested)
                    return listCard.HAlign(HorizontalAlignment.Stretch);

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
                    leftSlot.Grid(row: 0, column: 0).Margin(0, 0, showAssistAvatar ? bubbleSideMargin : 0, 0),
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
            // Both styles align the card to the bubble's text edge + indent
            // (see toolLeftMargin) so the burst visually belongs to the
            // assistant bubble it follows.
            if (style == ToolBurstStyle.FooterReframe)
            {
                return Wrap(CardOf(rows));
            }

            return Wrap(CardOf(rows));
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
                                    t.FontFamily = s_monoFontFamily;
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
        // Reorder display so that within each turn (segment between User entries)
        // ToolCall bursts render AFTER the assistant reply (or the thinking
        // indicator if no reply has streamed yet). Gateway emits tool_start
        // before assistant_delta, but the desired visual flow is
        //   [User] → [Assistant reply / thinking] → [Tool burst]
        // so the assistant message reads first and the tool work hangs below it.
        var orderedIdx = new int[Props.Entries.Count];
        {
            int outPos = 0;
            int turnStart = 0;
            void Flush(int endExclusive)
            {
                for (int j = turnStart; j < endExclusive; j++)
                    if (Props.Entries[j].Kind != ChatTimelineItemKind.ToolCall)
                        orderedIdx[outPos++] = j;
                for (int j = turnStart; j < endExclusive; j++)
                    if (Props.Entries[j].Kind == ChatTimelineItemKind.ToolCall)
                        orderedIdx[outPos++] = j;
            }
            for (int i = 0; i < Props.Entries.Count; i++)
            {
                if (Props.Entries[i].Kind == ChatTimelineItemKind.User && i > turnStart)
                {
                    Flush(i);
                    turnStart = i;
                }
            }
            Flush(Props.Entries.Count);
        }

        // Per-turn slot shared between an assistant bubble and tool cards
        // rendered below it in the same turn. Reset at each User entry.
        Microsoft.UI.Xaml.Controls.Border[]? currentBubbleSlot = null;

        // Pre-compute the last orderedIdx position of the turn containing each
        // entry. Used to look ahead from an assistant entry to its tool burst
        // (which the orderedIdx reorder always places at the tail of the turn)
        // so we can decide whether to nest the burst inside the assistant
        // bubble's content area.
        var turnEndAt = new int[orderedIdx.Length];
        {
            int ts = 0;
            for (int k = 1; k < orderedIdx.Length; k++)
            {
                if (Props.Entries[orderedIdx[k]].Kind == ChatTimelineItemKind.User)
                {
                    for (int j = ts; j < k; j++) turnEndAt[j] = k - 1;
                    ts = k;
                }
            }
            for (int j = ts; j < orderedIdx.Length; j++) turnEndAt[j] = orderedIdx.Length - 1;
        }

        // orderedIdx positions whose ToolCall entries have been "consumed" by
        // an assistant bubble that nested them inline — render Empty() for
        // these so the burst doesn't appear twice (once inside the bubble,
        // once below it).
        var nestedConsumed = new System.Collections.Generic.HashSet<int>();

        // Nestable rule: tool calls belong to the assistant turn, so render
        // them inside the assistant/thinking bubble whenever they can fit as
        // a compact row. Keep error bursts external so failures remain
        // visually prominent.
        bool BurstIsNestable(System.Collections.Generic.List<ChatTimelineItem> b)
        {
            if (b.Count == 0) return false;
            // Error rows stay external regardless of count so the failure is
            // visually prominent (red status pill + dedicated card) instead
            // of folded inside an assistant reply where it would be easy to
            // miss while skimming.
            foreach (var e in b)
            {
                if (e.ToolResult == ChatToolCallStatus.Error) return false;
            }
            if (b.Count == 1) return true;
            // Multi-step bursts collapse into a single CompactSummary row
            // under Auto / CompactSummary, so they fit comfortably inside
            // an assistant bubble even while a step is in-flight — the
            // aggregate status pill on the header shows Running/Done.
            var s = Props.EnableExplorationControls ? ChatExplorationState.ToolBurstStyle : ToolBurstStyle.Auto;
            return s == ToolBurstStyle.Auto || s == ToolBurstStyle.CompactSummary;
        }

        for (int k = 0; k < orderedIdx.Length; k++)
        {
            int i = orderedIdx[k];
            var entry = Props.Entries[i];
            var prevKind = k > 0 ? Props.Entries[orderedIdx[k - 1]].Kind : (ChatTimelineItemKind?)null;
            var nextKind = k < orderedIdx.Length - 1 ? Props.Entries[orderedIdx[k + 1]].Kind : (ChatTimelineItemKind?)null;
            var startsBurst = prevKind is null || !SameBurstKind(prevKind.Value, entry.Kind);
            var endsBurst = nextKind is null || !SameBurstKind(entry.Kind, nextKind.Value);
            var showAvatar = !(prevKind is { } pk && IsAgentSide(pk) && IsAgentSide(entry.Kind));

            // Reset the bubble slot at each new User turn so tool cards
            // never bind to a bubble from a previous conversation turn.
            if (entry.Kind == ChatTimelineItemKind.User)
                currentBubbleSlot = null;

            // Coalesce contiguous ToolCall entries into a single unified
            // burst card so a multi-step assistant turn reads as one tidy
            // block instead of N separate chips with repeated footers.
            if (entry.Kind == ChatTimelineItemKind.ToolCall)
            {
                if (!showToolCalls)
                {
                    renderedEntries[k] = Empty().WithKey(entry.Id);
                    continue;
                }
                if (!startsBurst)
                {
                    renderedEntries[k] = Empty().WithKey(entry.Id);
                    continue;
                }
                if (nestedConsumed.Contains(k))
                {
                    // The assistant bubble above already rendered this burst
                    // inline as a child element — emit nothing here.
                    renderedEntries[k] = Empty().WithKey(entry.Id);
                    continue;
                }
                var burst = new System.Collections.Generic.List<ChatTimelineItem> { entry };
                int kj = k + 1;
                while (kj < orderedIdx.Length && Props.Entries[orderedIdx[kj]].Kind == ChatTimelineItemKind.ToolCall)
                {
                    burst.Add(Props.Entries[orderedIdx[kj]]);
                    kj++;
                }
                renderedEntries[k] = RenderToolBurst(burst, showAvatar, currentBubbleSlot).WithKey(entry.Id);
                continue;
            }

            if (entry.Kind == ChatTimelineItemKind.Assistant)
            {
                currentBubbleSlot ??= new Microsoft.UI.Xaml.Controls.Border[1];

                // Look ahead inside the same turn for a contiguous tool burst
                // following this assistant entry. The orderedIdx reorder
                // guarantees tools are placed at the tail of the turn, so
                // when this is the last assistant (endsBurst && nextKind==Tool)
                // the burst starts at k+1 and runs through turnEndAt[k].
                Element? nestedTool = null;
                if (endsBurst && showToolCalls && nextKind == ChatTimelineItemKind.ToolCall)
                {
                    var lookahead = new System.Collections.Generic.List<ChatTimelineItem>();
                    int turnEnd = turnEndAt[k];
                    int kj = k + 1;
                    while (kj <= turnEnd && Props.Entries[orderedIdx[kj]].Kind == ChatTimelineItemKind.ToolCall)
                    {
                        lookahead.Add(Props.Entries[orderedIdx[kj]]);
                        kj++;
                    }
                    if (lookahead.Count > 0 && BurstIsNestable(lookahead))
                    {
                        // Render the burst in nested mode (no avatar slot, no
                        // outer left margin, stretches to bubble content
                        // width) and consume the orderedIdx positions so they
                        // don't render again as external rows below.
                        nestedTool = RenderToolBurst(lookahead, showAvatar: false, bubbleSlot: null, nested: true);
                        for (int kn = k + 1; kn < k + 1 + lookahead.Count; kn++)
                            nestedConsumed.Add(kn);
                    }
                }

                renderedEntries[k] = RenderAssistantEntry(entry, startsBurst, endsBurst, showAvatar, currentBubbleSlot, nestedTool).WithKey(entry.Id);
                continue;
            }

            renderedEntries[k] = RenderEntry(entry, startsBurst, endsBurst, showAvatar).WithKey(entry.Id);
        }

        var thinkingNestedConsumed = new System.Collections.Generic.HashSet<int>();
        Element? thinkingNestedTool = null;
        if (Props.ShowThinkingIndicator && showToolCalls && renderedEntries.Length > 0)
        {
            int lastUser = -1;
            for (int k = orderedIdx.Length - 1; k >= 0; k--)
            {
                if (Props.Entries[orderedIdx[k]].Kind == ChatTimelineItemKind.User)
                {
                    lastUser = k;
                    break;
                }
            }

            var start = lastUser + 1;
            if (start >= 0
                && start < orderedIdx.Length
                && Props.Entries[orderedIdx[start]].Kind == ChatTimelineItemKind.ToolCall)
            {
                var lookahead = new System.Collections.Generic.List<ChatTimelineItem>();
                int kj = start;
                while (kj < orderedIdx.Length && Props.Entries[orderedIdx[kj]].Kind == ChatTimelineItemKind.ToolCall)
                {
                    lookahead.Add(Props.Entries[orderedIdx[kj]]);
                    thinkingNestedConsumed.Add(kj);
                    kj++;
                }

                if (lookahead.Count > 0)
                    thinkingNestedTool = RenderToolBurst(lookahead, showAvatar: false, bubbleSlot: null, nested: true);
            }
        }

        // Inline "thinking" indicator rendered as a real assistant bubble
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
            var thinkingText = trimmed + animatedSuffix;
            thinkingIndicator = RenderAssistantEntry(
                new ChatTimelineItem("__thinking__", ChatTimelineItemKind.Assistant, thinkingText, IsStreaming: true),
                startsBurst: true,
                endsBurst: true,
                showAvatar: true,
                overrideBubbleContent: TextBlock(thinkingText)
                    .Set(t =>
                    {
                        t.TextWrapping = TextWrapping.Wrap;
                        t.IsTextSelectionEnabled = true;
                        t.FontStyle = global::Windows.UI.Text.FontStyle.Italic;
                        t.Foreground = themeBrush("TextFillColorSecondaryBrush");
                    }),
                nestedTool: thinkingNestedTool,
                suppressFooter: true,
                forceVisible: true)
                .LiveRegion(Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        }

        // Build the final element list, splicing the thinking indicator
        // inline RIGHT AFTER the most recent User entry so tool bursts that
        // follow it visually hang below the "Agent is thinking…" bubble
        // (and below the assistant reply once one streams in).
        Element[] timelineRows;
        if (Props.ShowThinkingIndicator && renderedEntries.Length > 0)
        {
            int insertAfter = -1;
            for (int k = orderedIdx.Length - 1; k >= 0; k--)
            {
                if (Props.Entries[orderedIdx[k]].Kind == ChatTimelineItemKind.User)
                {
                    insertAfter = k;
                    break;
                }
            }
            var spliced = new System.Collections.Generic.List<Element>(renderedEntries.Length + 1);
            for (int k = 0; k < renderedEntries.Length; k++)
            {
                if (thinkingNestedConsumed.Contains(k))
                    continue;
                spliced.Add(renderedEntries[k]);
                if (k == insertAfter) spliced.Add(thinkingIndicator);
            }
            if (insertAfter < 0) spliced.Insert(0, thinkingIndicator);
            timelineRows = spliced.ToArray();
        }
        else if (Props.ShowThinkingIndicator)
        {
            timelineRows = new[] { thinkingIndicator };
        }
        else
        {
            timelineRows = renderedEntries;
        }

        return Grid([GridSize.Star()], [GridSize.Star()],
            // Page background matches dash-light --bg so bubbles stand out.
            Border(
                ScrollView(
                    Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Auto],
                        loadMoreButton.Grid(row: 0, column: 0),
                        Border(Empty()).Height(20).Grid(row: 1, column: 0),
                        VStack(2, timelineRows).Set(sp =>
                        {
                            if (contentRef.Current != sp)
                            {
                                contentRef.Current = (Microsoft.UI.Xaml.Controls.StackPanel)sp;
                                sp.SizeChanged += (_, _) =>
                                {
                                    if (scrollViewRef.Current is not { } sv) return;

                                    // Pending scroll restoration from initialLoad — apply
                                    // once the layout is ready (ScrollableHeight > 0).
                                    if (pendingRestoreOffsetRef.Current is { } pendingOffset && sv.ScrollableHeight > 0)
                                    {
                                        pendingRestoreOffsetRef.Current = null;
                                        var target = ClampOffset(pendingOffset, sv.ScrollableHeight);
                                        isFollowingRef.Current = sv.ScrollableHeight - target <= FollowThreshold;
                                        sv.ChangeView(null, target, null, disableAnimation: true);
                                        lastVerticalOffsetRef.Current = target;
                                        lastScrollableHeightRef.Current = sv.ScrollableHeight;
                                        suppressAutoFollowRef.Current = false;
                                        return;
                                    }

                                    if (!suppressAutoFollowRef.Current && isFollowingRef.Current)
                                    {
                                        QueueScrollToBottom(sv, prevSessionIdRef.Current, disableAnimation: true);
                                    }
                                    else if (suppressAutoFollowRef.Current)
                                    {
                                        // Reset after one suppressed layout pass (e.g. tool expand/collapse).
                                        suppressAutoFollowRef.Current = false;
                                    }
                                };
                            }
                        }).Grid(row: 2, column: 0),
                        Border(Empty()).Height(24).Grid(row: 3, column: 0)
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

                if (sessionChanged && !isFirstMount)
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
                    // Check instance-level ref first, then static store (survives page recreation)
                    double? savedOffset = null;
                    if (Props.SessionId is not null)
                    {
                        if (sessionOffsetsRef.Current.TryGetValue(Props.SessionId, out var refOffset))
                            savedOffset = refOffset;
                        else if (s_sessionOffsets.TryGetValue(Props.SessionId, out var staticOffset))
                            savedOffset = staticOffset;
                    }

                    if (savedOffset is not null)
                    {
                        // Defer the scroll restoration: the ScrollViewer hasn't
                        // laid out yet (ScrollableHeight=0) so we can't scroll
                        // now. Store the target and let SizeChanged apply it
                        // once the layout is ready.
                        pendingRestoreOffsetRef.Current = savedOffset.Value;
                        suppressAutoFollowRef.Current = true;
                        isFollowingRef.Current = false;
                    }
                    else
                        QueueScrollToBottom(sv, Props.SessionId, disableAnimation: true);
                }
                else if (appendedEntries && isFollowingRef.Current)
                {
                    QueueScrollToBottom(sv, Props.SessionId, disableAnimation: false);
                }

                // External scroll-to-bottom request (e.g. user sent a message)
                if (Props.ScrollToBottomToken != prevScrollToBottomTokenRef.Current)
                {
                    prevScrollToBottomTokenRef.Current = Props.ScrollToBottomToken;
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
