// Searches the already-authorized display projection, never raw Gateway payloads.
// Call Match after reasoning/tool visibility projection; the snippet is literal text.
using OpenClaw.Chat;

namespace OpenClawTray.Chat;

internal static class ChatTranscriptSearch
{
    private const int SnippetContext = 80;

    internal static string? Match(ChatTimelineItem entry, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;
        var text = entry.ToolName == "sessions_yield"
            ? entry.ToolOutput ?? ""
            : string.Join("\n", new[]
            {
                entry.Text, entry.ToolName, entry.ToolInputDetails?.Command,
                entry.ToolInputDetails?.Text,
                entry.ToolInputDetails is null ? entry.ToolArgs?.ToJsonString() : null,
                entry.ToolOutput,
            });
        var index = text.IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return null;
        var start = Math.Max(0, index - SnippetContext);
        var end = Math.Min(text.Length, index + query.Trim().Length + SnippetContext);
        return (start > 0 ? "..." : "") + text[start..end] + (end < text.Length ? "..." : "");
    }
}
