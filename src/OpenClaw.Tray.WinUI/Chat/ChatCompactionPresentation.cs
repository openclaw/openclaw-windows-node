using System.Globalization;
using OpenClaw.Chat;

namespace OpenClawTray.Chat;

internal sealed record ChatCompactionPresentation(
    string Title,
    string Detail,
    string ActionLabel,
    string AutomationName);

internal static class ChatCompactionPresenter
{
    public static ChatCompactionPresentation? TryCreateForEntry(
        ChatTimelineItem entry,
        IReadOnlyDictionary<string, ChatEntryMetadata>? entryMetadata,
        string? title = null,
        string? detail = null,
        string? actionLabel = null)
    {
        if (entry.Kind != ChatTimelineItemKind.Status
            || entryMetadata?.TryGetValue(entry.Id, out var metadata) != true
            || metadata is null
            || !string.Equals(metadata.OpenClawKind, "compaction", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        title ??= "COMPACTED HISTORY";
        detail ??= "The compacted transcript is preserved as a checkpoint. " +
            "Open session checkpoints to branch or restore from that compacted view.";
        actionLabel ??= "Open checkpoints";
        return new ChatCompactionPresentation(
            title,
            detail,
            actionLabel,
            $"{title}. {detail}");
    }

    public static ChatCompactionPresentation Create(
        long? tokensBefore,
        long? tokensAfter,
        string? title = null,
        string? metricsFormat = null,
        string? fallbackDetail = null,
        string? actionLabel = null)
    {
        title ??= "Context compacted";
        var detail = BuildDetail(tokensBefore, tokensAfter, metricsFormat, fallbackDetail);
        actionLabel ??= "Open checkpoints";
        return new ChatCompactionPresentation(title, detail, actionLabel, $"{title}. {detail}");
    }

    private static string BuildDetail(
        long? tokensBefore,
        long? tokensAfter,
        string? metricsFormat,
        string? fallbackDetail)
    {
        if (tokensBefore is >= 0 && tokensAfter is >= 0)
        {
            var saved = Math.Max(0, tokensBefore.Value - tokensAfter.Value);
            return string.Format(
                CultureInfo.CurrentCulture,
                metricsFormat ?? "{0} → {1} tokens · {2} saved",
                Format(tokensBefore.Value),
                Format(tokensAfter.Value),
                Format(saved));
        }

        return fallbackDetail ?? "Earlier context was summarized into a checkpoint.";
    }

    private static string Format(long value) =>
        value.ToString("N0", CultureInfo.CurrentCulture);
}
