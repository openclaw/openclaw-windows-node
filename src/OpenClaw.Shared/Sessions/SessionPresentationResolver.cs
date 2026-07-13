using System.Text.RegularExpressions;

namespace OpenClaw.Shared;

/// <summary>
/// Resolves the Gateway's presentation contract and provides a conservative
/// fallback for older gateways without treating opaque key tails as a schema.
/// </summary>
public static partial class SessionPresentationResolver
{
    private static readonly HashSet<string> BackgroundFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "acp", "cron", "dreaming", "harness", "heartbeat", "hook", "subagent", "system",
    };

    public static SessionPresentationInfo Resolve(SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var fallback = ResolveKey(session.Key, session.IsMain, session.Channel, session.Worktree);
        var gateway = session.Presentation;
        var label = UsefulTitle(session.Key, session.Label);
        var gatewayTitle = UsefulTitle(session.Key, gateway?.Title);
        var displayName = UsefulTitle(session.Key, SafeLegacyDisplayName(session.DisplayName));
        var derivedTitle = UsefulTitle(session.Key, session.DerivedTitle);
        var worktreeTitle = UsefulTitle(session.Key, FormatWorktree(session.Worktree));
        var title = label ?? gatewayTitle ?? displayName ?? derivedTitle ?? worktreeTitle ?? fallback.Title;
        var titleSource = label is not null ? "label"
            : gatewayTitle is not null ? NonEmpty(gateway?.TitleSource) ?? "generated"
            : displayName is not null ? "displayName"
            : derivedTitle is not null ? "derivedTitle"
            : worktreeTitle is not null ? "worktree"
            : fallback.TitleSource;
        var family = NonEmpty(gateway?.Family) ?? fallback.Family;
        var agentId = NonEmpty(gateway?.AgentId) ?? fallback.AgentId;
        var channel = NonEmpty(gateway?.Channel) ?? NonEmpty(session.Channel) ?? fallback.Channel;
        var accountId = NonEmpty(gateway?.AccountId) ?? fallback.AccountId;
        var peerKind = NonEmpty(gateway?.PeerKind) ?? fallback.PeerKind;
        var subtitle = NonEmpty(gateway?.Subtitle)
            ?? BuildSubtitle(channel, accountId, agentId, session.ExecNode, session.Worktree);

        return new SessionPresentationInfo
        {
            Title = title,
            TitleSource = titleSource,
            Subtitle = subtitle,
            Family = family,
            AgentId = agentId,
            Channel = channel,
            AccountId = accountId,
            PeerKind = peerKind,
            IsMain = session.IsMain,
            IsBackground = gateway?.IsBackground ?? BackgroundFamilies.Contains(family),
        };
    }

    public static bool IsBackground(SessionInfo session) => Resolve(session).IsBackground;

    public static bool IsVisible(SessionInfo session, bool showBackground) =>
        showBackground || !IsBackground(session);

    private static SessionPresentationInfo ResolveKey(
        string? rawKey,
        bool isMain,
        string? rowChannel,
        SessionWorktreeInfo? worktree)
    {
        var key = rawKey?.Trim() ?? string.Empty;
        if (key.Length == 0)
            return Presentation("Session", "unknown", isMain: isMain);
        if (key.Equals("global", StringComparison.OrdinalIgnoreCase))
            return Presentation("Global session", "global", agentId: isMain ? "main" : null, isMain: isMain);
        if (key.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return Presentation("Unknown session", "unknown");

        var (agentId, rest) = ParseAgentWrapper(key);
        if (isMain)
            return Presentation("Main session", "main", agentId: agentId, isMain: true);

        // Older gateways did not send the heartbeat base-session marker or a
        // presentation object. Detect the terminal suffix before parsing any
        // route so routed heartbeat keys do not become foreground chats.
        if (rest.StartsWith("tui-", StringComparison.OrdinalIgnoreCase)
            && rest.EndsWith(":heartbeat", StringComparison.OrdinalIgnoreCase))
            return Presentation("Heartbeat", "heartbeat", agentId, isBackground: true);

        var threadIndex = rest.LastIndexOf(":thread:", StringComparison.OrdinalIgnoreCase);
        var routeRest = threadIndex >= 0 ? rest[..threadIndex] : rest;
        var route = ParseRoute(routeRest, rowChannel);
        if (threadIndex >= 0)
        {
            return Presentation(
                route.Channel is { Length: > 0 } ? $"{ChannelLabel(route.Channel)} thread" : "Thread",
                "thread",
                agentId,
                route.Channel,
                route.AccountId,
                route.PeerKind);
        }
        if (route.Family is not null)
        {
            var noun = route.Family switch
            {
                "direct" => "direct message",
                "group" => "group",
                _ => "channel",
            };
            return Presentation(
                route.Channel is { Length: > 0 } ? $"{ChannelLabel(route.Channel)} {noun}" : Capitalize(noun),
                route.Family,
                agentId,
                route.Channel,
                route.AccountId,
                route.PeerKind);
        }

        if (rest.Equals("subagent", StringComparison.OrdinalIgnoreCase)
            || rest.StartsWith("subagent:", StringComparison.OrdinalIgnoreCase))
            return Presentation("Subagent", "subagent", agentId, isBackground: true);
        if (rest.Equals("acp", StringComparison.OrdinalIgnoreCase)
            || rest.StartsWith("acp:", StringComparison.OrdinalIgnoreCase))
            return Presentation("ACP session", "acp", agentId, isBackground: true);
        if (rest.Equals("cron", StringComparison.OrdinalIgnoreCase)
            || rest.StartsWith("cron:", StringComparison.OrdinalIgnoreCase))
            return Presentation("Scheduled task", "cron", agentId, isBackground: true);
        if (rest.StartsWith("dashboard:", StringComparison.OrdinalIgnoreCase))
            return Presentation(FormatWorktree(worktree) ?? "New session", "dashboard", agentId);
        if (rest.StartsWith("tui-", StringComparison.OrdinalIgnoreCase))
            return Presentation("Terminal session", "tui", agentId);
        if (rest.StartsWith("explicit:", StringComparison.OrdinalIgnoreCase))
            return Presentation(ReadableTail(rest["explicit:".Length..]), "explicit", agentId);
        if (rest.StartsWith("hook:", StringComparison.OrdinalIgnoreCase))
            return Presentation("Hook run", "hook", agentId, isBackground: true);
        if (rest.StartsWith("harness:", StringComparison.OrdinalIgnoreCase))
            return Presentation("Harness session", "harness", agentId, isBackground: true);
        if (rest.StartsWith("voice:", StringComparison.OrdinalIgnoreCase))
            return Presentation("Voice call", "voice", agentId);
        if (rest.StartsWith("dreaming-narrative-", StringComparison.OrdinalIgnoreCase))
            return Presentation("Dreaming", "dreaming", agentId, isBackground: true);
        if (rest.Equals("boot", StringComparison.OrdinalIgnoreCase)
            || rest.StartsWith("commitments:", StringComparison.OrdinalIgnoreCase)
            || rest.StartsWith("internal-session-effects:", StringComparison.OrdinalIgnoreCase))
            return Presentation("Background task", "system", agentId, isBackground: true);

        return Presentation(ReadableTail(rest), "custom", agentId);
    }

    private static (string? AgentId, string Tail) ParseAgentWrapper(string key)
    {
        var first = key.IndexOf(':');
        var second = first >= 0 ? key.IndexOf(':', first + 1) : -1;
        if (first <= 0 || second <= first + 1 || !key[..first].Equals("agent", StringComparison.OrdinalIgnoreCase))
            return (null, key);
        return (key[(first + 1)..second], key[(second + 1)..]);
    }

    private static (string? Family, string? Channel, string? AccountId, string? PeerKind) ParseRoute(
        string rest,
        string? rowChannel)
    {
        var parts = rest.Split(':');
        if (parts.Length >= 2 && IsDirect(parts[0]))
            return ("direct", NonEmpty(rowChannel), null, "direct");
        if (parts.Length < 3)
            return (null, null, null, null);

        var channel = NonEmpty(parts[0]);
        if (IsPeerKind(parts[1]))
            return (NormalizeFamily(parts[1]), channel, null, NormalizePeerKind(parts[1]));
        if (parts.Length >= 4 && IsPeerKind(parts[2]))
            return (NormalizeFamily(parts[2]), channel, NonEmpty(parts[1]), NormalizePeerKind(parts[2]));
        return (null, null, null, null);
    }

    private static bool IsDirect(string value) =>
        value.Equals("direct", StringComparison.OrdinalIgnoreCase)
        || value.Equals("dm", StringComparison.OrdinalIgnoreCase);

    private static bool IsPeerKind(string value) =>
        IsDirect(value)
        || value.Equals("group", StringComparison.OrdinalIgnoreCase)
        || value.Equals("channel", StringComparison.OrdinalIgnoreCase)
        || value.Equals("room", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeFamily(string value) =>
        value.Equals("room", StringComparison.OrdinalIgnoreCase) ? "group"
        : IsDirect(value) ? "direct"
        : value.ToLowerInvariant();

    private static string NormalizePeerKind(string value) => NormalizeFamily(value);

    private static SessionPresentationInfo Presentation(
        string title,
        string family,
        string? agentId = null,
        string? channel = null,
        string? accountId = null,
        string? peerKind = null,
        bool isMain = false,
        bool isBackground = false) => new()
        {
            Title = title,
            TitleSource = "generated",
            Family = family,
            AgentId = NonEmpty(agentId),
            Channel = NonEmpty(channel),
            AccountId = NonEmpty(accountId),
            PeerKind = NonEmpty(peerKind),
            IsMain = isMain,
            IsBackground = isBackground,
        };

    private static string? UsefulTitle(string key, string? value)
    {
        var normalized = NonEmpty(value);
        return normalized is not null && !normalized.Equals(key, StringComparison.Ordinal) ? normalized : null;
    }

    private static string? SafeLegacyDisplayName(string? value)
    {
        var normalized = NonEmpty(value);
        return normalized is null || OpaqueIdRegex().IsMatch(normalized) ? null : normalized;
    }

    private static string ReadableTail(string value)
    {
        var normalizedPath = value.Replace('\\', '/');
        var leaf = normalizedPath.Contains('/')
            ? normalizedPath.Split('/').LastOrDefault(part => part.Length > 0) ?? normalizedPath
            : normalizedPath;
        var shortened = OpaqueIdRegex().Replace(leaf, match => $"…{match.Value[^4..]}");
        const int maxLength = 32;
        if (shortened.Length > maxLength)
            shortened = $"{shortened[..(maxLength - 1)]}…";
        return NonEmpty(shortened) ?? "Session";
    }

    private static string? BuildSubtitle(
        string? channel,
        string? accountId,
        string? agentId,
        string? execNode,
        SessionWorktreeInfo? worktree)
    {
        var parts = new List<string>(4);
        var work = FormatWorktree(worktree);
        if (work is not null) parts.Add(work);
        if (NonEmpty(channel) is { } channelValue) parts.Add(ChannelLabel(channelValue));
        if (NonEmpty(accountId) is { } accountValue) parts.Add($"account {ReadableTail(accountValue)}");
        if (NonEmpty(agentId) is { } agentValue) parts.Add($"agent {ReadableTail(agentValue)}");
        if (NonEmpty(execNode) is { } nodeValue) parts.Add($"node {ReadableTail(nodeValue)}");
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    private static string? FormatWorktree(SessionWorktreeInfo? worktree)
    {
        if (worktree is null) return null;
        var repo = NonEmpty(worktree.RepoRoot)?.Split('/', '\\').LastOrDefault(part => part.Length > 0);
        var branch = NonEmpty(worktree.Branch);
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

    private static string ChannelLabel(string channel) => channel.ToLowerInvariant() switch
    {
        "imessage" => "iMessage",
        "whatsapp" => "WhatsApp",
        "sms" => "SMS",
        _ => Capitalize(channel),
    };

    private static string Capitalize(string value) =>
        value.Length > 0 ? char.ToUpperInvariant(value[0]) + value[1..] : value;

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[0-9a-f]{10,}", RegexOptions.IgnoreCase)]
    private static partial Regex OpaqueIdRegex();
}
