using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>
/// Result of <see cref="ChannelConfigPatchBuilder.BuildPatch"/>.
///
/// Either <see cref="Patch"/> is set (caller sends it via <c>config.patch</c>)
/// OR <see cref="BlockedReason"/> is set (caller refuses to send and surfaces
/// the reason to the user — typically "you've got redacted secrets in other
/// channels that we can't safely round-trip; open the Config page instead").
/// </summary>
public sealed class ChannelPatchBuildResult
{
    /// <summary>The patched full-config to send. Null when <see cref="BlockedReason"/> is set.</summary>
    public JsonElement? Patch { get; init; }

    /// <summary>Human-readable reason the patch was refused. Null when <see cref="Patch"/> is set.</summary>
    public string? BlockedReason { get; init; }

    /// <summary>Dot-path of the field that caused <see cref="BlockedReason"/>, when applicable.</summary>
    public string? BlockedPath { get; init; }
}

/// <summary>
/// Pure helper that merges a per-channel set of credential updates into a
/// cached gateway config snapshot, producing a full-config JsonElement
/// suitable for the gateway's <c>config.patch { raw, baseHash }</c> wire.
///
/// Necessary because the gateway (v2026.5+) only accepts whole-config writes —
/// the legacy <c>config.set { path, value }</c> dot-path API was removed and
/// per-field writes are rejected with "must have required property 'raw'".
///
/// Safety rail: the gateway redacts secrets in <c>config.get</c> responses,
/// so blindly round-tripping the cached config can clobber unrelated secrets
/// with their redaction sentinels. <see cref="BuildPatch"/> scans for the
/// common sentinels (<c>[REDACTED]</c>, <c>&lt;redacted&gt;</c>, <c>***</c>)
/// in fields OUTSIDE the channel being written and aborts the patch if any
/// are found — caller should direct the user to the Config page in that
/// state.
/// </summary>
public static class ChannelConfigPatchBuilder
{
    /// <summary>
    /// Redaction sentinel strings we've observed gateways use. Matched
    /// case-insensitively, trimmed, and exactly — we don't want to false-
    /// positive on a value that happens to *contain* "redacted" as part of
    /// a legitimate string.
    /// </summary>
    private static readonly HashSet<string> RedactionSentinels = new(StringComparer.OrdinalIgnoreCase)
    {
        "[REDACTED]",
        "<redacted>",
        "***",
        "*****",
        "********",
    };

    /// <summary>
    /// Build a patched full-config from the cached config and a list of
    /// per-field updates for one channel.
    /// </summary>
    /// <param name="cachedConfig">
    /// The actual config root (already unwrapped from any <c>{ path, raw,
    /// parsed }</c> wrapper — pass <c>parsed</c>). May be Undefined / Null
    /// if the gateway hasn't returned config yet, in which case we start
    /// with an empty document.
    /// </param>
    /// <param name="channelId">Channel id, e.g. <c>telegram</c>.</param>
    /// <param name="updates">
    /// Dot-path / value pairs, e.g. <c>("channels.telegram.botToken",
    /// "12345:abc")</c>. The builder also sets <c>channels.{channelId}.enabled
    /// = true</c> automatically — callers shouldn't include it.
    /// </param>
    /// <param name="multilineDotPaths">
    /// Dot-paths whose string value should be split on newlines into an
    /// array of trimmed non-empty entries (e.g. Nostr relay URLs).
    /// </param>
    public static ChannelPatchBuildResult BuildPatch(
        JsonElement cachedConfig,
        string channelId,
        IReadOnlyList<(string DotPath, object Value)> updates,
        ISet<string>? multilineDotPaths = null)
    {
        if (string.IsNullOrWhiteSpace(channelId))
            throw new ArgumentException("channelId is required", nameof(channelId));
        if (updates == null)
            throw new ArgumentNullException(nameof(updates));

        multilineDotPaths ??= new HashSet<string>(StringComparer.Ordinal);

        // Seed a mutable dict from the cached config. An Object root is the
        // only valid input — otherwise treat as fresh.
        Dictionary<string, object?> root = cachedConfig.ValueKind == JsonValueKind.Object
            ? DeserializeObject(cachedConfig)
            : new Dictionary<string, object?>();

        // Safety rail: scan the cached config for redaction sentinels in any
        // leaf string field that lives OUTSIDE the channel we're writing.
        // If we'd be re-sending one of those, abort — the gateway might
        // clobber the on-disk secret with the sentinel.
        var targetPrefix = $"channels.{channelId}.";
        var redactedPath = FindRedactionSentinel(cachedConfig, "", targetPrefix);
        if (redactedPath != null)
        {
            return new ChannelPatchBuildResult
            {
                BlockedReason =
                    $"Your gateway returns redacted credentials for other channels (e.g. {redactedPath}). " +
                    "Saving from here would risk overwriting those secrets with their redaction placeholders. " +
                    "Use Open Config page for safe editing while we sort this out.",
                BlockedPath = redactedPath,
            };
        }

        // Apply each requested field update at its dot-path.
        foreach (var (path, rawValue) in updates)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var value = NormalizeValue(rawValue, multilineDotPaths.Contains(path));
            SetNestedValue(root, path, value);
        }

        // Channel enable flag — the gateway needs `enabled: true` alongside
        // credentials before it will start the channel (see e.g. gateway test
        // src/commands/channels.adds-non-default-telegram-account.test.ts).
        SetNestedValue(root, $"channels.{channelId}.enabled", true);

        // Reserialize → re-parse → clone so the returned JsonElement isn't
        // tied to a JsonDocument that goes out of scope on caller side.
        var json = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
        using var doc = JsonDocument.Parse(json);
        return new ChannelPatchBuildResult { Patch = doc.RootElement.Clone() };
    }

    /// <summary>
    /// Recursively walk <paramref name="el"/> looking for a string leaf whose
    /// value matches one of <see cref="RedactionSentinels"/>. Returns the
    /// first matching dot-path found, OR null if none. Paths that start with
    /// <paramref name="excludePrefix"/> are skipped — we don't care about
    /// sentinels in the channel we're about to overwrite.
    /// </summary>
    internal static string? FindRedactionSentinel(JsonElement el, string path, string excludePrefix)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                var childPath = string.IsNullOrEmpty(path) ? prop.Name : $"{path}.{prop.Name}";
                if (!string.IsNullOrEmpty(excludePrefix) && childPath.StartsWith(excludePrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                var hit = FindRedactionSentinel(prop.Value, childPath, excludePrefix);
                if (hit != null) return hit;
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var item in el.EnumerateArray())
            {
                var childPath = $"{path}[{i++}]";
                var hit = FindRedactionSentinel(item, childPath, excludePrefix);
                if (hit != null) return hit;
            }
        }
        else if (el.ValueKind == JsonValueKind.String)
        {
            var v = el.GetString();
            if (v != null && RedactionSentinels.Contains(v.Trim()))
                return path;
        }
        return null;
    }

    /// <summary>
    /// Convert a request value into the JSON-serializable form we'll write.
    /// For multiline fields we split on \n and trim each line — relay URL
    /// lists arrive as one-per-line text.
    /// </summary>
    private static object? NormalizeValue(object value, bool multiline)
    {
        if (multiline && value is string s)
        {
            var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToArray();
            return lines;
        }
        return value;
    }

    /// <summary>
    /// Apply a dot-separated path write to a mutable dict tree. Creates
    /// intermediate objects as needed; deserializes existing JsonElement
    /// objects into mutable Dictionary instances so the tree stays
    /// uniformly writable. Empty segments are rejected (would create
    /// "" keys in the JSON file).
    /// </summary>
    internal static void SetNestedValue(Dictionary<string, object?> dict, string dotPath, object? value)
    {
        var segments = dotPath.Split('.');
        if (segments.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException($"Invalid dot path '{dotPath}': empty segment.", nameof(dotPath));

        var current = dict;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            var key = segments[i];
            if (current.TryGetValue(key, out var existing))
            {
                if (existing is Dictionary<string, object?> childDict)
                {
                    current = childDict;
                    continue;
                }
                if (existing is JsonElement je && je.ValueKind == JsonValueKind.Object)
                {
                    var converted = DeserializeObject(je);
                    current[key] = converted;
                    current = converted;
                    continue;
                }
                // Existing non-object value at intermediate path — overwrite
                // with a new object so the deeper write can proceed.
            }
            var fresh = new Dictionary<string, object?>();
            current[key] = fresh;
            current = fresh;
        }
        current[segments[^1]] = value;
    }

    /// <summary>
    /// Deep-deserialize a JsonElement Object into a Dictionary tree we can
    /// mutate. Nested objects become Dictionaries; everything else (arrays,
    /// scalars) stays as JsonElement and is preserved verbatim on re-
    /// serialization — only the paths we explicitly write are changed.
    /// </summary>
    private static Dictionary<string, object?> DeserializeObject(JsonElement obj)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.ValueKind == JsonValueKind.Object
                ? DeserializeObject(prop.Value)
                : (object?)prop.Value.Clone();
        }
        return dict;
    }
}
