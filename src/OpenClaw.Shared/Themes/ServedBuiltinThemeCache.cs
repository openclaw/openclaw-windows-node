using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.Shared.Themes;

/// <summary>A cached probe of one Gateway build, with the built-in descriptors it listed when they were known.</summary>
public sealed record ServedThemeCacheEntry(string Key, ThemeProbeResult Probe, ImmutableArray<GatewayThemeDescriptor>? Descriptors)
{
    /// <summary>
    /// False when a theme stylesheet could not be read (<see cref="ServedThemeStylesheets.MissingThemeIds"/>). Such a
    /// result is used for its connection but never cached, so a failed read is retried on the next connection.
    /// </summary>
    public bool Complete { get; init; } = true;

    /// <summary>Every id the probe attempted, resolved or not, so a later request for the same ids needs no new probe.</summary>
    public IEnumerable<string> AttemptedIds => Probe.Result.EnumerateObject().Select(theme => theme.Name);

    public ServedBuiltinThemeSet ToSet() => ServedBuiltinThemeSet.Build(Key, Probe, Descriptors);
}

/// <summary>Probe results persisted per Gateway build (<see cref="ServedBuiltinThemeCache"/>).</summary>
public interface IServedBuiltinThemeCache
{
    ServedThemeCacheEntry? TryLoad(string key);

    /// <summary>The entry used most recently, for applying the last Gateway's palettes before it reconnects.</summary>
    ServedThemeCacheEntry? TryLoadLast();

    /// <summary>Stores or replaces <paramref name="entry"/> and makes it the last used. An incomplete entry is not stored.</summary>
    void Save(ServedThemeCacheEntry entry);
}

/// <summary>
/// Persists probe results per Gateway build so a reconnect to the same build, or a start while it is offline, applies
/// its palettes without running WebView2 again. One small JSON file holds at most <see cref="MaxEntries"/> builds,
/// least recently used dropped first, and records the last one used for an offline start. Entries from a different
/// <see cref="ThemeProbe.ScriptHash"/> or schema are ignored, and an unreadable file counts as empty: the cache can only
/// skip work, never decide what is shown.
/// <example><code>
/// var cache = new ServedBuiltinThemeCache(Path.Combine(localData, "Themes", "gateway-builtin-themes.json"));
/// if (cache.TryLoad(sheets.Key) is { } hit) Publish(hit.ToSet());
/// cache.Save(new ServedThemeCacheEntry(sheets.Key, probe, descriptors));
/// </code></example>
/// </summary>
public sealed class ServedBuiltinThemeCache(string filePath) : IServedBuiltinThemeCache
{
    public const int MaxEntries = 8;
    public const int MaxFileBytes = 4 * 1024 * 1024;
    // 2: entries carry the probe's failures, so a theme it rejected stays rejected after reloading.
    private const int Schema = 2;

    private readonly object _gate = new();

    public ServedThemeCacheEntry? TryLoad(string key)
    {
        lock (_gate)
            return Read() is { } file ? Find(file, key) : null;
    }

    /// <inheritdoc/>
    public ServedThemeCacheEntry? TryLoadLast()
    {
        lock (_gate)
            return Read() is { } file && Text(file["last"]) is { } key ? Find(file, key) : null;
    }

    /// <inheritdoc/>
    public void Save(ServedThemeCacheEntry entry)
    {
        if (!entry.Complete)
            return;
        lock (_gate)
        {
            var file = Read() ?? [];
            var entries = file["entries"] as JsonArray ?? [];
            var sequence = entries.Select(Sequence).DefaultIfEmpty(0).Max() + 1;
            var kept = entries
                .OfType<JsonObject>()
                .Where(item => Text(item["key"]) != entry.Key)
                .OrderByDescending(Sequence)
                .Take(MaxEntries - 1)
                .Select(item => item.DeepClone())
                .ToList();
            var stored = new JsonObject
            {
                ["key"] = entry.Key,
                ["seq"] = sequence,
                ["result"] = JsonNode.Parse(entry.Probe.Result.GetRawText()),
                ["failures"] = new JsonArray([.. entry.Probe.Failures.Select(failure => (JsonNode)failure)]),
                ["builtins"] = entry.Descriptors is { } descriptors
                    ? new JsonArray([.. descriptors.Select(ToWire)])
                    : null,
            };
            Write(new JsonObject
            {
                ["schema"] = Schema,
                ["probe"] = ThemeProbe.ScriptHash,
                ["last"] = entry.Key,
                ["entries"] = new JsonArray([stored, .. kept]),
            });
        }
    }

    private JsonObject? Read()
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length > MaxFileBytes)
                return null;
            var file = JsonNode.Parse(File.ReadAllText(filePath)) as JsonObject;
            return file is not null && file["schema"] is JsonValue schema && schema.TryGetValue<int>(out var version) && version == Schema &&
                Text(file["probe"]) == ThemeProbe.ScriptHash
                ? file
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private void Write(JsonObject file)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var temporary = filePath + ".tmp";
        File.WriteAllText(temporary, file.ToJsonString());
        File.Move(temporary, filePath, overwrite: true);
    }

    private static ServedThemeCacheEntry? Find(JsonObject file, string key)
    {
        try
        {
            var item = (file["entries"] as JsonArray)?.OfType<JsonObject>()
                .FirstOrDefault(candidate => Text(candidate["key"]) == key);
            if (item?["result"] is not JsonObject result || item["failures"] is not JsonArray failures)
                return null;
            using var document = JsonDocument.Parse(result.ToJsonString());
            // An unstyled theme still has complete (base palette) values in the result; only its failures reject it.
            var probe = ThemeProbe.ParseResult(document.RootElement, [.. failures.Select(Text).OfType<string>()]);
            ImmutableArray<GatewayThemeDescriptor>? descriptors = null;
            if (item["builtins"] is JsonArray builtins)
            {
                var parsed = ImmutableArray.CreateBuilder<GatewayThemeDescriptor>();
                foreach (var wire in builtins.OfType<JsonObject>())
                {
                    using var descriptor = JsonDocument.Parse(wire.ToJsonString());
                    if (GatewayThemeProtocol.TryParseDescriptor(descriptor.RootElement, out var value) &&
                        value.Source == GatewayThemeSource.Builtin)
                        parsed.Add(value);
                }
                descriptors = parsed.ToImmutable();
            }
            return new ServedThemeCacheEntry(key, probe, descriptors);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    // Malformed entries (valid JSON, wrong shape) count as sequence 0 and are dropped on the next save.
    private static long Sequence(JsonNode? item) =>
        item is JsonObject entry && entry["seq"] is JsonValue value && value.TryGetValue<long>(out var sequence) ? sequence : 0;

    // The themes.list descriptor shape, so loading reuses the wire validation.
    private static JsonObject ToWire(GatewayThemeDescriptor descriptor) => new()
    {
        ["id"] = descriptor.Id,
        ["name"] = descriptor.Name,
        ["description"] = descriptor.Description,
        ["source"] = "builtin",
        ["modes"] = new JsonArray([.. descriptor.Modes.Select(mode => (JsonNode)(mode == ThemeColorMode.Light ? "light" : "dark"))]),
        ["mascot"] = descriptor.Branding.MascotText,
        ["brandIcon"] = descriptor.Branding.BrandIcon,
        ["workingIndicator"] = descriptor.Branding.WorkingIndicatorText,
    };
}
