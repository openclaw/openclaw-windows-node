using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>
/// Builds stable, human-readable titles for gateway sessions.
/// </summary>
internal static class SessionTitleFormatter
{
    private static Func<string, string> s_getLocalizedString = key => key;

    internal static void ConfigureLocalization(Func<string, string> getLocalizedString)
    {
        s_getLocalizedString = getLocalizedString ?? throw new ArgumentNullException(nameof(getLocalizedString));
    }

    /// <summary>
    /// Formats a session title from the Gateway presentation contract, with a
    /// bounded fallback for older gateways.
    /// </summary>
    public static string Format(SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var presentation = SessionPresentationResolver.Resolve(session);
        if (!presentation.TitleSource.Equals("generated", StringComparison.OrdinalIgnoreCase))
            return presentation.Title;

        var family = presentation.Family.ToLowerInvariant();
        var hasChannel = !string.IsNullOrWhiteSpace(presentation.Channel);
        var resourceKey = family switch
        {
            "main" => "SessionTitle_Main",
            "global" => "SessionTitle_Global",
            "unknown" => "SessionTitle_Unknown",
            "direct" => hasChannel ? "SessionTitle_Direct" : "SessionTitle_DirectGeneric",
            "group" => hasChannel ? "SessionTitle_Group" : "SessionTitle_GroupGeneric",
            "channel" => hasChannel ? "SessionTitle_Channel" : "SessionTitle_ChannelGeneric",
            "thread" => hasChannel ? "SessionTitle_Thread" : "SessionTitle_ThreadGeneric",
            "cron" => "SessionTitle_Cron",
            "heartbeat" => "SessionTitle_Heartbeat",
            "subagent" => "SessionTitle_Subagent",
            "acp" => "SessionTitle_Acp",
            "dashboard" => "SessionTitle_Dashboard",
            "tui" => "SessionTitle_Tui",
            "hook" => "SessionTitle_Hook",
            "harness" => "SessionTitle_Harness",
            "voice" => "SessionTitle_Voice",
            "dreaming" => "SessionTitle_Dreaming",
            "system" => "SessionTitle_System",
            _ => null,
        };
        if (resourceKey is null)
            return presentation.Title;

        return hasChannel && (family is "direct" or "group" or "channel" or "thread")
            ? LocalizedFormat(resourceKey, presentation.Title, ChannelLabel(presentation.Channel))
            : Localized(resourceKey, presentation.Title);
    }

    public static string? FormatSubtitle(SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var presentation = SessionPresentationResolver.Resolve(session);
        var parts = new List<string>(5);
        if (FormatWorktree(session.Worktree) is { } worktree) parts.Add(worktree);
        if (!string.IsNullOrWhiteSpace(presentation.Channel)) parts.Add(ChannelLabel(presentation.Channel));
        if (!string.IsNullOrWhiteSpace(presentation.AccountId))
            parts.Add(LocalizedFormat("SessionSubtitle_Account", $"account {ShortContext(presentation.AccountId)}", ShortContext(presentation.AccountId)));
        if (!string.IsNullOrWhiteSpace(presentation.AgentId))
            parts.Add(LocalizedFormat("SessionSubtitle_Agent", $"agent {ShortContext(presentation.AgentId)}", ShortContext(presentation.AgentId)));
        if (!string.IsNullOrWhiteSpace(session.ExecNode))
            parts.Add(LocalizedFormat("SessionSubtitle_Node", $"node {ShortContext(session.ExecNode)}", ShortContext(session.ExecNode)));
        return parts.Count > 0 ? string.Join(" · ", parts) : presentation.Subtitle;
    }

    /// <summary>
    /// Formats a collection of sessions and adds stable numeric suffixes only
    /// when the normal key qualifier still leaves duplicate visible titles.
    /// The returned titles are aligned with the input collection.
    /// </summary>
    public static IReadOnlyList<string> FormatUnique(IReadOnlyList<SessionInfo> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        if (sessions.Count == 0)
            return Array.Empty<string>();

        var baseTitles = new string[sessions.Count];
        for (var i = 0; i < sessions.Count; i++)
            baseTitles[i] = Format(sessions[i]);

        // Reserve every natural title before adding counters so a generated
        // title such as "Research (2)" cannot collide with another row whose
        // actual display name is already "Research (2)".
        var reservedTitles = new HashSet<string>(baseTitles, StringComparer.OrdinalIgnoreCase);
        var assignedTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new string[sessions.Count];

        foreach (var group in Enumerable.Range(0, sessions.Count)
                     .GroupBy(index => baseTitles[index], StringComparer.OrdinalIgnoreCase))
        {
            var orderedIndices = group
                .OrderByDescending(index => IsCanonicalMain(sessions[index]))
                .ThenBy(index => sessions[index].Key ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(index => index)
                .ToArray();

            var primaryTitle = baseTitles[orderedIndices[0]];
            results[orderedIndices[0]] = primaryTitle;
            assignedTitles.Add(primaryTitle);

            var suffix = 2;
            for (var i = 1; i < orderedIndices.Length; i++)
            {
                string candidate;
                do
                {
                    candidate = $"{primaryTitle} ({suffix})";
                    suffix++;
                }
                while (reservedTitles.Contains(candidate) || !assignedTitles.Add(candidate));

                results[orderedIndices[i]] = candidate;
            }
        }

        return results;
    }

    /// <summary>
    /// Formats one session in the context of its active peers.
    /// </summary>
    public static string Format(SessionInfo session, IReadOnlyList<SessionInfo> sessions)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sessions);

        var index = -1;
        for (var i = 0; i < sessions.Count; i++)
        {
            if (ReferenceEquals(session, sessions[i]))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            for (var i = 0; i < sessions.Count; i++)
            {
                if (string.Equals(session.Key, sessions[i].Key, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }
        }

        return index >= 0 ? FormatUnique(sessions)[index] : Format(session);
    }

    private static bool IsCanonicalMain(SessionInfo session)
    {
        return SessionPresentationResolver.Resolve(session).IsMain;
    }

    private static string Localized(string key, string fallback)
    {
        var value = s_getLocalizedString(key);
        return value == key ? fallback : value;
    }

    private static string LocalizedFormat(string key, string fallback, object value)
    {
        var format = s_getLocalizedString(key);
        if (format == key) return fallback;
        try
        {
            return string.Format(format, value);
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    private static string ChannelLabel(string? channel) => channel?.ToLowerInvariant() switch
    {
        "imessage" => "iMessage",
        "whatsapp" => "WhatsApp",
        "sms" => "SMS",
        { Length: > 0 } value => char.ToUpperInvariant(value[0]) + value[1..],
        _ => "Channel",
    };

    private static string ShortContext(string value) => value.Length > 12 ? $"…{value[^4..]}" : value;

    private static string? FormatWorktree(SessionWorktreeInfo? worktree)
    {
        if (worktree is null) return null;
        var repo = worktree.RepoRoot?.Replace('\\', '/').Split('/').LastOrDefault(part => part.Length > 0);
        var branch = worktree.Branch;
        if (branch?.StartsWith("openclaw/", StringComparison.Ordinal) == true)
            branch = branch["openclaw/".Length..];
        return (repo, branch) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{repo} ⎇ {branch}",
            ({ Length: > 0 }, _) => repo,
            (_, { Length: > 0 }) => branch,
            _ => null,
        };
    }
}
