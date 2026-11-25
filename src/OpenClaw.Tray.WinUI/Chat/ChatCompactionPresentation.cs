using System.Globalization;
using OpenClaw.Chat;

namespace OpenClawTray.Chat;

internal sealed record ChatCompactionPresentation(
    string Title,
    string? Detail,
    string AutomationName);

internal static class ChatCompactionPresenter
{
    public static ChatCompactionPresentation? TryCreateForEntry(
        ChatTimelineItem entry,
        IReadOnlyDictionary<string, ChatEntryMetadata>? entryMetadata,
        string? title = null,
        string? metricsFormat = null)
    {
        if (entry.Kind != ChatTimelineItemKind.Status
            || entryMetadata?.TryGetValue(entry.Id, out var metadata) != true
            || metadata is null
            || !string.Equals(metadata.OpenClawKind, "compaction", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        title ??= "Context compacted";
        var detail = BuildDetail(metadata.CompactionTokensBefore, metadata.CompactionTokensAfter, metricsFormat);
        return new ChatCompactionPresentation(
            title,
            detail,
            detail is null ? title : $"{title}, {detail}");
    }

    private static string? BuildDetail(
        long? tokensBefore,
        long? tokensAfter,
        string? metricsFormat)
    {
        // Missing or inconsistent measurements cannot establish token savings.
        if (tokensBefore is not > 0 || tokensAfter is not >= 0 || tokensAfter > tokensBefore)
            return null;

        var saved = tokensBefore.Value - tokensAfter.Value;
        var percent = saved / (double)tokensBefore.Value * 100;
        return string.Format(
            CultureInfo.CurrentCulture,
            metricsFormat ?? "saved {0} tokens ({1}%)",
            Format(saved),
            percent.ToString("0.#", CultureInfo.CurrentCulture));
    }

    private static string Format(long value) =>
        value >= 1_000_000_000 ? (value / 1_000_000_000d).ToString("0.#", CultureInfo.CurrentCulture) + "B" :
        value >= 1_000_000 ? (value / 1_000_000d).ToString("0.#", CultureInfo.CurrentCulture) + "M" :
        value >= 1_000 ? (value / 1_000d).ToString("0.#", CultureInfo.CurrentCulture) + "k" :
        value.ToString(CultureInfo.CurrentCulture);
}
