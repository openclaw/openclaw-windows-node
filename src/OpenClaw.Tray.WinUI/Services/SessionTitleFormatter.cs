using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>
/// Builds stable, human-readable titles for gateway sessions.
/// </summary>
internal static class SessionTitleFormatter
{
    /// <summary>
    /// Formats a session title and qualifies non-canonical agent sessions with
    /// their agent and slot so identical gateway display names remain distinct.
    /// </summary>
    public static string Format(SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var baseName = !string.IsNullOrWhiteSpace(session.DisplayName)
            ? session.DisplayName!
            : (session.IsMain ? "OpenClaw Windows Tray" : session.ShortKey);

        // Keys follow agent:{agentId}:{sessionSlot}, for example
        // agent:main:main or agent:assistant:review.
        var parts = (session.Key ?? string.Empty).Split(':');
        if (parts.Length < 3 || !string.Equals(parts[0], "agent", StringComparison.Ordinal))
            return baseName;

        var agentId = parts[1];
        var sessionSlot = parts[2];

        if (agentId == "main" && sessionSlot == "main")
            return baseName;

        var qualifier = agentId == sessionSlot
            ? agentId
            : $"{agentId}/{sessionSlot}";

        return $"{baseName} ({qualifier})";
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
        if (session.IsMain)
            return true;

        var parts = (session.Key ?? string.Empty).Split(':');
        return parts.Length >= 3
            && string.Equals(parts[0], "agent", StringComparison.Ordinal)
            && string.Equals(parts[1], "main", StringComparison.Ordinal)
            && string.Equals(parts[2], "main", StringComparison.Ordinal);
    }
}
