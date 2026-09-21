using System.Collections.Immutable;
using System.Text.Json;

namespace OpenClaw.Shared;

public sealed record ThinkingLevelOption(string Id, string Label);

/// <summary>Null levels mean unknown; an empty array is an advertised empty profile.</summary>
public sealed record ThinkingProfile(
    ImmutableArray<ThinkingLevelOption>? Levels = null,
    string? Default = null)
{
    // ImmutableArray's backing-array identity is not a change in advertised metadata.
    public bool Equals(ThinkingProfile? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null || Default != other.Default)
            return false;
        if (Levels is not { } levels)
            return other.Levels is null;
        return other.Levels is { } otherLevels
            && levels.IsDefault == otherLevels.IsDefault
            && (levels.IsDefault || levels.AsSpan().SequenceEqual(otherLevels.AsSpan()));
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Default);
        hash.Add(Levels.HasValue);
        if (Levels is { } levels)
        {
            hash.Add(levels.IsDefault);
            if (!levels.IsDefault)
                foreach (var level in levels)
                    hash.Add(level);
        }
        return hash.ToHashCode();
    }
}

public sealed record ThinkingIdentity(
    string? Provider = null, string? Model = null, string? RuntimeId = null)
{
    public bool IsCompatibleWith(ThinkingIdentity other) =>
        Compatible(Provider, other.Provider)
        && Compatible(Model, other.Model)
        && Compatible(RuntimeId?.Trim(), other.RuntimeId?.Trim());

    private static bool Compatible(string? left, string? right) =>
        string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right) || left == right;
}

/// <summary>
/// Keeps the current wire identity separate from a retained profile's provenance.
/// Partial rows must not fabricate a catalog identity from cached fields.
/// </summary>
public sealed record ThinkingContext(ThinkingIdentity Identity, ThinkingProfile? Profile = null)
{
    internal ThinkingIdentity ProfileIdentity { get; init; } = Identity;
    internal string? ProfileSessionId { get; init; }
    internal string? ProfileAgentId { get; init; }
}

internal static class ThinkingMetadata
{
    internal static ThinkingContext Read(JsonElement item, bool modelEntry = false)
    {
        var identity = new ThinkingIdentity(
            Text(item, modelEntry ? "provider" : "modelProvider") ?? Text(item, "provider"),
            Text(item, modelEntry ? "id" : "model"),
            item.TryGetProperty("agentRuntime", out var runtime)
                && runtime.ValueKind == JsonValueKind.Object ? Text(runtime, "id") : null);
        ImmutableArray<ThinkingLevelOption>? levels = null;
        if (item.TryGetProperty("thinkingLevels", out var structured)
            && structured.ValueKind == JsonValueKind.Array)
        {
            levels = structured.EnumerateArray().Select(option =>
                new ThinkingLevelOption(
                    option.GetProperty("id").GetString() ?? throw new JsonException("Missing thinking option id."),
                    option.GetProperty("label").GetString() ?? throw new JsonException("Missing thinking option label."))).ToImmutableArray();
        }
        else if (!modelEntry && item.TryGetProperty("thinkingOptions", out var legacy)
            && legacy.ValueKind == JsonValueKind.Array)
        {
            levels = legacy.EnumerateArray().Select(option =>
            {
                var label = option.GetString() ?? throw new JsonException("Missing legacy thinking option.");
                return new ThinkingLevelOption(NormalizeLegacy(label), label);
            }).ToImmutableArray();
        }
        var defaultLevel = Text(item, "thinkingDefault");
        return new(identity, levels is not null || defaultLevel is not null
            ? new ThinkingProfile(levels, defaultLevel) : null)
        {
            ProfileSessionId = Text(item, "sessionId"),
            ProfileAgentId = Text(item, "agentId"),
        };
    }

    internal static ThinkingContext MergeSession(
        JsonElement item, ThinkingContext? previous)
    {
        var incoming = Read(item);
        if (previous is null
            || ChangesScope(item, "sessionId", previous.ProfileSessionId)
            || ChangesScope(item, "agentId", previous.ProfileAgentId)
            || !incoming.Identity.IsCompatibleWith(previous.ProfileIdentity)
            || ClearsIdentity(item, previous.ProfileIdentity))
            return incoming;

        var profile = incoming.Profile;
        if (profile?.Levels is null && previous.Profile?.Levels is { } knownLevels)
            profile = new(knownLevels, profile?.Default ?? previous.Profile.Default);

        return incoming with
        {
            Profile = profile,
            ProfileIdentity = new(
                incoming.Identity.Provider ?? previous.ProfileIdentity.Provider,
                incoming.Identity.Model ?? previous.ProfileIdentity.Model,
                incoming.Identity.RuntimeId ?? previous.ProfileIdentity.RuntimeId),
            ProfileSessionId = previous.ProfileSessionId,
            ProfileAgentId = previous.ProfileAgentId,
        };
    }

    private static bool ChangesScope(JsonElement item, string key, string? previous) =>
        item.TryGetProperty(key, out _) && Text(item, key) != previous;

    private static bool ClearsIdentity(JsonElement item, ThinkingIdentity previous) =>
        Clears(item, "model", previous.Model)
        || Clears(item, "modelProvider", previous.Provider)
        || Clears(item, "provider", previous.Provider)
        || (item.TryGetProperty("agentRuntime", out var runtime)
            && previous.RuntimeId is not null
            && (runtime.ValueKind != JsonValueKind.Object || Text(runtime, "id") is null));

    private static bool Clears(JsonElement item, string key, string? previous) =>
        previous is not null && item.TryGetProperty(key, out var value)
        && (value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(value.GetString()));

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    // Public legacy spellings from upstream thinking.shared.ts, not model policy.
    internal static string NormalizeLegacy(string value)
    {
        var key = value.Trim().ToLowerInvariant();
        var collapsed = string.Concat(key.Where(c => !char.IsWhiteSpace(c) && c is not '_' and not '-'));
        if (collapsed is "adaptive" or "auto") return "adaptive";
        if (collapsed is "max" or "ultra") return collapsed;
        if (collapsed is "xhigh" or "extrahigh") return "xhigh";
        return key switch
        {
            "off" or "none" => "off",
            "on" or "enable" or "enabled" or "low" or "thinkhard" or "think-hard" or "think_hard" => "low",
            "min" or "minimal" or "think" => "minimal",
            "mid" or "med" or "medium" or "thinkharder" or "think-harder" or "harder" => "medium",
            "high" or "ultrathink" or "thinkhardest" or "highest" => "high",
            _ => key,
        };
    }
}
