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

    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    internal static byte[] Serialize(JsonNode node) =>
        JsonSerializer.SerializeToUtf8Bytes(node, SerializerOptions);

    internal static JsonElement Parse(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        return document.RootElement.Clone();
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
        if (!parent.TryGetProperty(name, out var value) || !value.TryGetUInt64(out var result))
            throw new SidecarProtocolException($"Sidecar field '{name}' must be an unsigned integer.");
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
            return signed >= -checked((long)MaxPortableInteger);
        if (value.TryGetUInt64(out var unsigned))
            return unsigned <= MaxPortableInteger;
        if (!value.TryGetDouble(out var floating) || !double.IsFinite(floating))
            return false;
        return floating != Math.Truncate(floating) || Math.Abs(floating) <= MaxPortableInteger;
    }
}
