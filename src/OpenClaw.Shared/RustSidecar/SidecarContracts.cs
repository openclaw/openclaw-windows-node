using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.Shared.RustSidecar;

internal sealed record SidecarLimits(
    uint MaxFrameBytes,
    ushort MaxInFlight,
    uint BootstrapTimeoutMs);

internal sealed record SidecarPeerIdentity(
    SidecarPeerRole Role,
    string Name,
    string Version,
    string ArtifactIdentity);

internal sealed record SidecarProtocolOffer(
    ushort ProtocolMajor,
    ushort ProtocolMinor,
    SidecarPeerIdentity Peer,
    ulong FeatureBits,
    SidecarLimits Limits);

internal sealed record SidecarProtocolSelection(
    ushort ProtocolMajor,
    ushort ProtocolMinor,
    ulong FeatureBits,
    SidecarLimits Limits);

internal sealed record SidecarRuntimeConfiguration(
    ulong ManifestGeneration,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> Commands,
    ushort MaxConcurrency,
    uint MaxInputBytes,
    uint MaxOutputBytes,
    uint DefaultTimeoutMs,
    uint MaxTimeoutMs,
    uint ResultGraceMs)
{
    internal JsonObject ToConfigureMessage()
    {
        var capabilities = new JsonArray(
            Capabilities.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        var commands = new JsonArray(
            Commands.Select(name => (JsonNode)new JsonObject { ["name"] = name }).ToArray());
        return new JsonObject
        {
            ["type"] = "configure",
            ["configuration"] = new JsonObject
            {
                ["manifestGeneration"] = ManifestGeneration,
                ["capabilities"] = capabilities,
                ["commands"] = commands,
                ["maxConcurrency"] = MaxConcurrency,
                ["maxInputBytes"] = MaxInputBytes,
                ["maxOutputBytes"] = MaxOutputBytes,
                ["defaultTimeoutMs"] = DefaultTimeoutMs,
                ["maxTimeoutMs"] = MaxTimeoutMs,
                ["resultGraceMs"] = ResultGraceMs
            }
        };
    }

    internal JsonObject ToManifest() => new()
    {
        ["manifestGeneration"] = ManifestGeneration,
        ["capabilities"] = new JsonArray(
            Capabilities.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
        ["commands"] = new JsonArray(
            Commands.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())
    };
}

internal static class SidecarJson
{
    internal const ulong MaxPortableInteger = 9_007_199_254_740_991;
    // serde_json starts with 128 remaining levels and rejects the 128th container.
    internal const int MaxDepth = 127;

    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        MaxDepth = MaxDepth,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal static byte[] Serialize(JsonNode node) =>
        JsonSerializer.SerializeToUtf8Bytes(node, SerializerOptions);

    internal static JsonElement Parse(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(
            json.ToArray(),
            new JsonDocumentOptions { MaxDepth = MaxDepth });
        return document.RootElement.Clone();
    }

    internal static bool ValueEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
            return false;
        return left.ValueKind switch
        {
            JsonValueKind.Object => ObjectEquals(left, right),
            JsonValueKind.Array => left.EnumerateArray().SequenceEqual(
                right.EnumerateArray(),
                JsonElementValueComparer.Instance),
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.Number => NumberEquals(left, right),
            JsonValueKind.True or JsonValueKind.False => left.GetBoolean() == right.GetBoolean(),
            JsonValueKind.Null => true,
            _ => false
        };
    }

    private static bool ObjectEquals(JsonElement left, JsonElement right)
    {
        var leftProperties = ToPropertyMap(left);
        var rightProperties = ToPropertyMap(right);
        return leftProperties.Count == rightProperties.Count &&
            leftProperties.All(property =>
                rightProperties.TryGetValue(property.Key, out var value) &&
                ValueEquals(property.Value, value));
    }

    private static Dictionary<string, JsonElement> ToPropertyMap(JsonElement value)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            properties[property.Name] = property.Value;
        return properties;
    }

    private static bool NumberEquals(JsonElement left, JsonElement right)
    {
        var leftKind = GetNumberKind(left);
        if (leftKind != GetNumberKind(right))
            return false;
        return leftKind switch
        {
            JsonNumberKind.PositiveInteger =>
                left.GetUInt64() == right.GetUInt64(),
            JsonNumberKind.NegativeInteger =>
                left.GetInt64() == right.GetInt64(),
            _ => left.GetDouble().Equals(right.GetDouble())
        };
    }

    private static JsonNumberKind GetNumberKind(JsonElement value)
    {
        var raw = value.GetRawText();
        if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E') || raw == "-0")
            return JsonNumberKind.Float;
        return raw[0] == '-' ? JsonNumberKind.NegativeInteger : JsonNumberKind.PositiveInteger;
    }

    private enum JsonNumberKind
    {
        PositiveInteger,
        NegativeInteger,
        Float
    }

    private sealed class JsonElementValueComparer : IEqualityComparer<JsonElement>
    {
        internal static readonly JsonElementValueComparer Instance = new();
        public bool Equals(JsonElement x, JsonElement y) => ValueEquals(x, y);
        public int GetHashCode(JsonElement obj) => throw new NotSupportedException();
    }

    internal static JsonElement RequiredObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new SidecarProtocolException($"Sidecar field '{name}' must be an object.");
        return value;
    }

    internal static string RequiredString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new SidecarProtocolException($"Sidecar field '{name}' must be a string.");
        return value.GetString()!;
    }

    internal static ulong RequiredUInt64(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) ||
            !value.TryGetUInt64(out var result) ||
            result > MaxPortableInteger)
        {
            throw new SidecarProtocolException(
                $"Sidecar field '{name}' must be a portable unsigned integer.");
        }
        return result;
    }

    internal static void EnsureObjectShape(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new SidecarProtocolException("Sidecar value must be an object.");
        var allowed = expected.ToHashSet(StringComparer.Ordinal);
        var count = 0;
        foreach (var property in value.EnumerateObject())
        {
            count++;
            if (!allowed.Contains(property.Name))
                throw new SidecarProtocolException($"Unknown sidecar field '{property.Name}'.");
        }
        if (count != allowed.Count || allowed.Any(name => !value.TryGetProperty(name, out _)))
            throw new SidecarProtocolException("Sidecar value is missing a required field.");
    }

    internal static bool IsPortableJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().All(property => IsPortableJson(property.Value)),
        JsonValueKind.Array => value.EnumerateArray().All(IsPortableJson),
        JsonValueKind.Number => IsPortableNumber(value),
        _ => true
    };

    private static bool IsPortableNumber(JsonElement value)
    {
        if (value.TryGetInt64(out var signed))
            return signed >= -checked((long)MaxPortableInteger) &&
                signed <= checked((long)MaxPortableInteger);
        if (value.TryGetUInt64(out var unsigned))
            return unsigned <= MaxPortableInteger;
        if (!value.TryGetDouble(out var floating) || !double.IsFinite(floating))
            return false;
        return floating != Math.Truncate(floating) || Math.Abs(floating) <= MaxPortableInteger;
    }
}
