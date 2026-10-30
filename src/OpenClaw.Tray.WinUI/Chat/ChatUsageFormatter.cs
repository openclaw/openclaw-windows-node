using OpenClaw.Chat;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace OpenClawTray.Chat;

/// <summary>Resolved context-window usage; shared by the usage pill and the throughput strip.</summary>
internal readonly record struct ChatContextUsage(long UsedTokens, long ContextTokens, int? Percent);

public static class ChatUsageFormatter
{
    public static string? Format(ChatThread? thread) => Render(Resolve(thread));

    public static string? Format(
        IReadOnlyList<ChatTimelineItem>? entries,
        IReadOnlyDictionary<string, ChatEntryMetadata>? metadata)
        => Render(ResolveLatest(entries, metadata));

    public static string? Format(ChatEntryMetadata? metadata) => Render(Resolve(metadata));

    public static string? Format(int? inputTokens, int? outputTokens, int? responseTokens, int? contextPercent)
        => Format(inputTokens, outputTokens, responseTokens, contextPercent, 0);

    public static string? Format(int? inputTokens, int? outputTokens, int? responseTokens, int? contextPercent, long fallbackContextTokens)
        => Render(Resolve(inputTokens, outputTokens, responseTokens, contextPercent, fallbackContextTokens));

    public static string? Format(long usedTokens, long contextTokens, int? contextPercent = null)
        => Render(Normalize(usedTokens, contextTokens, contextPercent));

    /// <summary>
    /// Context usage for the throughput strip: the same source the usage pill uses, but only
    /// when the window size is known so a bar can be drawn.
    /// </summary>
    internal static ChatContextUsage? ResolveContext(
        IReadOnlyList<ChatTimelineItem>? entries,
        IReadOnlyDictionary<string, ChatEntryMetadata>? metadata,
        ChatThread? thread)
        => (ResolveLatest(entries, metadata) ?? Resolve(thread)) is { ContextTokens: > 0 } usage ? usage : null;

    internal static string FormatThroughput(ChatThroughputStats stats, bool turnActive)
    {
        var current = turnActive && stats.LiveTokensPerSecond is { } live
            ? "~" + Rate(live)
            : stats.LatestTokensPerSecond is { } latest ? Rate(latest) : "—";
        return $"{current} tps · μ {RateOrDash(stats.MeanTokensPerSecond)} · p95 {RateOrDash(stats.P95TokensPerSecond)}";
    }

    private static string Rate(double value)
        => value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture);

    private static string RateOrDash(double? value) => value is { } v ? Rate(v) : "—";

    private static ChatContextUsage? Resolve(ChatThread? thread)
    {
        if (thread is null) return null;

        var usedTokens = thread.TotalTokens;
        if (usedTokens <= 0)
            usedTokens = thread.InputTokens + thread.OutputTokens;

        return Normalize(usedTokens, thread.ContextTokens, null);
    }

    private static ChatContextUsage? ResolveLatest(
        IReadOnlyList<ChatTimelineItem>? entries,
        IReadOnlyDictionary<string, ChatEntryMetadata>? metadata)
    {
        if (entries is null || metadata is null) return null;

        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            if (entry.Kind != ChatTimelineItemKind.Assistant)
                continue;

            if (metadata.TryGetValue(entry.Id, out var meta) && Resolve(meta) is { } usage)
                return usage;
        }

        return null;
    }

    private static ChatContextUsage? Resolve(ChatEntryMetadata? metadata)
        => metadata is null
            ? null
            : Resolve(metadata.InputTokens, metadata.OutputTokens, metadata.ResponseTokens, metadata.ContextPercent, metadata.ContextTokens ?? 0);

    private static ChatContextUsage? Resolve(int? inputTokens, int? outputTokens, int? responseTokens, int? contextPercent, long fallbackContextTokens)
    {
        long? usedTokens = null;
        if (fallbackContextTokens > 0 && contextPercent is int pct)
            usedTokens = (long)Math.Round(fallbackContextTokens * Math.Clamp(pct, 0, 100) / 100d, MidpointRounding.AwayFromZero);
        usedTokens ??= responseTokens
            ?? (inputTokens is int input && outputTokens is int output ? input + output : null);

        return usedTokens is long used
            ? Normalize(used, fallbackContextTokens, contextPercent)
            : null;
    }

    private static ChatContextUsage? Normalize(long usedTokens, long contextTokens, int? contextPercent)
    {
        if (usedTokens < 0) usedTokens = 0;
        if (contextTokens < 0) contextTokens = 0;

        var percent = contextPercent;
        if (percent is null && contextTokens > 0)
            percent = (int)Math.Min(100d, (double)usedTokens / contextTokens * 100d);

        if (contextTokens <= 0 && usedTokens > 0 && percent is > 0)
            contextTokens = (long)Math.Round(usedTokens * 100d / percent.Value, MidpointRounding.AwayFromZero);

        if (percent is not null)
            percent = Math.Clamp(percent.Value, 0, 100);

        if (usedTokens == 0 && contextTokens == 0)
            return null;

        if (contextTokens <= 0 && percent is null)
            return null;

        return new ChatContextUsage(usedTokens, contextTokens, contextTokens > 0 ? percent ?? 0 : percent);
    }

    private static string? Render(ChatContextUsage? usage)
    {
        if (usage is not { } u) return null;
        return u.ContextTokens > 0
            ? $"{FormatCount(u.UsedTokens)}/{FormatCount(u.ContextTokens)} ({u.Percent ?? 0}%)"
            : $"{FormatCount(u.UsedTokens)} ({u.Percent}%)";
    }

    private static string FormatCount(long value)
    {
        if (value >= 1_000_000)
        {
            var millions = value / 1_000_000d;
            return millions % 1d == 0d
                ? millions.ToString("0", CultureInfo.InvariantCulture) + "M"
                : millions.ToString("0.#", CultureInfo.InvariantCulture) + "M";
        }

        if (value >= 1_000)
            return (value / 1_000d).ToString("0.0", CultureInfo.InvariantCulture) + "K";

        return value.ToString(CultureInfo.InvariantCulture);
    }
}
