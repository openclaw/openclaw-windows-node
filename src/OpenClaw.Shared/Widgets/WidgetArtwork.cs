// Validates snapshot-owned PNG artwork and resolves named card resources, never native filesystem paths.
// Usage: WidgetArtwork.Expand(card, presentation.Data, WidgetArtwork.Read(presentation.Assets)).
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.Shared.Widgets;

/// <summary>Bounded image transport for agent-created artwork. Windows still owns image decoding.</summary>
internal static class WidgetArtwork
{
    internal const int MaxTotalBytes = 262_144;
    internal const int MaxDimension = 4096;

    internal static IReadOnlyDictionary<string, string> Read(JsonElement? assets)
    {
        var images = new Dictionary<string, string>(StringComparer.Ordinal);
        if (assets is null) return images;
        if (assets.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Widget assets must be a named object of PNG base64 resources.");
        var total = 0;
        foreach (var asset in assets.Value.EnumerateObject())
        {
            WidgetSnapshotStore.ValidateId(asset.Name);
            if (asset.Value.ValueKind != JsonValueKind.Object ||
                asset.Value.EnumerateObject().Count() != 1 ||
                !asset.Value.TryGetProperty("base64", out var encoded) || encoded.ValueKind != JsonValueKind.String)
                throw new ArgumentException("Widget artwork requires only a base64 PNG field.");
            var text = encoded.GetString()!;
            if (text.Length > ((MaxTotalBytes + 2) / 3) * 4)
                throw new ArgumentException("Widget artwork exceeds 262144 decoded bytes.");
            byte[] png;
            try { png = Convert.FromBase64String(text); }
            catch (FormatException error) { throw new ArgumentException("Widget artwork requires canonical base64 PNG data.", error); }
            total += png.Length;
            if (total > MaxTotalBytes)
                throw new ArgumentException("Widget artwork exceeds 262144 decoded bytes.");
            if (png.Length < 45 || Convert.ToBase64String(png) != text ||
                !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(8, 4)) != 13 ||
                !png.AsSpan(12, 4).SequenceEqual("IHDR"u8) ||
                !png.AsSpan(png.Length - 12).SequenceEqual(new byte[] { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 }) ||
                BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)) is < 1 or > MaxDimension ||
                BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)) is < 1 or > MaxDimension)
                throw new ArgumentException("Widget artwork requires a bounded PNG header and end marker, at most 4096 pixels per side.");
            if (!images.TryAdd("openclaw-asset:" + asset.Name, "data:image/png;base64," + text))
                throw new ArgumentException("Duplicate widget artwork name.");
        }
        return images;
    }

    /// <summary>Expands only validated resource fields. User-authored data, links and text remain data.</summary>
    internal static void Expand(JsonNode? node, JsonElement? content, IReadOnlyDictionary<string, string> images)
    {
        var remaining = WidgetCard.MaxExpandedTemplateBytes;
        Expand(node, content, images, ref remaining);
    }

    private static void Expand(JsonNode? node, JsonElement? content, IReadOnlyDictionary<string, string> images, ref int remaining)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array) Expand(child, content, images, ref remaining);
        }
        else if (node is JsonObject element)
        {
            foreach (var property in element.ToArray())
            {
                if (property.Key is "data" or "facts" or "choices") continue;
                if (property.Key is "url" or "iconUrl" or "poster" or "backgroundImage" &&
                    property.Value is JsonValue scalar && scalar.TryGetValue<string>(out var value))
                {
                    if (value.StartsWith("${$root.content.", StringComparison.Ordinal) && value.EndsWith('}') &&
                        content is { } resource)
                    {
                        foreach (var key in value[2..^1].Split('.').Skip(2))
                        {
                            if (resource.ValueKind != JsonValueKind.Object || !resource.TryGetProperty(key, out resource))
                                throw new ArgumentException("Widget resource binding was not found in presentation data.");
                        }
                        if (resource.ValueKind == JsonValueKind.String && images.ContainsKey(resource.GetString()!))
                            value = resource.GetString()!;
                    }
                    if (images.TryGetValue(value, out var image))
                    {
                        remaining -= image.Length;
                        if (remaining < 0)
                            throw new ArgumentException("Expanded widget artwork exceeds the native template limit. Use fewer repeated image resources.");
                        element[property.Key] = image;
                    }
                }
                else Expand(property.Value, content, images, ref remaining);
            }
        }
    }
}
