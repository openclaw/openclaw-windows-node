using System.Text.Json;

namespace OpenClaw.SetupEngine;

// ─── Transaction Journal (crash recovery + forensics) ───

public sealed class TransactionJournal : IDisposable
{
    private readonly StreamWriter? _writer;
    private readonly List<JournalEntry> _entries = new();
    private readonly object _lock = new();

    public IReadOnlyList<JournalEntry> Entries => _entries;
    public string? FilePath { get; }

    public TransactionJournal(string? filePath)
    {
        FilePath = filePath;
        if (filePath != null)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (dir != null) Directory.CreateDirectory(dir);
            LoadExistingEntries(filePath);
            _writer = new StreamWriter(filePath, append: true) { AutoFlush = true };
        }
    }

    public void RecordStepStarted(string stepId)
    {
        var entry = new JournalEntry(DateTimeOffset.UtcNow, stepId, "started");
        Append(entry);
    }

    public void RecordStepCompleted(string stepId, StepOutcome outcome, TimeSpan elapsed, string? message = null)
    {
        var entry = new JournalEntry(DateTimeOffset.UtcNow, stepId, "completed", outcome.ToString(), elapsed, message);
        Append(entry);
    }

    public void RecordRollback(string stepId, bool success)
    {
        var entry = new JournalEntry(DateTimeOffset.UtcNow, stepId, success ? "rollback_ok" : "rollback_failed");
        Append(entry);
    }

    public void RecordPipelineEvent(string eventName, string? detail = null)
    {
        var entry = new JournalEntry(DateTimeOffset.UtcNow, "_pipeline", eventName, Detail: detail);
        Append(entry);
    }

    /// <summary>
    /// Get the last completed step (for resume support).
    /// </summary>
    public string? LastCompletedStepId()
    {
        lock (_lock)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].Event == "completed" && _entries[i].Outcome is "Success" or "Skipped")
                    return _entries[i].StepId;
            }
        }
        return null;
    }

    private void Append(JournalEntry entry)
    {
        lock (_lock)
        {
            _entries.Add(entry);
            try
            {
                var json = JsonSerializer.Serialize(entry, _jsonOptions);
                _writer?.WriteLine(json);
            }
            catch (IOException)
            {
                // Journal write failure is non-fatal — entries are still in memory
            }
        }
    }

    private void LoadExistingEntries(string filePath)
    {
        if (!File.Exists(filePath))
            return;

        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var entry = JsonSerializer.Deserialize<JournalEntry>(line, _jsonOptions);
                if (entry != null)
                    _entries.Add(entry);
            }
            catch (JsonException)
            {
                // Preserve the journal file as crash evidence even if one line is corrupt.
            }
        }
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public void Dispose() => _writer?.Dispose();
}

public sealed record JournalEntry(
    DateTimeOffset Timestamp,
    string StepId,
    string Event,
    string? Outcome = null,
    TimeSpan? Elapsed = null,
    string? Detail = null);
