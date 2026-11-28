using System.Globalization;
using OpenClawTray.Chat.LobsterVisits;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Parity for the composer lobster's visit lifecycle. Each Goldens trace in LobsterVisitGoldens.g.cs was
/// recorded by scripts/Update-LobsterVisitGoldens.ps1, which executes the pinned upstream LobsterPetOwner
/// (ui/src/components/lobster-pet-owner.ts) under Node with DOM-free stubs, fake timers ordered by due time then
/// scheduling order, an empty Lobsterdex, a local start of 2026-07-15 12:00, and ledge traffic and geometry
/// measurement neutralized. Seeds avoid molt, twin and Elder loads, which this port does not include. The same
/// step strings drive both runs. A line is recorded whenever the projected state changes: elapsed ms, presence,
/// act, spot and travel coordinates in hundredths, facing, anchor, entrance while entering, vigil, grumpy and mode.
/// </summary>
public sealed partial class LobsterVisitOwnerTests
{
    private static readonly DateTime Start = new(2026, 7, 15, 12, 0, 0);
    private static readonly LobsterScene Hub = new(new LobsterLane(296, 649, 0), null, null);
    private static readonly LobsterScene Roomy = new(new LobsterLane(66, 734, 0), new LobsterLane(150, 394, 107), (366, 394));

    private sealed record Setup(
        uint Seed,
        LobsterVisitMode Mode = LobsterVisitMode.Idle,
        bool FloorEnabled = false,
        bool Roomy = false,
        int Visits = 0,
        int Shoos = 0,
        bool ReducedMotion = false,
        bool PresenceOnly = false);

    private sealed record Golden(Setup Setup, string[] Steps, string Expected);

    public static TheoryData<string> UpstreamScenarios()
    {
        var data = new TheoryData<string>();
        foreach (var name in Goldens.Keys)
            data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(UpstreamScenarios))]
    public void ReproducesUpstreamVisitTrace(string scenario)
    {
        var golden = Goldens[scenario];
        var trace = Run(golden.Setup, golden.Steps);
        Assert.Equal(golden.Expected.ReplaceLineEndings("\n"), string.Join("\n", trace));
    }

    [Fact]
    public void PermanentDismissal_DisablesVisitsOnceAndRecordsTheShoo()
    {
        var harness = new Harness(new Setup(42));
        var disabled = 0;
        harness.Owner.VisitsDisabled += () => disabled++;
        harness.Scheduler.Advance(8000);
        Assert.Equal(LobsterPresence.In, harness.Owner.Snapshot()!.Presence);
        Assert.True(harness.Owner.OpenMenu());
        Assert.True(harness.Owner.Snapshot()!.MenuOpen);

        harness.Owner.Dismiss(permanently: true);

        Assert.Equal(1, disabled);
        Assert.Equal(1, harness.Journal.Arrivals);
        Assert.Equal(1, harness.Journal.Shoos);
        Assert.False(harness.Owner.Snapshot()!.MenuOpen);
        Assert.False(harness.Owner.OpenMenu());
    }

    [Fact]
    public void LeavingClosesAnOpenMenu()
    {
        var harness = new Harness(new Setup(7, Mode: LobsterVisitMode.Offline));
        Assert.True(harness.Owner.OpenMenu());

        harness.Update(harness.Inputs with { Mode = LobsterVisitMode.Idle });
        harness.Scheduler.Advance(2000);

        var snapshot = harness.Owner.Snapshot()!;
        Assert.Equal(LobsterPresence.Out, snapshot.Presence);
        Assert.False(snapshot.MenuOpen);
    }

    [Fact]
    public void Dispose_CancelsEveryPendingTimer()
    {
        var harness = new Harness(new Setup(42, Mode: LobsterVisitMode.Busy));
        harness.Scheduler.Advance(8000);
        harness.Owner.HandleHoldStart();
        harness.Owner.HandleHoldEnd();
        harness.Owner.HandleHoldStart();
        Assert.True(harness.Scheduler.Pending > 0);

        harness.Owner.Dispose();

        Assert.Equal(0, harness.Scheduler.Pending);
    }

    // Seed 42's first act is queued for 11.2 s and its first walk starts at 20.2 s in the upstream trace.
    [Theory]
    [InlineData(8000, true)]
    [InlineData(20_500, false)]
    public void EnablingReducedMotionMidVisit_StopsQueuedActsWalksAndPets(double enableAt, bool holdPending)
    {
        var harness = new Harness(new Setup(42));
        harness.Scheduler.Advance(enableAt);
        if (holdPending)
            harness.Owner.HandleHoldStart();
        Assert.Equal(holdPending ? null : (LobsterAct?)LobsterAct.Scuttle, harness.Owner.Snapshot()!.Act);

        harness.Owner.SetReducedMotion(true);
        AssertStill(harness.Owner.Snapshot()!);
        harness.Acts.Clear();
        harness.Scheduler.Advance(60_000);

        AssertStill(harness.Owner.Snapshot()!);
        Assert.DoesNotContain(harness.Acts, act => act is not null);

        static void AssertStill(LobsterVisitSnapshot snapshot)
        {
            Assert.Equal(LobsterPresence.In, snapshot.Presence);
            Assert.Null(snapshot.Act);
            Assert.Null(snapshot.Travel);
            Assert.False(snapshot.Entering);
        }
    }

    [Fact]
    public void HiddenHost_AdmitsNoActsUntilShownAgain()
    {
        var harness = new Harness(new Setup(42));
        harness.Scheduler.Advance(8000);
        harness.Owner.HandleHoldStart();
        harness.Owner.SetHostVisible(false);
        harness.Acts.Clear();

        harness.Update(harness.Inputs with { Mode = LobsterVisitMode.Busy });
        harness.Scheduler.Advance(30_000);

        Assert.DoesNotContain(harness.Acts, act => act is not null);
        Assert.Equal(LobsterPresence.In, harness.Owner.Snapshot()!.Presence);

        harness.Owner.SetHostVisible(true);
        harness.Scheduler.Advance(30_000);

        Assert.Contains(harness.Acts, act => act is not null);
    }

    [Fact]
    public void ReplacingTheSeed_CancelsAPendingPet()
    {
        var harness = new Harness(new Setup(7, Mode: LobsterVisitMode.Offline));
        Assert.Equal(LobsterPresence.In, harness.Owner.Snapshot()!.Presence);
        harness.Owner.HandleHoldStart();

        harness.Update(harness.Inputs with { Seed = 42 });
        harness.Scheduler.Advance(2000);

        Assert.Equal(LobsterPresence.In, harness.Owner.Snapshot()!.Presence);
        Assert.DoesNotContain(harness.Acts, act => act == LobsterAct.Pet);
    }

    // Windows can turn animation effects back on while a visitor sits still; upstream never sees that.
    [Fact]
    public void TurningReducedMotionOff_ResumesTheActLoop()
    {
        var harness = new Harness(new Setup(42, ReducedMotion: true));
        harness.Scheduler.Advance(60_000);
        Assert.DoesNotContain(harness.Acts, act => act is not null);
        harness.Acts.Clear();

        harness.Owner.SetReducedMotion(false);
        harness.Scheduler.Advance(7500);

        Assert.Contains(harness.Acts, act => act is not null);
    }

    [Fact]
    public void OfflineTracker_IgnoresBlipsAndClearsImmediatelyOnReconnect()
    {
        var scheduler = new FakeScheduler(Start);
        using var tracker = new LobsterOfflineTracker(scheduler);
        var changes = 0;
        tracker.Changed += () => changes++;

        tracker.Update(connected: false);
        scheduler.Advance(LobsterOfflineTracker.DelayMs - 1);
        tracker.Update(connected: true);
        scheduler.Advance(10_000);
        Assert.False(tracker.OfflineStable);
        Assert.Equal(0, changes);

        tracker.Update(connected: false);
        scheduler.Advance(1000);
        tracker.Update(connected: false);
        scheduler.Advance(LobsterOfflineTracker.DelayMs - 1000);
        Assert.True(tracker.OfflineStable);

        tracker.Update(connected: true);
        Assert.False(tracker.OfflineStable);
        Assert.Equal(2, changes);
    }

    private static List<string> Run(Setup setup, IEnumerable<string> steps)
    {
        var harness = new Harness(setup);
        foreach (var step in steps)
            harness.Step(step);
        harness.Owner.Dispose();
        return harness.Trace;
    }

    private sealed class Harness
    {
        private readonly Setup _setup;
        private string _last = "";
        private LobsterPresence? _lastPresence;

        internal Harness(Setup setup)
        {
            _setup = setup;
            Scheduler = new FakeScheduler(Start);
            Journal = new FakeJournal(new LobsterFamiliarity(setup.Visits, setup.Shoos));
            Owner = new LobsterVisitOwner(Scheduler, Journal);
            Owner.UpdateScene(setup.Roomy ? Roomy : Hub);
            Owner.SetReducedMotion(setup.ReducedMotion);
            Owner.Changed += Record;
            Inputs = new LobsterVisitInputs(setup.Seed, setup.Mode, FloorEnabled: setup.FloorEnabled);
            // The host owns the setting; upstream's host turns its visits input off the same way.
            Owner.VisitsDisabled += () => Inputs = Inputs with { VisitsEnabled = false };
            Owner.Connect(Inputs);
        }

        internal FakeScheduler Scheduler { get; }
        internal FakeJournal Journal { get; }
        internal LobsterVisitOwner Owner { get; }
        internal LobsterVisitInputs Inputs { get; private set; }
        internal List<string> Trace { get; } = [];
        internal List<LobsterAct?> Acts { get; } = [];

        internal void Update(LobsterVisitInputs inputs)
        {
            Inputs = inputs;
            Owner.Update(inputs);
        }

        internal void Step(string step)
        {
            var parts = step.Split(' ');
            switch (parts[0])
            {
                case "advance": Scheduler.Advance(double.Parse(parts[1], CultureInfo.InvariantCulture)); break;
                case "poke": Owner.HandleHoldStart(); Owner.HandleHoldEnd(); break;
                case "holdStart": Owner.HandleHoldStart(); break;
                case "holdEnd": Owner.HandleHoldEnd(); break;
                case "holdCancel": Owner.HandleHoldCancel(); break;
                case "dismiss": Owner.Dismiss(parts[1] == "permanently"); break;
                case "hidden": Owner.SetHostVisible(parts[1] != "1"); break;
                case "gaze": Owner.HandlePointerMoved(double.Parse(parts[1], CultureInfo.InvariantCulture), double.Parse(parts[2], CultureInfo.InvariantCulture)); break;
                case "update":
                    var next = Inputs;
                    foreach (var pair in parts.Skip(1))
                    {
                        var (key, value) = (pair.Split('=')[0], pair.Split('=')[1]);
                        next = key switch
                        {
                            "mode" => next with { Mode = Enum.Parse<LobsterVisitMode>(value, ignoreCase: true) },
                            "outcome" => next with { RunOutcome = Enum.Parse<LobsterRunOutcome>(value, ignoreCase: true) },
                            "floor" => next with { FloorEnabled = value == "1" },
                            "visits" => next with { VisitsEnabled = value == "1" },
                            _ => throw new ArgumentException(pair),
                        };
                    }
                    Update(next);
                    break;
                default: throw new ArgumentException(step);
            }
        }

        private void Record()
        {
            var snapshot = Owner.Snapshot()!;
            Acts.Add(snapshot.Act);
            var at = (long)Math.Floor(Scheduler.ElapsedMs);
            if (_setup.PresenceOnly)
            {
                if (snapshot.Presence != _lastPresence)
                {
                    _lastPresence = snapshot.Presence;
                    Trace.Add($"{at} {Lower(snapshot.Presence)}");
                }
                return;
            }
            var line = Project(snapshot);
            if (line != _last)
            {
                _last = line;
                Trace.Add($"{at} {line}");
            }
        }

        private static string Project(LobsterVisitSnapshot s)
        {
            static long Hundredths(double value) => (long)Math.Floor(value * 100);
            var travel = s.Travel is { } t
                ? $"{(t.Hop ? "h" : "w")}:{Hundredths(t.From.X)},{Hundredths(t.From.Y)}>{Hundredths(t.To.X)},{Hundredths(t.To.Y)}"
                : "-";
            return $"{Lower(s.Presence)} act={(s.Act is { } act ? Lower(act) : "-")} spot={Hundredths(s.SpotPct)} " +
                $"face={s.Facing} {Lower(s.Anchor)} enter={(s.Entering ? Lower(s.Entrance) : "-")} " +
                $"vigil={(s.Vigil ? 1 : 0)} grumpy={(s.Grumpy ? 1 : 0)} travel={travel} mode={Lower(s.Mode)}";
        }

        private static string Lower<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();
    }

    /// <summary>Fires due callbacks in due-time then scheduling order, advancing the clock to each.</summary>
    internal sealed class FakeScheduler(DateTime start) : ILobsterVisitScheduler
    {
        private sealed class Entry(FakeScheduler owner, double due, long sequence, Action callback) : IDisposable
        {
            internal double Due { get; } = due;
            internal long Sequence { get; } = sequence;
            internal Action Callback { get; } = callback;
            public void Dispose() => owner._entries.Remove(this);
        }

        private readonly List<Entry> _entries = [];
        private long _sequence;

        public double ElapsedMs { get; private set; }
        public DateTime LocalNow => start.AddMilliseconds(ElapsedMs);
        internal int Pending => _entries.Count;

        public IDisposable Schedule(double delayMs, Action callback)
        {
            var entry = new Entry(this, ElapsedMs + Math.Max(0, delayMs), _sequence++, callback);
            _entries.Add(entry);
            return entry;
        }

        internal void Advance(double ms)
        {
            var end = ElapsedMs + ms;
            while (_entries.OrderBy(e => e.Due).ThenBy(e => e.Sequence).FirstOrDefault() is { } next && next.Due <= end)
            {
                _entries.Remove(next);
                ElapsedMs = next.Due;
                next.Callback();
            }
            ElapsedMs = end;
        }
    }

    internal sealed class FakeJournal(LobsterFamiliarity familiarity) : ILobsterVisitJournal
    {
        internal int Arrivals { get; private set; }
        internal int Shoos { get; private set; }
        public LobsterFamiliarity ReadFamiliarity() => familiarity;
        public void RecordArrival() => Arrivals++;
        public void RecordShoo() => Shoos++;
    }
}
