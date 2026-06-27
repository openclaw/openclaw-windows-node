using System;
using System.Text;

namespace OpenClaw.Shared.Sessions;

/// <summary>
/// Renders a <see cref="ChatHistoryInfo"/> transcript to plain text for the
/// "Export transcript" action, and suggests a filename. Pure and testable.
/// </summary>
public static class SessionTranscriptFormatter
{
    /// <summary>
    /// Formats the transcript as a readable plain-text document with a header
    /// and one block per message (role, local timestamp, text).
    /// </summary>
    public static string Format(ChatHistoryInfo history)
    {
        if (history is null) throw new ArgumentNullException(nameof(history));

        var nl = Environment.NewLine;
        var sb = new StringBuilder();
        sb.Append("OpenClaw session transcript").Append(nl);
        sb.Append("Session: ").Append(string.IsNullOrWhiteSpace(history.SessionKey) ? "(unknown)" : history.SessionKey).Append(nl);
        if (!string.IsNullOrWhiteSpace(history.SessionId))
            sb.Append("Session ID: ").Append(history.SessionId).Append(nl);
        sb.Append("Exported: ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(nl);
        sb.Append("Messages: ").Append(history.Messages.Count).Append(nl);
        sb.Append(new string('-', 40)).Append(nl);

        foreach (var m in history.Messages)
        {
            var role = string.IsNullOrWhiteSpace(m.Role) ? "?" : m.Role;
            sb.Append(nl).Append('[').Append(role);
            if (m.Ts > 0)
            {
                var ts = DateTimeOffset.FromUnixTimeMilliseconds(m.Ts).LocalDateTime;
                sb.Append(" \u00B7 ").Append(ts.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            sb.Append(']').Append(nl);
            sb.Append(m.Text ?? string.Empty).Append(nl);
        }

        return sb.ToString();
    }

    /// <summary>
    /// A filesystem-safe suggested filename for the export, derived from the
    /// session key and current date.
    /// </summary>
    public static string SuggestFileName(string? sessionKey)
    {
        var slug = Slugify(sessionKey);
        return $"openclaw-transcript-{slug}-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
    }

    private static string Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "session";
        var sb = new StringBuilder(value!.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (ch is '-' or '_') sb.Append(ch);
            else sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length == 0) return "session";
        return slug.Length > 48 ? slug[..48].Trim('-') : slug;
    }
}
