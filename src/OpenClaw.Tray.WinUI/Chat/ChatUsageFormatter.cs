using OpenClaw.Chat;
using System;
using System.Collections.Generic;

namespace OpenClawTray.Chat;

public static class ChatUsageFormatter
{
    public static string? Format(ChatThread? thread)
    {
        if (thread is null) return null;

        var usedTokens = thread.TotalTokens;
        if (usedTokens <= 0)
            usedTokens = thread.InputTokens + thread.OutputTokens;

        return Format(usedTokens, thread.ContextTokens);
    }

    public static string? Format(
        IReadOnlyList<ChatTimelineItem>? entries,
        IReadOnlyDictionary<string, ChatEntryMetadata>? metadata)
    {
        if (entries is null || metadata is null) return null;

        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            if (entry.Kind != ChatTimelineItemKind.Assistant)
                continue;

            if (!metadata.TryGetValue(entry.Id, out var meta))
                continue;

            var summary = Format(meta);
            if (!string.IsNullOrWhiteSpace(summary))
                return summary;
        }

        return null;
    }

    public static string? Format(ChatEntryMetadata? metadata)
        => metadata is null
            ? null
            : Format(metadata.InputTokens, metadata.OutputTokens, metadata.ResponseTokens, metadata.ContextPercent, metadata.ContextTokens ?? 0);

    public static string? Format(int? inputTokens, int? outputTokens, int? responseTokens, int? contextPercent)
        => Format(inputTokens, outputTokens, responseTokens, contextPercent, 0);

    public static string? Format(int? inputTokens, int? outputTokens, int? responseTokens, int? contextPercent, long fallbackContextTokens)
    {
        long? usedTokens = null;
        if (fallbackContextTokens > 0 && contextPercent is int pct)
            usedTokens = (long)Math.Round(fallbackContextTokens * Math.Clamp(pct, 0, 100) / 100d, MidpointRounding.AwayFromZero);
        usedTokens ??= responseTokens
            ?? (inputTokens is int input && outputTokens is int output ? input + output : null);

        return usedTokens is long used
            ? Format(used, fallbackContextTokens, contextPercent)
            : null;
    }

    public static string? Format(long usedTokens, long contextTokens, int? contextPercent = null)
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

        if (contextTokens > 0)
            return $"{FormatCount(usedTokens)}/{FormatCount(contextTokens)} ({percent ?? 0}%)";

        return percent is int pct
            ? $"{FormatCount(usedTokens)} ({pct}%)"
            : null;
    }

    private static string FormatCount(long value)
    {
        if (value >= 1_000_000)
        {
            var millions = value / 1_000_000d;
            return millions % 1d == 0d
                ? $"{millions:0}M"
                : $"{millions:0.#}M";
        }

        if (value >= 1_000)
            return $"{value / 1_000d:0.0}K";

        return value.ToString();
    }
}
