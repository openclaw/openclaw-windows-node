// Device-local familiarity counters for composer lobster visits: lifetime arrivals and shoos, the Hub's counterpart
// of the Control UI's per-browser localStorage record (openclaw.control.lobsterpet.familiarity.v1). Visits warm
// visitors from shy to friend; frequent shooing makes them wary. Reads and writes are best-effort: an unreadable or
// unwritable file never breaks a visit, as upstream's storage helpers guarantee.
// OpenClaw 4cffd43ee043275f6a4c693aaacee8d8e5a61288: ui/src/components/lobster-dex.ts (MIT; see THIRD_PARTY_NOTICES.md).
//
// Usage:
//   var journal = LobsterVisitJournalFile.For(Path.Combine(dataDir, LobsterVisitJournalFile.FileName));
//   var owner = new LobsterVisitOwner(scheduler, journal);   // the owner reads and records through the journal
using System.Text.Json;

namespace OpenClawTray.Chat.LobsterVisits;

internal sealed class LobsterVisitJournalFile : ILobsterVisitJournal
{
    internal const string FileName = "lobster-visits.json";

    private static readonly Dictionary<string, LobsterVisitJournalFile> s_shared = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    private readonly Action<Action> _queueWrite;
    private readonly object _gate = new();
    private (int Visits, int Shoos)? _counters;

    /// <param name="queueWrite">Runs a save; production saves on the thread pool, off the UI thread.</param>
    internal LobsterVisitJournalFile(string path, Action<Action>? queueWrite = null)
    {
        _path = path;
        _queueWrite = queueWrite ?? (save => _ = Task.Run(save));
    }

    /// <summary>
    /// One journal per file per process, so every composer sees the same counts. Upstream shares one storage record
    /// across all composers in a browser profile.
    /// </summary>
    internal static LobsterVisitJournalFile For(string path)
    {
        lock (s_shared)
        {
            if (!s_shared.TryGetValue(path, out var journal))
                s_shared[path] = journal = new LobsterVisitJournalFile(path);
            return journal;
        }
    }

    public LobsterFamiliarity ReadFamiliarity()
    {
        lock (_gate)
        {
            var (visits, shoos) = Load();
            return new LobsterFamiliarity(visits, shoos);
        }
    }

    public void RecordArrival() => Bump(visits: 1, shoos: 0);

    public void RecordShoo() => Bump(visits: 0, shoos: 1);

    private void Bump(int visits, int shoos)
    {
        lock (_gate)
        {
            var current = Load();
            _counters = (current.Visits + visits, current.Shoos + shoos);
        }
        // Each write saves the latest counters, so racing background writes still converge on the newest values.
        _queueWrite(Save);
    }

    private (int Visits, int Shoos) Load()
    {
        if (_counters is { } cached)
            return cached;
        var counters = (0, 0);
        try
        {
            if (File.Exists(_path))
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(_path));
                counters = (Count(document.RootElement, "visits"), Count(document.RootElement, "shoos"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Upstream readFamiliarityCounters falls back to a fresh record.
        }
        _counters = counters;
        return counters;
    }

    // Upstream accepts only non-negative numbers and treats anything else as zero.
    private static int Count(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
        && number >= 0
            ? (int)Math.Min(number, int.MaxValue)
            : 0;

    private void Save()
    {
        lock (_gate)
        {
            if (_counters is not { } counters)
                return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temporary = _path + ".tmp";
                using (var stream = File.Create(temporary))
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("visits", counters.Visits);
                    writer.WriteNumber("shoos", counters.Shoos);
                    writer.WriteEndObject();
                }
                File.Move(temporary, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort, like upstream: a full or blocked store must not break visits.
            }
        }
    }
}
