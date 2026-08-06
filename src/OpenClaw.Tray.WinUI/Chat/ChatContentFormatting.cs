using System;
using System.Buffers;
using OpenClaw.Chat;
#if !OPENCLAW_TRAY_TESTS
using OpenClawTray.Helpers;
#endif
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

internal static class ChatContentFormatting
{
    // Keep this value in sync with OpenClawChatDataProvider.MaxEntryTextBytes for test compatibility.
    private const int MaxEntryTextBytes = 256 * 1024;

    /// <summary>
    /// Truncate <paramref name="text"/> to at most
    /// <see cref="MaxEntryTextBytes"/> bytes when encoded as UTF-8 and
    /// append a <c> … [N bytes truncated]</c> marker. Slices at a UTF-16
    /// code-unit boundary that doesn't split a surrogate pair, then
    /// verifies the byte budget. Returns the input unchanged when it
    /// already fits or is null/empty.
    /// </summary>
    internal static string TruncateForChatEntry(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var enc = System.Text.Encoding.UTF8;
        // Cheap upper bound: every char is at most 3 UTF-8 bytes for the
        // BMP and surrogate pairs encode to 4 bytes / 2 chars (still ≤ 3
        // bytes per char). 4 is the worst case and keeps the cheap path
        // safe. If even the worst case fits, we're done.
        if ((long)text.Length * 4 <= MaxEntryTextBytes) return text;
        var actual = enc.GetByteCount(text);
        if (actual <= MaxEntryTextBytes) return text;

        // Binary search for the largest char-count whose UTF-8 byte count
        // fits in MaxEntryTextBytes minus a generous margin for the marker.
        var marker = string.Format(LocalizationHelper.GetString("Chat_TruncationMarkerFormat"), actual);
        int budget = MaxEntryTextBytes - enc.GetByteCount(marker);
        if (budget <= 0) budget = MaxEntryTextBytes / 2;

        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            // Don't split a surrogate pair: nudge mid back if it lands on
            // a low surrogate.
            if (mid < text.Length && char.IsLowSurrogate(text[mid])) mid--;
            if (mid <= lo)
            {
                hi = lo;
                continue;
            }
            int bytes = enc.GetByteCount(text.AsSpan(0, mid));
            if (bytes <= budget) lo = mid;
            else hi = mid - 1;
        }
        if (lo > 0 && char.IsHighSurrogate(text[lo - 1])) lo--;

        Logger.Debug($"[ChatTruncate] message {actual} bytes → {lo} chars (~{enc.GetByteCount(text.AsSpan(0, lo))} bytes); cap={MaxEntryTextBytes}");
        return string.Concat(text.AsSpan(0, lo), marker.AsSpan());
    }

    /// <summary>
    /// True when text is one of the approval slash-commands we send on the
    /// user's behalf (<c>/approve &lt;slug&gt; allow-once</c>,
    /// <c>/approve &lt;slug&gt; allow-always</c>, or
    /// <c>/deny &lt;slug&gt;</c>). Matches the exact dashboard grammar
    /// — not just the prefix — so legitimate user prose like
    /// "/approve the design changes" still renders as a normal bubble.
    /// </summary>
    /// <remarks>
    /// Slug shape: hex-ish identifier (letters, digits, dashes, underscores;
    /// 4–64 chars). This mirrors what the gateway emits for
    /// ``approvalSlug``; we don't anchor on a specific length because the
    /// gateway has changed it before.
    /// </remarks>
    internal static bool LooksLikeApprovalSlashCommand(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var t = text.Trim();
        return s_approvalSlashCommandRegex.IsMatch(t);
    }

    private static readonly System.Text.RegularExpressions.Regex s_approvalSlashCommandRegex =
        new(@"^/(?:approve\s+[A-Za-z0-9_-]{4,64}(?:\s+(?:allow-once|allow-always))?|deny\s+[A-Za-z0-9_-]{4,64})\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex s_seamBoldClose =
        new(@"(?<=[a-z0-9])(\*\*)(?=[A-Z])",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex s_seamSentencePunct =
        new(@"(?<=[a-z0-9][.!?:])(?=[A-Z][a-z]+[\s,;:!?])",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly SearchValues<char> s_seamPunctChars = SearchValues.Create(".!?:");

    /// <summary>
    /// Re-insert paragraph breaks at gateway-glued content-block seams in
    /// an assistant message. Safe to call on any text — short text, text
    /// without seams, and text that is entirely fenced code all pass
    /// through unchanged. Fenced code blocks (``` ``` ``` ```) are skipped
    /// so JSON/code samples never get whitespace injected inside them.
    /// </summary>
    internal static string RepairContentBlockSeams(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        if (text.Length < 4) return text;

        // Fast path: if neither marker is present we can skip entirely.
        if (!text.Contains("**", System.StringComparison.Ordinal) &&
            text.AsSpan().IndexOfAny(s_seamPunctChars) < 0)
        {
            return text;
        }

        // Walk the string, alternating between prose and fenced-code
        // segments. Apply seam regexes to prose only. We tolerate
        // unclosed fences by treating everything after the dangling
        // opener as code (matches Markdown renderer behavior).
        var sb = new System.Text.StringBuilder(text.Length + 16);
        int i = 0;
        while (i < text.Length)
        {
            int fenceStart = text.IndexOf("```", i, System.StringComparison.Ordinal);
            if (fenceStart < 0)
            {
                sb.Append(RepairProseSegment(text[i..]));
                break;
            }

            sb.Append(RepairProseSegment(text.Substring(i, fenceStart - i)));

            int fenceEnd = text.IndexOf("```", fenceStart + 3, System.StringComparison.Ordinal);
            if (fenceEnd < 0)
            {
                // Unclosed fence — append the rest verbatim as code.
                sb.Append(text, fenceStart, text.Length - fenceStart);
                break;
            }

            // Append fenced block verbatim (including both fence markers).
            sb.Append(text, fenceStart, fenceEnd - fenceStart + 3);
            i = fenceEnd + 3;
        }

        return sb.ToString();
    }

    internal static string RepairProseSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment)) return segment;
        segment = s_seamBoldClose.Replace(segment, "$1\n\n");
        // s_seamSentencePunct is a zero-width assertion (lookbehind +
        // lookahead) so the replacement is a pure insert of "\n\n" at
        // the seam — no captured punctuation to re-emit.
        segment = s_seamSentencePunct.Replace(segment, "\n\n");
        return segment;
    }

    // Per-process random seed for ChatTraceHash. Mixing this into the FNV
    // initial state keeps identical-text frames colliding within a single
    // tray run (so duplicate-bubble diagnostics still work) while making
    // the hash useless as a content fingerprint outside this process: an
    // attacker with the log file can no longer rebuild the hash for a
    // guessed plaintext, and the value rotates on every tray restart.
    private static readonly uint ChatTraceHashSeed = unchecked((uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue));

    // Short FNV-1a-style 32-bit fold of the message text, seeded with a
    // per-process random value. Used in trace logs to tell two near-
    // duplicate frames apart at a glance without dumping the text itself.
    // Not a security hash; not reproducible outside this process.
    internal static string ChatTraceHash(string text)
    {
        if (string.IsNullOrEmpty(text)) return "00000000";
        uint h = ChatTraceHashSeed;
        for (int i = 0; i < text.Length; i++)
        {
            h ^= text[i];
            h *= 16777619u;
        }
        return h.ToString("x8");
    }

    /// <summary>
    /// Apply <see cref="TruncateForChatEntry(string?)"/> to whichever text
    /// payload a <see cref="ChatEvent"/> carries. Returns the input
    /// unchanged when there is nothing to truncate or the text already
    /// fits. Used by <see cref="ApplyEventAndPublish"/> to enforce the
    /// per-message size cap on every code path.
    /// </summary>
    /// <remarks>
    /// Coverage: every <see cref="ChatEvent"/> subtype that carries a
    /// caller-supplied text payload is truncated here, including the
    /// currently-unused
    /// <see cref="ChatModelChangedEvent"/> /
    /// <see cref="ChatPermissionRequestEvent"/> /
    /// <see cref="ChatIntentEvent"/> shapes — these don't flow through
    /// <see cref="ApplyEventAndPublish"/> today but covering them now
    /// prevents a future caller from bypassing the cap when wiring
    /// them up. The <see cref="ChatTurnEndEvent"/> /
    /// <see cref="ChatContextChangedEvent"/> shapes have no untrusted
    /// text fields and fall through unchanged.
    /// </remarks>
    internal static ChatEvent TruncateChatEvent(ChatEvent evt) => evt switch
    {
        ChatUserMessageEvent e => e with { Text = TruncateForChatEntry(e.Text) },
        ChatThinkingEvent e => e with { Text = TruncateForChatEntry(e.Text) },
        ChatReasoningEvent e => e with { Text = TruncateForChatEntry(e.Text) },
        ChatReasoningDeltaEvent e => e with { Text = TruncateForChatEntry(e.Text) },
        ChatMessageEvent e => e with
        {
            Text = TruncateForChatEntry(e.Text),
            ReasoningText = e.ReasoningText is null ? null : TruncateForChatEntry(e.ReasoningText)
        },
        ChatMessageDeltaEvent e => e with { Text = TruncateForChatEntry(e.Text) },
        ChatToolStartEvent e => e with
        {
            Text = TruncateForChatEntry(e.Text),
            ToolName = TruncateForChatEntry(e.ToolName)
        },
        ChatToolOutputEvent e => e with { Text = TruncateForChatEntry(e.Text) },
        ChatToolErrorEvent e => e with { Text = TruncateForChatEntry(e.Text) },
        ChatStatusEvent e => e with { Text = TruncateForChatEntry(e.Text) },
        ChatErrorEvent e => e with { Text = TruncateForChatEntry(e.Text) },
        ChatRestoredEvent e => e with { Text = TruncateForChatEntry(e.Text) },
        ChatRawEvent e => e with { Text = e.Text is null ? null : TruncateForChatEntry(e.Text) },
        ChatModelChangedEvent e => e with { Model = TruncateForChatEntry(e.Model) },
        ChatIntentEvent e => e with { Intent = TruncateForChatEntry(e.Intent) },
        ChatPermissionRequestEvent e => e with
        {
            PermissionKind = TruncateForChatEntry(e.PermissionKind),
            ToolName = TruncateForChatEntry(e.ToolName),
            Detail = TruncateForChatEntry(e.Detail)
        },
        _ => evt
    };

}
