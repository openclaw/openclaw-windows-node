using OpenClaw.Chat;
using OpenClawTray.Chat.LobsterVisits;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// The Hub's glue around the ported visit logic: which chat state makes the visitor busy or react, when the
/// new-session composer keeps its visitor through a disconnect, and the device-local familiarity journal.
/// </summary>
public sealed class LobsterComposerVisitsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openclaw-lobster-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static ChatThread Thread(string id, string? status = null, bool? hasActiveRun = null, long? endedAt = null) =>
        new() { Id = id, Title = id, RunStatus = status, HasActiveRun = hasActiveRun, EndedAt = endedAt };

    [Fact]
    public void Runs_UseTheGatewayLiveRunFlagAndEndTimes()
    {
        // A session with no status but a live run keeps the visitor busy, as upstream's hasActiveRun does.
        var busy = LobsterComposerVisits.Runs([Thread("a", hasActiveRun: true), Thread("b", "done")]);
        Assert.Equal(LobsterVisitMode.Busy, LobsterVisitPlans.ResolveMode(connected: true, busy));

        // A terminal status outranks a stale live-run flag.
        var stale = LobsterComposerVisits.Runs([Thread("a", "done", hasActiveRun: true)]);
        Assert.Equal(LobsterVisitMode.Idle, LobsterVisitPlans.ResolveMode(connected: true, stale));

        // The most recently ended run decides the reaction, whichever session it belongs to.
        var outcome = LobsterComposerVisits.Runs([Thread("a", "done", endedAt: 10), Thread("b", "failed", endedAt: 20)]);
        Assert.Equal(LobsterRunOutcome.Error, LobsterVisitPlans.ResolveOutcome(outcome));
    }

    [Fact]
    public void Seed_ChangesWithTheOpeningAndTheSession()
    {
        var first = LobsterComposerVisits.Seed("0.25", "agent:main:one");
        Assert.Equal(first, LobsterComposerVisits.Seed("0.25", "agent:main:one"));
        Assert.NotEqual(first, LobsterComposerVisits.Seed("0.5", "agent:main:one"));
        Assert.NotEqual(first, LobsterComposerVisits.Seed("0.25", "agent:main:two"));
    }

    [Fact]
    public void Visitor_StaysForTheEmptySessionThroughADisconnect()
    {
        // The new-session composer shows it and remembers the session.
        var (show, sticky) = LobsterComposerVisits.ShowVisitor(null, newSessionComposer: true, "s1", targetIsEmpty: true);
        Assert.True(show);

        // Disconnected: the Hub hides its new-session chrome, but the same empty session keeps its visitor.
        (show, sticky) = LobsterComposerVisits.ShowVisitor(sticky, newSessionComposer: false, "s1", targetIsEmpty: true);
        Assert.True(show);
        Assert.Equal("s1", sticky);

        // Sending makes the session non-empty and ends the visit for good.
        (show, sticky) = LobsterComposerVisits.ShowVisitor(sticky, newSessionComposer: false, "s1", targetIsEmpty: false);
        Assert.False(show);
        (show, _) = LobsterComposerVisits.ShowVisitor(sticky, newSessionComposer: false, "s1", targetIsEmpty: true);
        Assert.False(show);
    }

    [Fact]
    public void Visitor_NeverStaysForAnotherSession()
    {
        var (_, sticky) = LobsterComposerVisits.ShowVisitor(null, newSessionComposer: true, "s1", targetIsEmpty: true);

        Assert.False(LobsterComposerVisits.ShowVisitor(sticky, newSessionComposer: false, "s2", targetIsEmpty: true).Show);
    }

    [Fact]
    public void Journal_CountsArrivalsAndShoosAcrossInstances()
    {
        var path = Path.Combine(_directory, LobsterVisitJournalFile.FileName);
        var journal = new LobsterVisitJournalFile(path, save => save());
        Assert.Equal(LobsterFamiliarity.New, journal.ReadFamiliarity());

        for (var i = 0; i < 15; i++)
            journal.RecordArrival();
        for (var i = 0; i < 6; i++)
            journal.RecordShoo();

        // A fresh process reads the persisted record: a friend that has been shooed often enough to be wary.
        var reread = new LobsterVisitJournalFile(path, save => save()).ReadFamiliarity();
        Assert.Equal(new LobsterFamiliarity(15, 6), reread);
        Assert.Equal(LobsterFamiliarityTier.Friend, reread.Tier);
        Assert.True(reread.Wary);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"visits":"many","shoos":-2}""")]
    [InlineData("[1,2]")]
    public void Journal_TreatsAMalformedRecordAsFresh(string content)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, LobsterVisitJournalFile.FileName);
        File.WriteAllText(path, content);
        var journal = new LobsterVisitJournalFile(path, save => save());

        Assert.Equal(LobsterFamiliarity.New, journal.ReadFamiliarity());
        journal.RecordArrival();
        Assert.Equal(new LobsterFamiliarity(1, 0), new LobsterVisitJournalFile(path, save => save()).ReadFamiliarity());
    }

    [Fact]
    public void Journal_KeepsCountingWhenItsFileCannotBeWritten()
    {
        // A directory where the file should be makes every write fail; visits must carry on in memory.
        var path = Path.Combine(_directory, LobsterVisitJournalFile.FileName);
        Directory.CreateDirectory(path);
        var journal = new LobsterVisitJournalFile(path, save => save());

        journal.RecordArrival();
        journal.RecordArrival();

        Assert.Equal(new LobsterFamiliarity(2, 0), journal.ReadFamiliarity());
    }
}
