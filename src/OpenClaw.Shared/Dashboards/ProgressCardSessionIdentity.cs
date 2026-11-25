// Resolves which Gateway session a session:progress widget reads, and the
// canonical key the Gateway uses for that session's card and change events.
// Mirrors upstream Control UI (ui/src/lib/session-progress-cards.ts and
// ui/src/lib/sessions/session-key.ts) so aliases such as "main", mixed case,
// and bare keys match the Gateway's replies instead of failing the read.
//
// Usage:
//   var defaults = BoardSessionDefaults.FromHello(helloPayload);
//   var target = ProgressCardSessionIdentity.Resolve(new BoardSessionTarget("main", "ops"), defaults);
//   // target.SessionKey == "agent:ops:main", target.AgentId == null (the key owns its agent),
//   // target.WireKey == "agent:ops:main"; compare replies with SameSession(card.SessionKey, target.WireKey).
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared.Dashboards;

/// <summary>Session routing defaults from the Gateway hello (<c>snapshot.sessionDefaults</c>).</summary>
public sealed record BoardSessionDefaults(string? DefaultAgentId, string? MainKey, string? MainSessionKey)
{
    /// <summary>Reads <c>snapshot.sessionDefaults</c> from a hello-ok payload; null when absent.</summary>
    public static BoardSessionDefaults? FromHello(JsonElement hello)
    {
        if (hello.ValueKind != JsonValueKind.Object ||
            !hello.TryGetProperty("snapshot", out var snapshot) || snapshot.ValueKind != JsonValueKind.Object ||
            !snapshot.TryGetProperty("sessionDefaults", out var defaults) || defaults.ValueKind != JsonValueKind.Object)
            return null;
        return new(Read(defaults, "defaultAgentId"), Read(defaults, "mainKey"), Read(defaults, "mainSessionKey"));

        static string? Read(JsonElement parent, string property) =>
            parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}

/// <summary>
/// A resolved <c>progressCard.get</c> target. <see cref="AgentId"/> is sent only for
/// keys that do not name their agent; <see cref="WireKey"/> is the Gateway's scope key
/// for the card and its <c>progressCard.changed</c> events.
/// </summary>
public sealed record ProgressCardTarget(string SessionKey, string? AgentId, string WireKey);

/// <summary>Gateway session-key canonicalization used by progress cards.</summary>
public static partial class ProgressCardSessionIdentity
{
    private const string DefaultAgentId = "main";
    private const string DefaultMainKey = "main";
    private const string AgentPrefix = "agent:";

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidAgentIdPattern();

    [GeneratedRegex("[^a-z0-9_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidAgentIdCharacters();

    [GeneratedRegex("(?:^|:)(?:catalog|matrix|signal)(?=:|$)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CasePreservingChannel();

    /// <summary>
    /// Resolves a widget's session to the request and response identity. When the
    /// target names no agent, the hello's default agent is used so the expected key
    /// matches the Gateway's agent-scoped reply.
    /// </summary>
    public static ProgressCardTarget Resolve(BoardSessionTarget target, BoardSessionDefaults? defaults)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target.SessionKey);
        var agentId = !string.IsNullOrWhiteSpace(target.AgentId) ? NormalizeAgentId(target.AgentId)
            : !string.IsNullOrWhiteSpace(defaults?.DefaultAgentId) ? NormalizeAgentId(defaults.DefaultAgentId)
            : null;
        var key = target.SessionKey.Trim();
        var sentinel = key.ToLowerInvariant();
        var raw = sentinel is "global" or "unknown" ? sentinel : ScopedSessionArtifactKey(key, agentId);
        var (sessionKey, resolvedAgentId) = ResolveConversationIdentity(raw, agentId, defaults);
        var effectiveAgentId = resolvedAgentId ?? agentId;
        return new(
            sessionKey,
            TryParseAgentSessionKey(sessionKey, out _, out _) ? null : effectiveAgentId,
            ScopedSessionArtifactKey(sessionKey, effectiveAgentId));
    }

    /// <summary>True when both keys name the same session after Gateway comparison normalization.</summary>
    public static bool SameSession(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(NormalizeSessionKeyForComparison(left), NormalizeSessionKeyForComparison(right), StringComparison.Ordinal);

    /// <summary>Parses <c>agent:&lt;agentId&gt;:&lt;rest&gt;</c>; both parts are lowercased.</summary>
    public static bool TryParseAgentSessionKey(string? sessionKey, out string agentId, out string rest)
    {
        agentId = rest = "";
        var raw = (sessionKey ?? "").Trim().ToLowerInvariant();
        if (raw.Length == 0)
            return false;
        if (raw.StartsWith(':') || raw.EndsWith(':') || raw.Contains("::", StringComparison.Ordinal))
            raw = string.Join(':', raw.Split(':', StringSplitOptions.RemoveEmptyEntries));
        if (!raw.StartsWith(AgentPrefix, StringComparison.Ordinal))
            return false;
        var end = raw.IndexOf(':', AgentPrefix.Length);
        if (end < 0)
            return false;
        agentId = raw[AgentPrefix.Length..end].Trim();
        rest = raw[(end + 1)..];
        return agentId.Length > 0 && rest.Length > 0 && !rest.StartsWith(':');
    }

    /// <summary>Upstream <c>normalizeAgentId</c>: a lowercase id of at most 64 characters, or <c>main</c>.</summary>
    public static string NormalizeAgentId(string? value)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0)
            return DefaultAgentId;
        if (ValidAgentIdPattern().IsMatch(trimmed))
            return trimmed.ToLowerInvariant();
        var normalized = InvalidAgentIdCharacters().Replace(trimmed.ToLowerInvariant(), "-").Trim('-');
        if (normalized.Length > 64)
            normalized = normalized[..64];
        return normalized.Length == 0 ? DefaultAgentId : normalized;
    }

    /// <summary>
    /// Upstream <c>normalizeSessionKeyForComparison</c>: lowercases the key, except the
    /// opaque peer ids of catalog, Matrix room, and Signal group sessions.
    /// </summary>
    public static string NormalizeSessionKeyForComparison(string? value)
    {
        var raw = (value ?? "").Trim();
        if (raw.Length == 0)
            return "";
        if (!CasePreservingChannel().IsMatch(raw))
            return raw.ToLowerInvariant();

        var parts = raw.Split(':').ToList();
        var bodyStart = 0;
        while (parts.Count - bodyStart >= 3 && parts[bodyStart].Equals("agent", StringComparison.OrdinalIgnoreCase))
        {
            parts[bodyStart] = "agent";
            parts[bodyStart + 1] = parts[bodyStart + 1].ToLowerInvariant();
            bodyStart += 2;
        }
        while (bodyStart < parts.Count && string.IsNullOrWhiteSpace(parts[bodyStart]))
            bodyStart++;

        var channel = bodyStart < parts.Count ? parts[bodyStart].ToLowerInvariant() : null;
        if (channel == "catalog")
            return string.Join(':', parts);
        var peerKind = bodyStart + 1 < parts.Count ? parts[bodyStart + 1].ToLowerInvariant() : null;
        var matrixRoom = channel == "matrix" && peerKind is "channel" or "group";
        var signalGroup = channel == "signal" && peerKind == "group";
        if (!matrixRoom && !signalGroup)
            return raw.ToLowerInvariant();

        parts[bodyStart] = channel!;
        parts[bodyStart + 1] = peerKind!;
        if (matrixRoom)
        {
            for (var index = parts.Count - 2; index >= bodyStart + 2; index--)
            {
                if (parts[index].Equals("thread", StringComparison.OrdinalIgnoreCase))
                {
                    parts[index] = "thread";
                    break;
                }
            }
        }
        else
        {
            if (bodyStart + 2 < parts.Count)
                parts[bodyStart + 2] = parts[bodyStart + 2].Trim();
            else
                parts.Add("");
            for (var index = bodyStart + 3; index < parts.Count; index++)
                parts[index] = parts[index].ToLowerInvariant();
        }
        return string.Join(':', parts);
    }

    /// <summary>Upstream <c>scopedSessionArtifactKey</c>: prefixes a bare key with its agent.</summary>
    public static string ScopedSessionArtifactKey(string sessionKey, string? agentId)
    {
        var key = sessionKey.Trim();
        if (key.Length == 0 || string.IsNullOrWhiteSpace(agentId) || TryParseAgentSessionKey(key, out _, out _))
            return key;
        return $"{AgentPrefix}{NormalizeAgentId(agentId)}:{key}";
    }

    /// <summary>Upstream <c>resolveUiConversationIdentity</c> with the hello as the only defaults source.</summary>
    private static (string SessionKey, string? AgentId) ResolveConversationIdentity(
        string sessionKey,
        string? agentIdOverride,
        BoardSessionDefaults? defaults)
    {
        var raw = sessionKey.Trim();
        var parsed = TryParseAgentSessionKey(raw, out var parsedAgentId, out var parsedRest);
        var knownDefaults = defaults is not null;
        var mainKey = NormalizeMainKey(defaults?.MainKey);
        var mainCandidate = parsed ? parsedRest : raw.ToLowerInvariant();
        var isMain = mainCandidate == DefaultMainKey || knownDefaults && mainCandidate == mainKey;
        if (IsGlobal(raw))
        {
            var globalAgentId = !string.IsNullOrWhiteSpace(agentIdOverride) ? NormalizeAgentId(agentIdOverride)
                : !string.IsNullOrWhiteSpace(defaults?.DefaultAgentId) ? NormalizeAgentId(defaults.DefaultAgentId)
                : null;
            return ("global", globalAgentId);
        }
        if (!parsed && (!isMain || !knownDefaults))
            return (raw, null);

        var defaultAgentId = NormalizeAgentId(defaults?.DefaultAgentId);
        var agentId = NormalizeAgentId(parsed ? parsedAgentId
            : !string.IsNullOrWhiteSpace(agentIdOverride) ? agentIdOverride.Trim()
            : defaultAgentId);
        var canonicalKey = NormalizeSessionKeyForComparison(raw);
        if (isMain && knownDefaults)
        {
            var advertised = defaults!.MainSessionKey?.Trim();
            var advertisedApplies = !string.IsNullOrEmpty(advertised) &&
                agentId == defaultAgentId &&
                TryParseAgentSessionKey(advertised, out var advertisedAgentId, out _) &&
                agentId == advertisedAgentId;
            canonicalKey = IsGlobal(defaults.MainSessionKey) ? "global"
                : advertisedApplies ? NormalizeSessionKeyForComparison(advertised)
                : $"{AgentPrefix}{agentId}:{mainKey}";
        }
        return (canonicalKey, agentId);
    }

    private static string NormalizeMainKey(string? value)
    {
        var normalized = (value ?? "").Trim().ToLowerInvariant();
        return normalized.Length == 0 ? DefaultMainKey : normalized;
    }

    private static bool IsGlobal(string? sessionKey) =>
        string.Equals((sessionKey ?? "").Trim(), "global", StringComparison.OrdinalIgnoreCase);
}
