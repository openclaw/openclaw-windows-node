using System.Globalization;

namespace OpenClawTray.Chat;

internal sealed record ChatCompactionPresentation(
    string Title,
    string Detail,
    string AutomationName);

internal static class ChatCompactionPresenter
{
    public static ChatCompactionPresentation Create(
        long? tokensBefore,
        long? tokensAfter,
        string? title = null,
        string? metricsFormat = null,
        string? fallbackDetail = null)
    {
        title ??= "Context compacted";
        var detail = BuildDetail(tokensBefore, tokensAfter, metricsFormat, fallbackDetail);
        return new ChatCompactionPresentation(title, detail, $"{title}. {detail}");
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
