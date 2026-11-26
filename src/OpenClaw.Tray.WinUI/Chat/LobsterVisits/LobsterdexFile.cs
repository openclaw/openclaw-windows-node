// Profile-local palette sightings, independent from the visit/shoo counters.
// Use ForProfile(dataDirectory).RecordAsync(...) on display and ReadAsync() when opening the collection.
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClawTray.Chat.LobsterVisits;

internal sealed record LobsterSighting(
    [property: JsonRequired] string Name,
    [property: JsonRequired] DateTimeOffset FirstSeenAt,
    DateTimeOffset? ShinySeenAt);

/// <summary>Serializes reads and atomic updates to an individual Hub profile's collection.</summary>
internal sealed class LobsterdexFile(string path)
{
    internal const string FileName = "lobsterdex.json";
    private static readonly ConcurrentDictionary<string, LobsterdexFile> Profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1);

    internal static LobsterdexFile ForProfile(string dataDirectory) =>
        Profiles.GetOrAdd(CollectionPath(dataDirectory), static path => new(path));

    internal static Task DrainProfileAsync(string dataDirectory) =>
        Profiles.TryGetValue(CollectionPath(dataDirectory), out var store) ? store.DrainAsync() : Task.CompletedTask;

    internal event Action? Changed;

    private static string CollectionPath(string directory) => Path.GetFullPath(Path.Combine(directory, FileName));

    /// <summary>Returns a snapshot by palette ID. Missing storage is an empty collection; unreadable storage is an error.</summary>
    internal Task<Dictionary<string, LobsterSighting>> ReadAsync() => RunAsync(Read);

    /// <summary>Preserves the first visit and separately records the first shiny sighting for a palette.</summary>
    internal async Task RecordAsync(string paletteId, string name, bool shiny, DateTimeOffset seenAt)
    {
        var changed = await RunAsync(() =>
        {
            var entries = Read();
            if (!entries.TryGetValue(paletteId, out var first))
                entries.Add(paletteId, new(name, seenAt, shiny ? seenAt : null));
            else if (shiny && first.ShinySeenAt is null)
                entries[paletteId] = first with { ShinySeenAt = seenAt };
            else
                return false;

            Save(entries);
            return true;
        }).ConfigureAwait(false);
        if (changed)
            Changed?.Invoke();
    }

    private async Task DrainAsync()
    {
        // The app calls this after its windows close, when no composer can admit new writes.
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
    }

    private async Task<T> RunAsync<T>(Func<T> operation)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Collection I/O never runs on the WinUI dispatcher, even when the gate is idle.
            return await Task.Run(operation).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Dictionary<string, LobsterSighting> Read()
    {
        try
        {
            using var stream = File.OpenRead(path);
            var entries = JsonSerializer.Deserialize(stream, LobsterdexJsonContext.Default.DictionaryStringLobsterSighting);
            if (entries is null || entries.Any(entry => string.IsNullOrWhiteSpace(entry.Key)
                || string.IsNullOrWhiteSpace(entry.Value?.Name)))
                throw new JsonException("Invalid Lobsterdex collection.");
            return entries;
        }
        catch (FileNotFoundException)
        {
            return new(StringComparer.Ordinal);
        }
        catch (DirectoryNotFoundException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    private void Save(Dictionary<string, LobsterSighting> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        try
        {
            using (var stream = File.Create(temporary))
            {
                JsonSerializer.Serialize(stream, entries, LobsterdexJsonContext.Default.DictionaryStringLobsterSighting);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Dictionary<string, LobsterSighting>))]
internal sealed partial class LobsterdexJsonContext : JsonSerializerContext;
