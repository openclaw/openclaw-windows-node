using System.Text.Json;

namespace OpenClaw.SetupEngine;

// ─── Transaction Journal (crash recovery + forensics) ───

public sealed class TransactionJournal : IDisposable
{
    private readonly StreamWriter? _writer;
    private readonly List<JournalEntry> _entries = new();
    private readonly object _lock = new();
    private readonly SetupLogger? _logger;

    public IReadOnlyList<JournalEntry> Entries
    {
        get
        {
            lock (_lock)
                return _entries.ToArray();
        }
    }
    public string? FilePath { get; }

    public TransactionJournal(string? filePath, SetupLogger? logger = null)
    {
        FilePath = filePath;
        _logger = logger;
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
        var entry = new JournalEntry(DateTimeOffset.UtcNow, stepId, "completed", outcome.ToString(), elapsed, message is null ? null : SetupLogger.Sanitize(message));
        Append(entry);
    }

    public void RecordRollback(string stepId, bool success)
    {
        var entry = new JournalEntry(DateTimeOffset.UtcNow, stepId, success ? "rollback_ok" : "rollback_failed");
        Append(entry);
    }

    public void RecordPipelineEvent(string eventName, string? detail = null)
    {
        var entry = new JournalEntry(DateTimeOffset.UtcNow, "_pipeline", eventName, Detail: detail is null ? null : SetupLogger.Sanitize(detail));
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
        IOException? writeFailure = null;
        lock (_lock)
        {
            _entries.Add(entry);
            try
            {
                var json = JsonSerializer.Serialize(entry, _jsonOptions);
                _writer?.WriteLine(json);
            }
            catch (IOException ex)
            {
                writeFailure = ex;
            }
        }

        if (writeFailure != null)
            _logger?.Warn("transaction journal write failed; entries remain in memory", new { file_path = FilePath, error = writeFailure.Message });
    }

    private void LoadExistingEntries(string filePath)
    {
        if (!File.Exists(filePath))
            return;

        var lineNumber = 0;
        foreach (var line in File.ReadLines(filePath))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var entry = JsonSerializer.Deserialize<JournalEntry>(line, _jsonOptions);
                if (entry != null)
                    _entries.Add(entry);
            }
            catch (JsonException ex)
            {
                _logger?.Warn("transaction journal line is corrupt and was skipped", new { file_path = filePath, line = lineNumber, error = ex.Message });
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
