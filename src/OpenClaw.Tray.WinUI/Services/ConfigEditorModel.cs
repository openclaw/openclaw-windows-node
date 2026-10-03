using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClawTray.Helpers;

namespace OpenClawTray.Services;

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

    public static string? JsonKindMismatch(JsonElement value, string? expectedType)
    {
        if (expectedType == "object" && value.ValueKind != JsonValueKind.Object)
            return "Must be a JSON object.";
        if (expectedType == "string" && value.ValueKind != JsonValueKind.String)
            return "Must be a JSON string.";
        return null;
    }

    public static string? FirstArrayItemKindError(JsonElement array, JsonElement itemsSchema)
    {
        if (array.ValueKind != JsonValueKind.Array)
            return "Must be a list.";

        var expectedType = ReadSchemaType(itemsSchema);
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            var error = JsonKindMismatch(item, expectedType);
            if (error != null)
                return $"Item {index + 1}: {error}";
            index++;
        }

        return null;
    }

    public static bool UseHiddenObjectEditor(string path, JsonElement schema) =>
        ConfigPathSensitivity.IsSensitive(path) && ReadSchemaType(schema) == "object";

    private static string? ReadSchemaType(JsonElement schemaNode)
    {
        if (!schemaNode.TryGetProperty("type", out var typeEl))
            return null;
        if (typeEl.ValueKind == JsonValueKind.String)
            return typeEl.GetString();
        if (typeEl.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in typeEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;
            var schemaType = item.GetString();
            if (!string.IsNullOrEmpty(schemaType) && schemaType != "null")
                return schemaType;
        }

        return null;
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

internal enum SensitiveArrayDecision
{
    Preserve,
    Replace,
    Clear
}

/// <summary>
/// Edit session for a sensitive array or object whose current value must stay off the page.
/// The session stores a count and the newly typed JSON. It never receives the stored secrets.
/// </summary>
internal sealed class SensitiveArrayEditSession
{
    public SensitiveArrayEditSession(int existingCount, JsonValueKind expectedKind = JsonValueKind.Array)
    {
        if (existingCount < 0)
            throw new ArgumentOutOfRangeException(nameof(existingCount));
        if (expectedKind is not JsonValueKind.Array and not JsonValueKind.Object)
            throw new ArgumentOutOfRangeException(nameof(expectedKind));
        ExistingCount = existingCount;
        ExpectedKind = expectedKind;
    }

    public int ExistingCount { get; }
    public JsonValueKind ExpectedKind { get; }
    public bool ReplaceOpen { get; private set; }
    public bool ClearConfirmOpen { get; private set; }
    public string Draft { get; private set; } = "";
    public string? Error { get; private set; }
    public SensitiveArrayDecision Decision { get; private set; }
    public JsonElement? Replacement { get; private set; }

    public string CountText => ExistingCount == 1
        ? "1 entry is configured. Stored values stay hidden."
        : $"{ExistingCount} entries are configured. Stored values stay hidden.";

    public static JsonElement EmptyArray()
    {
        using var document = JsonDocument.Parse("[]");
        return document.RootElement.Clone();
    }

    public static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    public JsonElement EmptyReplacement() =>
        ExpectedKind == JsonValueKind.Object ? EmptyObject() : EmptyArray();

    public void BeginReplace()
    {
        ReplaceOpen = true;
        ClearConfirmOpen = false;
        Draft = "";
        Error = null;
    }

    public void SetDraft(string? text) => Draft = text ?? "";

    public bool TryReadDraft(out JsonElement parsed)
    {
        parsed = default;
        try
        {
            using var document = JsonDocument.Parse(Draft);
            if (document.RootElement.ValueKind != ExpectedKind)
            {
                Error = ExpectedKind == JsonValueKind.Object
                    ? "Must be a JSON object."
                    : "Must be a JSON array.";
                return false;
            }

            parsed = document.RootElement.Clone();
            Error = null;
            return true;
        }
        catch (JsonException ex)
        {
            Error = $"Invalid JSON: {ex.Message}";
            return false;
        }
    }

    public void CommitReplace(JsonElement replacement)
    {
        Replacement = replacement;
        Decision = SensitiveArrayDecision.Replace;
        Error = null;
        ReplaceOpen = false;
        Draft = "";
    }

    public bool TryApplyReplace()
    {
        if (!TryReadDraft(out var parsed))
            return false;

        CommitReplace(parsed);
        return true;
    }

    public void CancelReplace()
    {
        ReplaceOpen = false;
        Draft = "";
        Error = null;
    }

    public void BeginClear()
    {
        ClearConfirmOpen = true;
        ReplaceOpen = false;
    }

    public void ConfirmClear()
    {
        ClearConfirmOpen = false;
        ReplaceOpen = false;
        Draft = "";
        Error = null;
        Replacement = null;
        Decision = SensitiveArrayDecision.Clear;
    }

    public void CancelClear() => ClearConfirmOpen = false;

    public bool TryRestoreCommittedReplacement(out JsonElement value)
    {
        if (Replacement is JsonElement previous)
        {
            value = previous;
            return true;
        }

        value = default;
        return false;
    }
}
