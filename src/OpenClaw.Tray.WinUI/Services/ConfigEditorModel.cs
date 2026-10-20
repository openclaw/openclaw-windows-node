using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClawTray.Services;

internal readonly record struct BindingRoute(
    string Channel,
    string AccountId,
    string AgentId,
    string? Peer,
    int? Priority);

internal sealed record ConfigEditorSnapshot(JsonElement Root, string? BaseHash)
{
    public static ConfigEditorSnapshot Empty { get; } = new(default, null);

    public bool HasRoot => Root.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null;
}

internal static class ConfigEditorModel
{
    public static ConfigEditorSnapshot CaptureSnapshot(JsonElement configResponse)
    {
        var root = ExtractConfigRoot(configResponse);
        var baseHash = ExtractBaseHash(configResponse);
        return new ConfigEditorSnapshot(root.Clone(), baseHash);
    }

    public static JsonElement ExtractConfigRoot(JsonElement configResponse)
    {
        if (configResponse.TryGetProperty("parsed", out var parsed))
            return parsed;
        if (configResponse.TryGetProperty("config", out var config))
            return config;
        return configResponse;
    }

    public static IReadOnlyList<BindingRoute> ReadBindingRoutes(JsonElement configResponse)
    {
        var root = ExtractConfigRoot(configResponse);
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("bindings", out var bindingsEl)
            || bindingsEl.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var routes = new List<BindingRoute>();
        foreach (var item in bindingsEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var match = item.TryGetProperty("match", out var matchElement)
                && matchElement.ValueKind == JsonValueKind.Object
                ? matchElement
                : default;
            var channel = ReadString(item, "channel") ?? ReadString(match, "channel") ?? "";
            var accountId = ReadString(item, "accountId") ?? ReadString(match, "accountId") ?? "*";
            var agentId = ReadString(item, "agentId") ?? "main";
            var peer = ReadString(item, "peer") ?? ReadPeer(match);
            int? priority = null;
            if (item.TryGetProperty("priority", out var priorityElement)
                && priorityElement.TryGetInt32(out var priorityValue))
            {
                priority = priorityValue;
            }

            routes.Add(new BindingRoute(channel, accountId, agentId, peer, priority));
        }

        return routes;
    }

    private static string? ReadString(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static string? ReadPeer(JsonElement match)
    {
        if (match.ValueKind != JsonValueKind.Object
            || !match.TryGetProperty("peer", out var peer))
        {
            return null;
        }

        if (peer.ValueKind == JsonValueKind.String)
            return peer.GetString();
        if (peer.ValueKind != JsonValueKind.Object)
            return null;

        var kind = ReadString(peer, "kind");
        var id = ReadString(peer, "id");
        if (string.IsNullOrEmpty(id))
            return kind;
        return string.IsNullOrEmpty(kind) ? id : kind + ":" + id;
    }

    public static string? ExtractBaseHash(JsonElement configResponse)
    {
        if (configResponse.TryGetProperty("baseHash", out var baseHash) &&
            baseHash.ValueKind == JsonValueKind.String)
            return baseHash.GetString();

        if (configResponse.TryGetProperty("hash", out var hash) &&
            hash.ValueKind == JsonValueKind.String)
            return hash.GetString();

        if (configResponse.TryGetProperty("raw", out var raw) &&
            raw.ValueKind == JsonValueKind.String &&
            raw.GetString() is { } rawContent)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(rawContent);
            var computedHash = System.Security.Cryptography.SHA256.HashData(bytes);
            return Convert.ToHexStringLower(computedHash);
        }

        return null;
    }

    public static JsonElement ApplyChanges(JsonElement root, IReadOnlyDictionary<string, object?> changes)
    {
        var node = JsonNode.Parse(root.GetRawText()) ?? new JsonObject();
        foreach (var (path, value) in changes)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            if (value?.GetType() == typeof(object))
                continue;

            SetPath(node, path, JsonSerializer.SerializeToNode(value));
        }

        using var document = JsonDocument.Parse(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return document.RootElement.Clone();
    }

    public static Dictionary<string, object?> RelativeChangesFor(
        string sectionPath,
        IReadOnlyDictionary<string, object?> changes)
    {
        var relative = new Dictionary<string, object?>(StringComparer.Ordinal);
        var prefix = string.IsNullOrEmpty(sectionPath) ? "" : sectionPath + ".";

        foreach (var (path, value) in changes)
        {
            if (string.IsNullOrEmpty(sectionPath))
            {
                relative[path] = value;
            }
            else if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                relative[path[prefix.Length..]] = value;
            }
        }

        return relative;
    }

    public static JsonElement ApplyRelativeChanges(
        JsonElement section,
        string sectionPath,
        IReadOnlyDictionary<string, object?> changes)
    {
        var relative = RelativeChangesFor(sectionPath, changes);
        return relative.Count == 0 ? section.Clone() : ApplyChanges(section, relative);
    }

    public static object? CoerceNumber(double value, string schemaType)
    {
        if (schemaType == "integer")
        {
            if (value > long.MaxValue || value < long.MinValue)
                return value;

            return Convert.ToInt64(Math.Truncate(value), CultureInfo.InvariantCulture);
        }

        return value;
    }

    private static void SetPath(JsonNode node, string dotPath, JsonNode? value)
    {
        var segments = dotPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return;

        var current = node;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            if (current is not JsonObject obj)
                return;

            if (obj[segment] is not JsonObject child)
            {
                child = new JsonObject();
                obj[segment] = child;
            }

            current = child;
        }

        if (current is JsonObject target)
            target[segments[^1]] = value;
    }
}
