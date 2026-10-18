using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Shared;

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

    public static string? FindUneditedRedactionSentinel(
        JsonElement updated,
        IEnumerable<string> editedPaths,
        JsonElement baseDocument)
    {
        var edited = new HashSet<string>(editedPaths, StringComparer.OrdinalIgnoreCase);
        return FindBlockedRedactionSentinel(updated, baseDocument, "", edited);
    }

    public static JsonElement OmitUntouchedRedactionSentinels(
        JsonElement document,
        IEnumerable<string> editedPaths) =>
        OmitUntouchedRedactionSentinels(document, editedPaths, document);

    public static JsonElement OmitUntouchedRedactionSentinels(
        JsonElement document,
        IEnumerable<string> editedPaths,
        JsonElement baseDocument)
    {
        var node = JsonNode.Parse(document.GetRawText());
        if (node is null)
            return document.Clone();

        var edited = new HashSet<string>(editedPaths, StringComparer.OrdinalIgnoreCase);
        var arraysToDrop = new List<ArrayLocation>();
        RemoveUntouchedRedactionSentinels(
            node, "", edited, baseDocument, document, baseDocument, null, null, arraysToDrop);
        foreach (var array in arraysToDrop)
            array.Parent.Remove(array.Key);

        using var rewritten = JsonDocument.Parse(node.ToJsonString());
        return rewritten.RootElement.Clone();
    }

    private static string? FindBlockedRedactionSentinel(
        JsonElement updated,
        JsonElement baseDocument,
        string path,
        HashSet<string> edited) =>
        FindBlockedRedactionSentinel(updated, baseDocument, path, edited, baseDocument, updated, null);

    private static string? FindBlockedRedactionSentinel(
        JsonElement updated,
        JsonElement baseDocument,
        string path,
        HashSet<string> edited,
        JsonElement baseRoot,
        JsonElement submittedRoot,
        string? outermostNonIdArray)
    {
        if (updated.ValueKind == JsonValueKind.Object)
        {
            var baseObject = baseDocument.ValueKind == JsonValueKind.Object
                ? baseDocument
                : default;
            foreach (var property in updated.EnumerateObject())
            {
                var childPath = string.IsNullOrEmpty(path) ? property.Name : $"{path}.{property.Name}";
                var childBase = default(JsonElement);
                if (baseObject.ValueKind == JsonValueKind.Object)
                    baseObject.TryGetProperty(property.Name, out childBase);
                var hit = FindBlockedRedactionSentinel(
                    property.Value, childBase, childPath, edited, baseRoot, submittedRoot, outermostNonIdArray);
                if (hit != null)
                    return hit;
            }
        }
        else if (updated.ValueKind == JsonValueKind.Array)
        {
            var idKeyed = baseDocument.ValueKind == JsonValueKind.Array && ArrayIsIdKeyed(baseDocument);
            var nextOuter = outermostNonIdArray;
            if (nextOuter is null && baseDocument.ValueKind == JsonValueKind.Array && !idKeyed && path.Length > 0)
                nextOuter = path;
            var index = 0;
            foreach (var item in updated.EnumerateArray())
            {
                var childPath = $"{path}[{index}]";
                var childBase = default(JsonElement);
                if (idKeyed && TryGetObjectId(item, out var id) &&
                    TryFindArrayItemById(baseDocument, id, out var matched))
                {
                    childBase = matched;
                }
                else if (!idKeyed &&
                    baseDocument.ValueKind == JsonValueKind.Array &&
                    index < baseDocument.GetArrayLength())
                {
                    childBase = baseDocument[index];
                }

                index++;
                var hit = FindBlockedRedactionSentinel(
                    item, childBase, childPath, edited, baseRoot, submittedRoot, nextOuter);
                if (hit != null)
                    return hit;
            }
        }
        else if (updated.ValueKind == JsonValueKind.String &&
                 ChannelConfigPatchBuilder.IsRedactionSentinel(updated.GetString()))
        {
            var loadedSentinel = baseDocument.ValueKind == JsonValueKind.String &&
                                 ChannelConfigPatchBuilder.IsRedactionSentinel(baseDocument.GetString());
            // __OPENCLAW_REDACTED__ stays out of the Channels sentinel set.
            // A direct edit of a credential that replaces that mask with a
            // legacy placeholder is the same refusal as the parent-object edit.
            if (edited.Contains(path))
            {
                return loadedSentinel || LoadedCredentialIsNativeMask(baseDocument, path)
                    ? path
                    : null;
            }

            if (HasAncestorEdit(edited, path) &&
                IsCredentialPath(path) &&
                !SameSentinel(baseDocument, updated.GetString()))
            {
                return path;
            }

            // Object fields and id-keyed array fields can be left out of the
            // patch. Gateway keeps the stored secret for an absent key. An array
            // whose entries lack stable ids is replaced wholesale, so an indexed
            // edit inside that array cannot drop the masked credential safely.
            if (ShouldOmitUntouchedSentinel(path))
            {
                if (outermostNonIdArray != null && EditedPathInsideArray(edited, outermostNonIdArray))
                    return path;

                return null;
            }

            if (!loadedSentinel || !IsCredentialPath(path))
                return null;

            return path;
        }

        return null;
    }

    private static bool IsCredentialPath(string path)
    {
        var name = path;
        var bracket = name.LastIndexOf('[');
        if (bracket >= 0 && name.EndsWith("]", StringComparison.Ordinal))
            name = name[..bracket];
        var dot = name.LastIndexOf('.');
        if (dot >= 0)
            name = name[(dot + 1)..];

        return name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("webhook", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("privateKey", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("key", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldOmitUntouchedSentinel(string path)
    {
        if (!path.Contains('[', StringComparison.Ordinal))
            return true;

        // A masked array element is not a property we can drop. A credential
        // field on an item inside the array is.
        return IsCredentialPath(path) && !path.EndsWith(']');
    }

    private static void RemoveUntouchedRedactionSentinels(
        JsonNode node,
        string path,
        HashSet<string> edited,
        JsonElement baseRoot,
        JsonElement submittedRoot,
        JsonElement loadedNode,
        string? outermostNonIdArray,
        ArrayLocation? outermostArray,
        List<ArrayLocation> arraysToDrop)
    {
        if (node is JsonObject obj)
        {
            var loadedObject = loadedNode.ValueKind == JsonValueKind.Object ? loadedNode : default;
            var removals = new List<string>();
            foreach (var property in obj)
            {
                var childPath = string.IsNullOrEmpty(path) ? property.Key : $"{path}.{property.Key}";
                var loadedChild = default(JsonElement);
                if (loadedObject.ValueKind == JsonValueKind.Object)
                    loadedObject.TryGetProperty(property.Key, out loadedChild);

                if (property.Value is JsonValue value &&
                    value.TryGetValue<string>(out var text) &&
                    (ChannelConfigPatchBuilder.IsRedactionSentinel(text) ||
                     ChannelConfigPatchBuilder.IsNativeGatewayRedactionMask(text)) &&
                    !edited.Contains(childPath) &&
                    ShouldOmitUntouchedSentinel(childPath))
                {
                    // Leave a native mask on a non-credential leaf, including a
                    // SecretRef id. The gateway uses that mask to restore the
                    // stored id or to refuse a provider change.
                    if (ChannelConfigPatchBuilder.IsNativeGatewayRedactionMask(text) &&
                        !IsCredentialPath(childPath))
                    {
                        continue;
                    }

                    if (!IsCredentialPath(childPath) && !LoadedValueIsSentinel(loadedChild))
                        continue;

                    if (HasAncestorEdit(edited, childPath) && !SameSentinel(loadedChild, text))
                        continue;

                    if (outermostArray is null)
                        removals.Add(property.Key);
                    else if (outermostNonIdArray is null ||
                        !EditedPathInsideArray(edited, outermostNonIdArray))
                    {
                        arraysToDrop.Add(outermostArray.Value);
                    }

                    continue;
                }

                var childOuter = outermostNonIdArray;
                var childArray = outermostArray;
                if (childArray is null &&
                    property.Value is JsonArray &&
                    loadedChild.ValueKind == JsonValueKind.Array &&
                    !ArrayIsIdKeyed(loadedChild))
                {
                    childOuter = childPath;
                    childArray = new ArrayLocation(obj, property.Key);
                }

                if (property.Value is not null)
                    RemoveUntouchedRedactionSentinels(
                        property.Value,
                        childPath,
                        edited,
                        baseRoot,
                        submittedRoot,
                        loadedChild,
                        childOuter,
                        childArray,
                        arraysToDrop);
            }

            foreach (var key in removals)
                obj.Remove(key);
        }
        else if (node is JsonArray array)
        {
            var loadedArray = loadedNode.ValueKind == JsonValueKind.Array ? loadedNode : default;
            var idKeyed = loadedArray.ValueKind == JsonValueKind.Array && ArrayIsIdKeyed(loadedArray);
            var nextOuter = outermostNonIdArray;
            if (nextOuter is null && loadedArray.ValueKind == JsonValueKind.Array && !idKeyed && path.Length > 0)
                nextOuter = path;
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is not JsonNode item)
                    continue;

                var loadedItem = default(JsonElement);
                if (idKeyed &&
                    item is JsonObject itemObject &&
                    itemObject["id"] is JsonValue idValue &&
                    idValue.TryGetValue<string>(out var id) &&
                    TryFindArrayItemById(loadedArray, id, out var matched))
                {
                    loadedItem = matched;
                }
                else if (!idKeyed &&
                    loadedArray.ValueKind == JsonValueKind.Array &&
                    index < loadedArray.GetArrayLength())
                {
                    loadedItem = loadedArray[index];
                }

                RemoveUntouchedRedactionSentinels(
                    item,
                    $"{path}[{index}]",
                    edited,
                    baseRoot,
                    submittedRoot,
                    loadedItem,
                    nextOuter,
                    outermostArray,
                    arraysToDrop);
            }
        }
    }

    private readonly record struct ArrayLocation(JsonObject Parent, string Key);

    private readonly record struct ConfigPathSegment(string Name, int? Index);

    private static List<ConfigPathSegment> ParsePath(string path)
    {
        var segments = new List<ConfigPathSegment>();
        var index = 0;
        while (index < path.Length)
        {
            if (path[index] == '.')
            {
                index++;
                continue;
            }

            var start = index;
            while (index < path.Length && path[index] != '.' && path[index] != '[')
                index++;

            var name = path[start..index];
            int? arrayIndex = null;
            if (index < path.Length && path[index] == '[')
            {
                index++;
                var numberStart = index;
                while (index < path.Length && path[index] != ']')
                    index++;
                if (int.TryParse(path[numberStart..index], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                    arrayIndex = parsed;
                if (index < path.Length && path[index] == ']')
                    index++;
            }

            if (name.Length > 0)
                segments.Add(new ConfigPathSegment(name, arrayIndex));
        }

        return segments;
    }

    private static bool ArrayIsIdKeyed(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(id.GetString()))
            {
                return false;
            }
        }

        return true;
    }

    private static string? OutermostNonIdArrayPath(
        JsonElement baseRoot,
        JsonElement submittedRoot,
        string credentialPath)
    {
        if (!credentialPath.Contains('[', StringComparison.Ordinal) ||
            baseRoot.ValueKind != JsonValueKind.Object ||
            submittedRoot.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var baseCurrent = baseRoot;
        var submittedCurrent = submittedRoot;
        var walked = "";
        foreach (var segment in ParsePath(credentialPath))
        {
            if (baseCurrent.ValueKind != JsonValueKind.Object ||
                submittedCurrent.ValueKind != JsonValueKind.Object ||
                !baseCurrent.TryGetProperty(segment.Name, out var baseChild) ||
                !submittedCurrent.TryGetProperty(segment.Name, out var submittedChild))
            {
                return null;
            }

            walked = walked.Length == 0 ? segment.Name : $"{walked}.{segment.Name}";
            if (segment.Index is not int arrayIndex)
            {
                baseCurrent = baseChild;
                submittedCurrent = submittedChild;
                continue;
            }

            if (baseChild.ValueKind != JsonValueKind.Array ||
                submittedChild.ValueKind != JsonValueKind.Array ||
                (uint)arrayIndex >= (uint)submittedChild.GetArrayLength())
            {
                return null;
            }

            if (!ArrayIsIdKeyed(baseChild))
                return walked;

            var submittedItem = submittedChild[arrayIndex];
            if (!TryGetObjectId(submittedItem, out var id) ||
                !TryFindArrayItemById(baseChild, id, out var matched))
            {
                return null;
            }

            walked = $"{walked}[{arrayIndex.ToString(CultureInfo.InvariantCulture)}]";
            baseCurrent = matched;
            submittedCurrent = submittedItem;
        }

        return null;
    }

    private static bool EditedPathInsideArray(HashSet<string> edited, string arrayPath)
    {
        foreach (var path in edited)
        {
            if (path.Equals(arrayPath, StringComparison.OrdinalIgnoreCase))
                return true;
            if (path.StartsWith(arrayPath + "[", StringComparison.OrdinalIgnoreCase))
                return true;
            if (IsAncestorEdit(path, arrayPath))
                return true;
        }

        return false;
    }

    private static bool HasAncestorEdit(HashSet<string> edited, string path)
    {
        foreach (var editedPath in edited)
        {
            if (IsAncestorEdit(editedPath, path))
                return true;
        }

        return false;
    }

    private static bool IsAncestorEdit(string editedPath, string arrayPath)
    {
        if (editedPath.Length == 0 || editedPath.Length >= arrayPath.Length)
            return false;
        if (!arrayPath.StartsWith(editedPath, StringComparison.OrdinalIgnoreCase))
            return false;

        var boundary = arrayPath[editedPath.Length];
        return boundary is '.' or '[';
    }

    private static bool SameSentinel(JsonElement loaded, string? submitted) =>
        loaded.ValueKind == JsonValueKind.String &&
        string.Equals(loaded.GetString(), submitted, StringComparison.Ordinal);

    private static bool CredentialArrayIsIdKeyed(JsonElement baseRoot, string propertyPath)
    {
        var bracket = propertyPath.LastIndexOf('[');
        if (bracket < 0)
            return false;

        var arrayPath = propertyPath[..bracket];
        if (!TryReadPath(baseRoot, arrayPath, out var array))
            return false;

        return ArrayIsIdKeyed(array);
    }

    private static bool LoadedCredentialIsNativeMask(JsonElement loaded, string path) =>
        IsCredentialPath(path) &&
        loaded.ValueKind == JsonValueKind.String &&
        ChannelConfigPatchBuilder.IsNativeGatewayRedactionMask(loaded.GetString());

    private static bool LoadedValueIsSentinel(JsonElement loaded) =>
        loaded.ValueKind == JsonValueKind.String &&
        ChannelConfigPatchBuilder.IsRedactionSentinel(loaded.GetString());

    private static bool LoadedValueIsRealSecret(JsonElement loaded) =>
        loaded.ValueKind == JsonValueKind.String &&
        !ChannelConfigPatchBuilder.IsRedactionSentinel(loaded.GetString());

    private static bool LoadedValueIsRealSecret(JsonElement baseRoot, JsonElement submittedRoot, string path) =>
        TryReadIdAware(baseRoot, submittedRoot, path, out var loaded) &&
        LoadedValueIsRealSecret(loaded);

    private static bool UnchangedLoadedSentinel(
        JsonElement baseRoot,
        JsonElement submittedRoot,
        string path,
        string submitted)
    {
        if (!TryReadIdAware(baseRoot, submittedRoot, path, out var element))
            return false;

        return SameSentinel(element, submitted);
    }

    private static bool LoadedStringIsRedactionSentinel(
        JsonElement baseRoot,
        JsonElement submittedRoot,
        string path)
    {
        if (!TryReadIdAware(baseRoot, submittedRoot, path, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return ChannelConfigPatchBuilder.IsRedactionSentinel(element.GetString());
    }

    private static bool TryReadIdAware(
        JsonElement baseRoot,
        JsonElement submittedRoot,
        string path,
        out JsonElement value)
    {
        value = default;
        var baseCurrent = baseRoot;
        var submittedCurrent = submittedRoot;
        foreach (var segment in ParsePath(path))
        {
            if (baseCurrent.ValueKind != JsonValueKind.Object ||
                submittedCurrent.ValueKind != JsonValueKind.Object ||
                !baseCurrent.TryGetProperty(segment.Name, out var baseChild) ||
                !submittedCurrent.TryGetProperty(segment.Name, out var submittedChild))
            {
                return false;
            }

            if (segment.Index is not int index)
            {
                baseCurrent = baseChild;
                submittedCurrent = submittedChild;
                continue;
            }

            if (submittedChild.ValueKind != JsonValueKind.Array ||
                (uint)index >= (uint)submittedChild.GetArrayLength())
            {
                return false;
            }

            var submittedItem = submittedChild[index];
            if (baseChild.ValueKind == JsonValueKind.Array &&
                ArrayIsIdKeyed(baseChild) &&
                TryGetObjectId(submittedItem, out var id) &&
                TryFindArrayItemById(baseChild, id, out var matched))
            {
                baseCurrent = matched;
            }
            else if (baseChild.ValueKind == JsonValueKind.Array &&
                !ArrayIsIdKeyed(baseChild) &&
                index < baseChild.GetArrayLength())
            {
                baseCurrent = baseChild[index];
            }
            else
            {
                return false;
            }

            submittedCurrent = submittedItem;
        }

        value = baseCurrent;
        return true;
    }

    private static bool TryGetObjectId(JsonElement item, out string id)
    {
        id = "";
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("id", out var idElement) ||
            idElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        id = idElement.GetString() ?? "";
        return id.Length > 0;
    }

    private static bool TryFindArrayItemById(JsonElement array, string id, out JsonElement item)
    {
        foreach (var candidate in array.EnumerateArray())
        {
            if (TryGetObjectId(candidate, out var candidateId) &&
                string.Equals(candidateId, id, StringComparison.Ordinal))
            {
                item = candidate;
                return true;
            }
        }

        item = default;
        return false;
    }

    private static bool TryReadPath(JsonElement root, string path, out JsonElement element)
    {
        element = root;
        foreach (var segment in ParsePath(path))
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(segment.Name, out element))
            {
                return false;
            }

            if (segment.Index is not int arrayIndex)
                continue;

            if (element.ValueKind != JsonValueKind.Array ||
                (uint)arrayIndex >= (uint)element.GetArrayLength())
            {
                return false;
            }

            element = element[arrayIndex];
        }

        return true;
    }

    private static void RemovePropertyAtPath(JsonNode root, string propertyPath)
    {
        JsonNode? current = root;
        var segments = ParsePath(propertyPath);
        for (var index = 0; index < segments.Count; index++)
        {
            if (current is not JsonObject obj)
                return;

            var segment = segments[index];
            if (obj[segment.Name] is not JsonNode child)
                return;

            if (index == segments.Count - 1)
            {
                obj.Remove(segment.Name);
                return;
            }

            if (segment.Index is int arrayIndex)
            {
                if (child is not JsonArray array || (uint)arrayIndex >= (uint)array.Count)
                    return;
                current = array[arrayIndex];
                continue;
            }

            current = child;
        }
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
