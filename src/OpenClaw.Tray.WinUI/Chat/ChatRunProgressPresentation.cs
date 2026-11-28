using OpenClaw.Chat;
#if !OPENCLAW_TRAY_TESTS
using OpenClawTray.Helpers;
#endif

namespace OpenClawTray.Chat;

public sealed record ChatRunProgressPresentation(
    DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, string Label, bool Active, bool Waiting,
    long? OutputTokens = null, DateTimeOffset? RetryAt = null, string? RunId = null)
{
    /// <summary>A yielded parent may still be waiting for work, even though its own turn is inactive.</summary>
    public bool IsInProgress => Active || Waiting;
}

internal static class ChatRunProgressProjection
{
    internal const string RetryLabel = "Retrying the model request";
    internal const int StatusSwapMilliseconds = 220;

    internal static bool NeedsClock(ChatRunProgressPresentation progress, DateTimeOffset now) =>
        progress.IsInProgress && (progress.RetryAt > now ||
            progress.StartedAt.HasValue && (progress.Waiting || !progress.EndedAt.HasValue));

    /// <summary>
    /// True while a status swap started at <paramref name="startedAtMs"/> is still moving, so a newer
    /// phase continues it instead of restarting. No swap has started before the first phase change.
    /// </summary>
    internal static bool IsStatusSwapInFlight(long? startedAtMs, long nowMs) =>
        startedAtMs is { } started && nowMs - started < StatusSwapMilliseconds;

    internal static ChatRunProgressPresentation? Project(ChatTimelineState timeline, bool awaitingQuestion = false,
        bool awaitingSubagents = false, IReadOnlyDictionary<string, ChatEntryMetadata>? metadata = null)
    {
        var waiting = awaitingQuestion || timeline.PendingPermission is not null
            || timeline.ProgressText == "Waiting for approval" || awaitingSubagents;
        if (!timeline.TurnActive && !waiting)
            return null;
        var activeTool = timeline.Entries.LastOrDefault(entry =>
            entry.Kind == ChatTimelineItemKind.ToolCall && entry.ToolResult == ChatToolCallStatus.InProgress);
        var currentReasoning = timeline.ReasoningStream is { ActiveItemId: not null } stream
            && (timeline.RunUsage?.RunId is null || timeline.RunUsage.RunId == stream.RunId);
        var label = awaitingQuestion ? "Waiting for your answer"
            : timeline.PendingPermission is not null || timeline.ProgressText == "Waiting for approval" ? "Waiting for approval"
            : awaitingSubagents ? "Waiting for subagents"
            : timeline.ProgressText is { Length: > 0 } progress ? progress
            : activeTool is not null ? $"Running {activeTool.ToolName ?? "tool"}"
            : timeline.ActiveReasoningId is not null || currentReasoning ? "Thinking"
            : timeline.ActiveAssistantId is { } assistantId && timeline.Entries.LastOrDefault()?.Id == assistantId
                ? "Responding"
            : "Working";
        var retryAt = label == RetryLabel && timeline.ProgressRetryAtMs is { } retryAtMs
            ? DateTimeOffset.FromUnixTimeMilliseconds(retryAtMs) : (DateTimeOffset?)null;
        var request = RequestUsage(timeline, metadata);
        var started = request is { } aggregate && (timeline.TurnStartedAt is null || aggregate.Start < timeline.TurnStartedAt)
            ? aggregate.Start : timeline.TurnStartedAt;
        return new(started, timeline.TurnEndedAt, label, timeline.TurnActive, waiting,
            request is { } result ? result.Tokens : timeline.RunUsage?.OutputTokens, retryAt, timeline.RunUsage?.RunId);
    }

    // Web request membership is display-only and conservative: only confirmed sessions_yield
    // runs behind one observed request boundary may contribute. Adjacent runs never imply membership.
    private static (DateTimeOffset Start, long? Tokens)? RequestUsage(ChatTimelineState timeline,
        IReadOnlyDictionary<string, ChatEntryMetadata>? metadata)
    {
        if (metadata is null || timeline.RunUsage?.RunId is not { Length: > 0 } current) return null;
        var seen = new HashSet<string>(StringComparer.Ordinal) { current };
        var yielded = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset? askedAt = null;
        foreach (var entry in timeline.Entries.Reverse())
        {
            var facts = entry.Presentation ?? metadata.GetValueOrDefault(entry.Id)?.Presentation;
            if (entry.Kind == ChatTimelineItemKind.User || entry.StartsTurn ||
                entry.Kind == ChatTimelineItemKind.Status && ChatNoticePresentation.StartsTurn(facts) ||
                facts is { TurnBoundary: true } or { IsIndependent: true } ||
                facts?.SenderSession is not null || facts?.SenderAgentId is not null)
            {
                askedAt = metadata.GetValueOrDefault(entry.Id)?.Timestamp;
                break;
            }
            var run = entry.ToolRunId ?? facts?.RunId;
            if (run is not null) seen.Add(run);
            if (entry.ConfirmedHandoff is { ToolName: "sessions_yield" } && run is not null && run != current)
                yielded.Add(run);
        }
        if (askedAt is not { } start || start.ToUnixTimeMilliseconds() <= 0 || yielded.Count == 0 ||
            seen.Any(run => run != current && !yielded.Contains(run))) return null;
        var usage = (timeline.ObservedRunUsage ?? []).ToDictionary(item => item.RunId, StringComparer.Ordinal);
        usage[current] = timeline.RunUsage;
        long? Sum(IEnumerable<string> members)
        {
            long total = 0;
            foreach (var run in members)
            {
                if (!usage.TryGetValue(run, out var item) || item.OutputTokens is not { } tokens || tokens < 0 ||
                    tokens > long.MaxValue - total) return null;
                total += tokens;
            }
            return total;
        }
        return (start, Sum(yielded.Append(current)) ?? Sum(yielded));
    }

    /// <summary>The claw carries ordinary activity, as in the web working indicator.</summary>
    internal static bool ShowStatus(ChatRunProgressPresentation progress) =>
        progress.Label == "Waiting for approval" || !(progress.Label is "Working" or "Thinking" or "Responding" or
            "Compacting context" or "Retrying after context compaction" or "Waiting for your answer" or "Waiting for subagents" ||
            progress.Label.StartsWith("Running ", StringComparison.Ordinal));

    /// <summary>Web run-token notation, separate from the context window's K/M notation.</summary>
    internal static string CompactTokens(long tokens)
    {
        if (tokens < 1_000) return tokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (tokens >= 1_000_000_000) return FixedOne(tokens / 1_000_000_000d) + "B";
        if (tokens < 1_000_000)
        {
            var thousands = FixedOne(tokens / 1_000d);
            if (thousands != "1000") return thousands + "k";
        }
        return FixedOne(tokens / 1_000_000d) + "M";
    }

    // JavaScript toFixed rounds the exact binary64 value, unlike .NET's decimal
    // formatting midpoint rules (for example 1.15 -> 1.1). Preserve that web contract.
    private static string FixedOne(double value)
    {
        var bits = BitConverter.DoubleToInt64Bits(value);
        var exponent = (int)((bits >> 52) & 0x7ff) - 1023 - 52;
        System.Numerics.BigInteger numerator = ((bits & 0x000fffffffffffffL) | 0x0010000000000000L);
        numerator *= 10;
        var denominator = System.Numerics.BigInteger.One;
        if (exponent >= 0) numerator <<= exponent;
        else denominator <<= -exponent;
        var rounded = System.Numerics.BigInteger.DivRem(numerator, denominator, out var remainder);
        if (remainder * 2 >= denominator) rounded++;
        var whole = System.Numerics.BigInteger.DivRem(rounded, 10, out var fraction);
        return whole.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            (fraction.IsZero ? "" : "." + fraction.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static readonly System.Text.Json.JsonSerializerOptions WorkingPhraseJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly string[] WorkingPhrases =
        ["shelling", "scuttling", "clawing", "pinching", "molting", "bubbling", "tiding", "reefing", "cracking",
         "sifting", "brining", "nautiling", "krilling", "barnacling", "lobstering", "tidepooling", "pearling", "snapping", "surfacing"];

    internal static string? WorkingPhraseKey(ChatRunProgressPresentation progress, string? sessionKey, DateTimeOffset now)
    {
        if (progress.OutputTokens is not null || progress.Waiting || !progress.Active ||
            progress.Label == RetryLabel || progress.StartedAt is not { } start ||
            string.IsNullOrEmpty(progress.RunId) || string.IsNullOrEmpty(sessionKey)) return null;
        var elapsed = (now - start).TotalMilliseconds - 30_000;
        if (elapsed < 0) return null;
        // Match JSON.stringify's display-only seed, including non-ASCII and HTML-sensitive text.
        var seed = "stream-working:" + System.Text.Json.JsonSerializer.Serialize(new[] { sessionKey, progress.RunId },
            WorkingPhraseJsonOptions);
        static uint Hash(string value)
        {
            var hash = 2166136261u;
            foreach (var character in value) hash = unchecked((hash ^ character) * 16777619u);
            return hash;
        }
        var offset = Hash(seed + ":offset") % (uint)WorkingPhrases.Length;
        var stride = 1 + Hash(seed + ":stride") % (uint)(WorkingPhrases.Length - 1);
        var step = (long)Math.Floor(elapsed / 45_000);
        return WorkingPhrases[(int)((offset + step * stride) % WorkingPhrases.Length)];
    }

    internal static string RetryLabelText(ChatRunProgressPresentation progress, DateTimeOffset now,
        Func<string, string, string> localize)
    {
        if (progress.RetryAt is { } retryAt)
        {
            var seconds = (long)Math.Ceiling((retryAt - now).TotalMilliseconds / 1000);
            if (seconds > 0)
                return string.Format(localize("Chat_Run_RetryIn", "Retrying in {0}s"), seconds);
        }
        return localize("Chat_Run_Retry", "Retrying\u2026");
    }

    internal static string Elapsed(DateTimeOffset? start, DateTimeOffset now, Func<string, string>? getString = null)
    {
        if (start is null)
            return "";
        return ElapsedMilliseconds(Math.Max(0, (now - start.Value).TotalMilliseconds), getString);
    }

    internal static string ElapsedMilliseconds(double milliseconds, Func<string, string>? getString = null)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0)
            return "";
        var seconds = Math.Floor(milliseconds / 1000);
        var (key, fallback) = seconds >= 3600
            ? ("Chat_Run_ElapsedHours", "{0}h {1}m {2}s")
            : seconds >= 60
                ? ("Chat_Run_ElapsedMinutes", "{1}m {2}s")
                : ("Chat_Run_ElapsedSeconds", "{2}s");
        var format = (getString ?? LocalizationHelper.GetString)(key);
        if (string.IsNullOrEmpty(format) || format == key)
            format = fallback;
        return string.Format(format, Math.Floor(seconds / 3600), (int)(Math.Floor(seconds / 60) % 60), (int)(seconds % 60));
    }
}
