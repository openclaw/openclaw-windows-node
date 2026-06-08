namespace OpenClaw.SetupEngine.Tests;

public class TransactionJournalTests : IDisposable
{
    private readonly string _tempDir;

    public TransactionJournalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"journal-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void RecordStepStarted_AddsEntry()
    {
        using var journal = new TransactionJournal(filePath: null);
        journal.RecordStepStarted("step-1");

        Assert.Single(journal.Entries);
        Assert.Equal("step-1", journal.Entries[0].StepId);
        Assert.Equal("started", journal.Entries[0].Event);
    }

    [Fact]
    public void RecordStepCompleted_AddsEntryWithOutcome()
    {
        using var journal = new TransactionJournal(filePath: null);
        journal.RecordStepCompleted("step-1", StepOutcome.Success, TimeSpan.FromSeconds(2), "ok");

        Assert.Single(journal.Entries);
        Assert.Equal("completed", journal.Entries[0].Event);
        Assert.Equal("Success", journal.Entries[0].Outcome);
    }

    [Fact]
    public void RecordRollback_AddsEntry()
    {
        using var journal = new TransactionJournal(filePath: null);
        journal.RecordRollback("step-1", success: true);

        Assert.Single(journal.Entries);
        Assert.Equal("rollback_ok", journal.Entries[0].Event);
    }

    [Fact]
    public void RecordRollback_Failure_AddsEntry()
    {
        using var journal = new TransactionJournal(filePath: null);
        journal.RecordRollback("step-1", success: false);

        Assert.Single(journal.Entries);
        Assert.Equal("rollback_failed", journal.Entries[0].Event);
    }

    [Fact]
    public void RecordPipelineEvent_AddsEntry()
    {
        using var journal = new TransactionJournal(filePath: null);
        journal.RecordPipelineEvent("pipeline_started", "steps=5");

        Assert.Single(journal.Entries);
        Assert.Equal("_pipeline", journal.Entries[0].StepId);
        Assert.Equal("pipeline_started", journal.Entries[0].Event);
        Assert.Equal("steps=5", journal.Entries[0].Detail);
    }

    [Fact]
    public void LastCompletedStepId_ReturnsLastSuccessful()
    {
        using var journal = new TransactionJournal(filePath: null);
        journal.RecordStepCompleted("step-1", StepOutcome.Success, TimeSpan.Zero);
        journal.RecordStepCompleted("step-2", StepOutcome.Success, TimeSpan.Zero);
        journal.RecordStepCompleted("step-3", StepOutcome.Failed, TimeSpan.Zero);

        Assert.Equal("step-2", journal.LastCompletedStepId());
    }

    [Fact]
    public void LastCompletedStepId_IncludesSkipped()
    {
        using var journal = new TransactionJournal(filePath: null);
        journal.RecordStepCompleted("step-1", StepOutcome.Success, TimeSpan.Zero);
        journal.RecordStepCompleted("step-2", StepOutcome.Skipped, TimeSpan.Zero);

        Assert.Equal("step-2", journal.LastCompletedStepId());
    }

    [Fact]
    public void LastCompletedStepId_NoCompletedSteps_ReturnsNull()
    {
        using var journal = new TransactionJournal(filePath: null);
        journal.RecordStepStarted("step-1");

        Assert.Null(journal.LastCompletedStepId());
    }

    [Fact]
    public void WritesToFile_WhenPathProvided()
    {
        var path = Path.Combine(_tempDir, "journal.jsonl");
        using (var journal = new TransactionJournal(path))
        {
            journal.RecordStepStarted("step-1");
            journal.RecordStepCompleted("step-1", StepOutcome.Success, TimeSpan.FromSeconds(1));
        }

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("step-1", lines[0]);
        Assert.Contains("started", lines[0]);
        Assert.Contains("completed", lines[1]);
    }

    [Fact]
    public void NullFilePath_WorksInMemoryOnly()
    {
        using var journal = new TransactionJournal(filePath: null);
        journal.RecordStepStarted("step-1");
        Assert.Single(journal.Entries);
        Assert.Null(journal.FilePath);
    }

    [Fact]
    public void WritesToFile_CreatesMissingDirectory()
    {
        var path = Path.Combine(_tempDir, "subdir", "journal.jsonl");
        using (var journal = new TransactionJournal(path))
        {
            journal.RecordStepStarted("step-1");
        }

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Constructor_LoadsExistingEntriesAndAppends()
    {
        var path = Path.Combine(_tempDir, "journal.jsonl");

        using (var journal = new TransactionJournal(path))
        {
            journal.RecordStepCompleted("step-1", StepOutcome.Success, TimeSpan.Zero);
        }

        using (var journal = new TransactionJournal(path))
        {
            Assert.Equal("step-1", journal.LastCompletedStepId());
            journal.RecordStepCompleted("step-2", StepOutcome.Success, TimeSpan.Zero);
        }

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("step-1", lines[0]);
        Assert.Contains("step-2", lines[1]);
    }

    [Fact]
    public void Constructor_IgnoresCorruptExistingLines()
    {
        var path = Path.Combine(_tempDir, "journal.jsonl");
        File.WriteAllLines(path, ["not json"]);

        using var journal = new TransactionJournal(path);

        journal.RecordStepStarted("step-1");
        Assert.Single(journal.Entries);
    }
}
