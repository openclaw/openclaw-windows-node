using System.Text.RegularExpressions;

namespace OpenClaw.Shared;

/// <summary>Builds Windows-local display text from flat Gateway session facts.</summary>
public static partial class SessionDisplayResolver
{
    private static readonly HashSet<string> BackgroundClassifications = new(StringComparer.OrdinalIgnoreCase)
    {
        "acp", "cron", "dreaming", "harness", "heartbeat", "hook", "subagent", "system",
    };

    public static SessionDisplayInfo Resolve(SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var fallback = ResolveKey(session.Key, session.IsMain, session.Channel, session.Worktree);
        var gatewayClassification = NonEmpty(session.Classification);
        var classification = gatewayClassification ?? fallback.Classification;
        var agentId = NonEmpty(session.AgentId) ?? fallback.AgentId;
        var channel = NonEmpty(session.Channel) ?? fallback.Channel;
        var accountId = NonEmpty(session.AccountId) ?? fallback.AccountId;
        var peerKind = NonEmpty(session.PeerKind) ?? fallback.PeerKind;
        var isDirect = classification.Equals("direct", StringComparison.OrdinalIgnoreCase)
            || peerKind?.Equals("direct", StringComparison.OrdinalIgnoreCase) == true;
        var displayName = isDirect
            ? null
            : UsefulTitle(session.Key, SafeLegacyDisplayName(session.DisplayName));
        var title = UsefulTitle(session.Key, session.Label)
            ?? displayName
            ?? UsefulTitle(session.Key, session.DerivedTitle)
            ?? (gatewayClassification is null
                ? fallback.Title
                : TitleForClassification(classification, channel, session.Worktree, fallback.Title));

        return new SessionDisplayInfo
        {
            Title = title,
            TitleSource = UsefulTitle(session.Key, session.Label) is not null ? "label"
                : displayName is not null ? "displayName"
                : UsefulTitle(session.Key, session.DerivedTitle) is not null ? "derivedTitle"
                : "generated",
            Subtitle = BuildSubtitle(channel, accountId, agentId, session.ExecNode, session.Worktree),
            Classification = classification,
            AgentId = agentId,
            Channel = channel,
            AccountId = accountId,
            PeerKind = peerKind,
            IsMain = session.IsMain,
            IsBackground = session.IsBackground ?? BackgroundClassifications.Contains(classification),
        };
    }

    public static bool IsBackground(SessionInfo session) => Resolve(session).IsBackground;

    public static bool IsVisible(SessionInfo session, bool showBackground) =>
        showBackground || !IsBackground(session);

    /// <summary>Produces a bounded context label while masking opaque identifiers.</summary>
    public static string FormatContext(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return ReadableTail(value);
    }

    private static SessionDisplayInfo ResolveKey(
        string? rawKey,
        bool isMain,
        string? rowChannel,
        SessionWorktreeInfo? worktree)
    {
        var key = rawKey?.Trim() ?? string.Empty;
        if (key.Length == 0)
            return isMain ? Display("Main session", "main", agentId: "main", isMain: true) : Display("Session", "unknown");
        if (key.Equals("global", StringComparison.OrdinalIgnoreCase))
            return Display("Global session", "global", agentId: isMain ? "main" : null, isMain: isMain);
        if (key.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return Display("Unknown session", "unknown");

        var (agentId, rest) = ParseAgentWrapper(key);
        if (isMain) return Display("Main session", "main", agentId: agentId, isMain: true);
        if (rest.StartsWith("tui-", StringComparison.OrdinalIgnoreCase) && rest.EndsWith(":heartbeat", StringComparison.OrdinalIgnoreCase))
            return Display("Heartbeat", "heartbeat", agentId, isBackground: true);
        if (rest.Equals("subagent", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("subagent:", StringComparison.OrdinalIgnoreCase))
            return Display("Subagent", "subagent", agentId, isBackground: true);
        if (rest.Equals("acp", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("acp:", StringComparison.OrdinalIgnoreCase))
            return Display("ACP session", "acp", agentId, isBackground: true);
        if (rest.Equals("cron", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("cron:", StringComparison.OrdinalIgnoreCase))
            return Display("Scheduled task", "cron", agentId, isBackground: true);
        if (rest.StartsWith("dashboard:", StringComparison.OrdinalIgnoreCase))
            return Display(FormatWorktree(worktree) ?? "New session", "dashboard", agentId);
        if (rest.StartsWith("tui-", StringComparison.OrdinalIgnoreCase)) return Display("Terminal session", "tui", agentId);
        if (rest.StartsWith("explicit:", StringComparison.OrdinalIgnoreCase)) return Display(ReadableTail(rest["explicit:".Length..]), "explicit", agentId);
        if (rest.StartsWith("hook:", StringComparison.OrdinalIgnoreCase)) return Display("Hook run", "hook", agentId, isBackground: true);
        if (rest.StartsWith("harness:", StringComparison.OrdinalIgnoreCase)) return Display("Harness session", "harness", agentId, isBackground: true);
        if (rest.StartsWith("voice:", StringComparison.OrdinalIgnoreCase)) return Display("Voice call", "voice", agentId);
        if (rest.StartsWith("dreaming-narrative-", StringComparison.OrdinalIgnoreCase)) return Display("Dreaming", "dreaming", agentId, isBackground: true);
        if (rest.Equals("boot", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("commitments:", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("internal-session-effects:", StringComparison.OrdinalIgnoreCase))
            return Display("Background task", "system", agentId, isBackground: true);

        var threadIndex = rest.LastIndexOf(":thread:", StringComparison.OrdinalIgnoreCase);
        var route = ParseRoute(threadIndex >= 0 ? rest[..threadIndex] : rest, rowChannel);
        if (threadIndex >= 0)
            return Display(route.Channel is { Length: > 0 } ? $"{ChannelLabel(route.Channel)} thread" : "Thread", "thread", agentId, route.Channel, route.AccountId, route.PeerKind);
        if (route.Classification is not null)
        {
            var noun = route.Classification switch { "direct" => "direct message", "group" => "group", _ => "channel" };
            return Display(route.Channel is { Length: > 0 } ? $"{ChannelLabel(route.Channel)} {noun}" : Capitalize(noun), route.Classification, agentId, route.Channel, route.AccountId, route.PeerKind);
        }
        return Display(ReadableTail(rest), "custom", agentId);
    }

    private static string TitleForClassification(
        string classification,
        string? channel,
        SessionWorktreeInfo? worktree,
        string fallback)
    {
        var channelTitle = channel is { Length: > 0 } ? ChannelLabel(channel) : null;
        return classification.ToLowerInvariant() switch
        {
            "main" => "Main session",
            "global" => "Global session",
            "unknown" => "Unknown session",
            "direct" => channelTitle is null ? "Direct message" : $"{channelTitle} direct message",
            "group" => channelTitle is null ? "Group conversation" : $"{channelTitle} group",
            "channel" => channelTitle is null ? "Channel conversation" : $"{channelTitle} channel",
            "thread" => channelTitle is null ? "Thread" : $"{channelTitle} thread",
            "cron" => "Scheduled task",
            "heartbeat" => "Heartbeat",
            "subagent" => "Subagent",
            "acp" => "ACP session",
            "dashboard" => FormatWorktree(worktree) ?? "New session",
            "tui" => "Terminal session",
            "hook" => "Hook run",
            "harness" => "Harness session",
            "voice" => "Voice call",
            "dreaming" => "Dreaming",
            "system" => "Background task",
            _ => fallback,
        };
    }

    private static (string? AgentId, string Tail) ParseAgentWrapper(string key)
    {
        var first = key.IndexOf(':');
        var second = first >= 0 ? key.IndexOf(':', first + 1) : -1;
        return first <= 0 || second <= first + 1 || !key[..first].Equals("agent", StringComparison.OrdinalIgnoreCase)
            ? (null, key) : (key[(first + 1)..second], key[(second + 1)..]);
    }

    private static (string? Classification, string? Channel, string? AccountId, string? PeerKind) ParseRoute(string rest, string? rowChannel)
    {
        var parts = rest.Split(':');
        if (parts.Length >= 2 && IsDirect(parts[0])) return ("direct", NonEmpty(rowChannel), null, "direct");
        if (parts.Length < 3) return (null, null, null, null);
        var channel = NonEmpty(parts[0]);
        if (IsPeerKind(parts[1])) return (NormalizeClassification(parts[1]), channel, null, NormalizePeerKind(parts[1]));
        if (parts.Length >= 4 && IsPeerKind(parts[2])) return (NormalizeClassification(parts[2]), channel, NonEmpty(parts[1]), NormalizePeerKind(parts[2]));
        return (null, null, null, null);
    }

    private static bool IsDirect(string value) => value.Equals("direct", StringComparison.OrdinalIgnoreCase) || value.Equals("dm", StringComparison.OrdinalIgnoreCase);
    private static bool IsPeerKind(string value) => IsDirect(value) || value.Equals("group", StringComparison.OrdinalIgnoreCase) || value.Equals("channel", StringComparison.OrdinalIgnoreCase) || value.Equals("room", StringComparison.OrdinalIgnoreCase);
    private static string NormalizeClassification(string value) => value.Equals("room", StringComparison.OrdinalIgnoreCase) ? "group" : IsDirect(value) ? "direct" : value.ToLowerInvariant();
    private static string NormalizePeerKind(string value) => NormalizeClassification(value);

    private static SessionDisplayInfo Display(string title, string classification, string? agentId = null, string? channel = null, string? accountId = null, string? peerKind = null, bool isMain = false, bool isBackground = false) => new()
    {
        Title = title, TitleSource = "generated", Classification = classification, AgentId = NonEmpty(agentId), Channel = NonEmpty(channel),
        AccountId = NonEmpty(accountId), PeerKind = NonEmpty(peerKind), IsMain = isMain, IsBackground = isBackground,
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
        var leaf = normalizedPath.Contains('/') ? normalizedPath.Split('/').LastOrDefault(part => part.Length > 0) ?? normalizedPath : normalizedPath;
        var shortened = OpaqueIdRegex().Replace(leaf, match => $"…{match.Value[^4..]}");
        return NonEmpty(shortened.Length > 32 ? $"{shortened[..31]}…" : shortened) ?? "Session";
    }

    private static string? BuildSubtitle(string? channel, string? accountId, string? agentId, string? execNode, SessionWorktreeInfo? worktree)
    {
        var parts = new List<string>(4);
        if (FormatWorktree(worktree) is { } work) parts.Add(work);
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
        if (branch?.StartsWith("openclaw/", StringComparison.Ordinal) == true) branch = branch["openclaw/".Length..];
        return (repo, branch) switch { ({ Length: > 0 }, { Length: > 0 }) => $"{repo} ⎇ {branch}", ({ Length: > 0 }, _) => repo, (_, { Length: > 0 }) => branch, _ => null };
    }

    private static string ChannelLabel(string channel) => channel.ToLowerInvariant() switch { "imessage" => "iMessage", "whatsapp" => "WhatsApp", "sms" => "SMS", _ => Capitalize(channel) };
    private static string Capitalize(string value) => value.Length > 0 ? char.ToUpperInvariant(value[0]) + value[1..] : value;
    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[0-9a-f]{10,}", RegexOptions.IgnoreCase)]
    private static partial Regex OpaqueIdRegex();
}

public sealed class SessionDisplayInfo
{
    public string Title { get; init; } = "";
    public string TitleSource { get; init; } = "generated";
    public string? Subtitle { get; init; }
    public string Classification { get; init; } = "custom";
    public string? AgentId { get; init; }
    public string? Channel { get; init; }
    public string? AccountId { get; init; }
    public string? PeerKind { get; init; }
    public bool IsMain { get; init; }
    public bool IsBackground { get; init; }
}
