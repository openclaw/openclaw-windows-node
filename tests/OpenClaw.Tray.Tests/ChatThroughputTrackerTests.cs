using OpenClaw.TestSupport;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public class ChatThroughputTrackerTests
{
    private readonly ManualTimeProvider _clock = new();
    private readonly ChatThroughputTracker _tracker;

    public ChatThroughputTrackerTests() => _tracker = new ChatThroughputTracker(_clock);

    // Setup ops run before the first '|'; each later frame is "run:cumulativeLength", one second apart.
    [Theory]
    [InlineData("start|r1:5|r1:11", 11)]
    [InlineData("start;r1:100;reset|r1:20|r1:40", 40)] // saved text left the live buffer (empty reset unseen)
    [InlineData("r1:100;start|r1:104|r1:108", 8)] // late lifecycle start does not recount earlier text
    [InlineData("start;r1:50;reset|r2:10|r2:30", 30)] // another run starts from empty
    [InlineData("start;r1:5;end;reset|r1:20|r1:30", 10)] // missed start: first frame is only a baseline
    public void ObserveCumulativeText_CountsOnlyNewCharactersOfTheRun(string script, int expectedCharacters)
    {
        var parts = script.Split('|');
        foreach (var op in parts[0].Split(';'))
        {
            switch (op)
            {
                case "start": _tracker.StartRun("main"); break;
                case "end": _tracker.EndRun("main"); break;
                case "reset": _tracker.ResetSegment("main"); break;
                default: Observe(op); break;
            }
        }
        for (var i = 1; i < parts.Length; i++)
        {
            if (i > 1)
                _clock.Advance(TimeSpan.FromSeconds(1));
            Observe(parts[i]);
        }

        Assert.Equal(expectedCharacters / ChatThroughputTracker.CharactersPerToken / (parts.Length - 2),
            _tracker.Get("main").LiveTokensPerSecond);

        void Observe(string frame)
        {
            var (run, length) = (frame.Split(':')[0], int.Parse(frame.Split(':')[1]));
            _tracker.ObserveCumulativeText("main", run, length);
        }
    }

    [Fact]
    public void CompleteAssistant_RecordsOutputTokensOverStreamingWindow()
    {
        _tracker.ObserveText("main", 40);
        _clock.Advance(TimeSpan.FromSeconds(2));

        Assert.True(_tracker.CompleteAssistant("main", 100));
        var stats = _tracker.Get("main");
        Assert.Equal(1, stats.SampleCount);
        Assert.Equal(50d, stats.LatestTokensPerSecond);
        Assert.Equal(50d, stats.MeanTokensPerSecond);
        Assert.Equal(50d, stats.P95TokensPerSecond);
    }

    [Fact]
    public void ToolActivity_RestartsStreamingWindow()
    {
        _tracker.ObserveText("main", 10);
        _clock.Advance(TimeSpan.FromSeconds(5));
        _tracker.ResetSegment("main");
        _tracker.ObserveText("main", 10);
        _clock.Advance(TimeSpan.FromSeconds(2));

        Assert.True(_tracker.CompleteAssistant("main", 100));
        Assert.Equal(50d, _tracker.Get("main").LatestTokensPerSecond);
    }

    [Fact]
    public void UnstreamedOrBurstReply_RecordsNoSample()
    {
        Assert.False(_tracker.CompleteAssistant("main", 100));

        _tracker.ObserveText("main", 10);
        _clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.False(_tracker.CompleteAssistant("main", 100));

        Assert.Equal(0, _tracker.Get("main").SampleCount);
    }

    [Fact]
    public void MissingOutputTokens_ClosesWindowWithoutSample()
    {
        _tracker.ObserveText("main", 400);
        _clock.Advance(TimeSpan.FromSeconds(1));
        _tracker.ObserveText("main", 0);

        Assert.False(_tracker.CompleteAssistant("main", null));
        var stats = _tracker.Get("main");
        Assert.Null(stats.LiveTokensPerSecond);
        Assert.Equal(0, stats.SampleCount);
    }

    [Fact]
    public void LiveEstimate_UsesCharactersPerTokenOverObservedWindow()
    {
        _tracker.ObserveText("main", 200);
        _clock.Advance(TimeSpan.FromSeconds(1));
        _tracker.ObserveText("main", 200);

        var stats = _tracker.Get("main");
        Assert.Equal(100d, stats.LiveTokensPerSecond);
        Assert.Null(stats.LatestTokensPerSecond);
    }

    [Fact]
    public void Statistics_UseLastFiftySamplesWithNearestRankP95()
    {
        for (var k = 1; k <= 60; k++)
        {
            _tracker.ObserveText("main", 1);
            _clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(_tracker.CompleteAssistant("main", k));
        }

        var stats = _tracker.Get("main");
        Assert.Equal(50, stats.SampleCount);
        Assert.Equal(60d, stats.LatestTokensPerSecond);
        Assert.Equal(35.5d, stats.MeanTokensPerSecond);
        Assert.Equal(58d, stats.P95TokensPerSecond);
    }

    [Fact]
    public void RunUsage_RecordsEachModelCallFromCumulativeTotals_AndFinalDoesNotDoubleCount()
    {
        _tracker.StartRun("main");
        _tracker.ObserveText("main", 10);
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(_tracker.ObserveRunUsage("main", 100));

        _tracker.ObserveText("main", 10);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(_tracker.ObserveRunUsage("main", 160));

        _tracker.ObserveText("main", 0);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(_tracker.CompleteAssistant("main", 160));

        var stats = _tracker.Get("main");
        Assert.Equal(2, stats.SampleCount);
        Assert.Equal(60d, stats.LatestTokensPerSecond);
        Assert.Equal(55d, stats.MeanTokensPerSecond);
    }

    [Fact]
    public void StartRun_ResetsCumulativeBaselineAndFinalFallback()
    {
        _tracker.StartRun("main");
        _tracker.ObserveText("main", 10);
        _clock.Advance(TimeSpan.FromSeconds(1));
        _tracker.ObserveRunUsage("main", 100);

        _tracker.StartRun("main");
        _tracker.ObserveText("main", 10);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(_tracker.ObserveRunUsage("main", 30));
        Assert.Equal(30d, _tracker.Get("main").LatestTokensPerSecond);

        _tracker.StartRun("main");
        _tracker.ObserveText("main", 10);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(_tracker.CompleteAssistant("main", 40));
        Assert.Equal(40d, _tracker.Get("main").LatestTokensPerSecond);
    }

    [Fact]
    public void Changed_IsRaisedForStreamedTextAndCompletedCalls()
    {
        var changed = new List<string>();
        _tracker.Changed += changed.Add;

        _tracker.ObserveText("main", 0);
        Assert.Empty(changed);
        _tracker.ObserveText("main", 5);
        _clock.Advance(TimeSpan.FromSeconds(1));
        _tracker.ObserveRunUsage("main", 10);

        Assert.Equal(["main", "main"], changed);
    }

    [Fact]
    public void RunUsageWithoutObservedStart_AdoptsBaselineInsteadOfRecordingRunTotal()
    {
        // Provider created mid-run: the first total spans calls we never timed.
        _tracker.ObserveText("main", 10);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(_tracker.ObserveRunUsage("main", 500));

        _tracker.ObserveText("main", 10);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(_tracker.ObserveRunUsage("main", 560));

        Assert.Equal(1, _tracker.Get("main").SampleCount);
        Assert.Equal(60d, _tracker.Get("main").LatestTokensPerSecond);
    }

    [Fact]
    public void EndRun_MakesNextUnstartedRunBaselineUnknown()
    {
        _tracker.StartRun("main");
        _tracker.ObserveText("main", 10);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(_tracker.ObserveRunUsage("main", 100));
        _tracker.EndRun("main");

        // Next run's start was missed (e.g. buffered); its total is larger than the stale baseline.
        _tracker.ObserveText("main", 10);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(_tracker.ObserveRunUsage("main", 150));
        Assert.Equal(1, _tracker.Get("main").SampleCount);
    }

    [Fact]
    public void EndRun_ClosesUsageRunSegmentButKeepsFallbackSegmentForFinalMessage()
    {
        _tracker.StartRun("main");
        _tracker.ObserveText("main", 10);
        _clock.Advance(TimeSpan.FromSeconds(1));
        _tracker.EndRun("main");
        Assert.True(_tracker.CompleteAssistant("main", 40));

        _tracker.StartRun("main");
        _tracker.ObserveText("main", 10);
        _clock.Advance(TimeSpan.FromSeconds(1));
        _tracker.ObserveRunUsage("main", 10);
        _tracker.ObserveText("main", 400);
        _clock.Advance(TimeSpan.FromSeconds(1));
        _tracker.ObserveText("main", 400);
        Assert.NotNull(_tracker.Get("main").LiveTokensPerSecond);
        _tracker.EndRun("main");
        Assert.Null(_tracker.Get("main").LiveTokensPerSecond);
    }

    [Fact]
    public void Samples_AreIsolatedPerThread()
    {
        _tracker.ObserveText("a", 10);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(_tracker.CompleteAssistant("a", 10));

        Assert.Equal(ChatThroughputStats.Empty, _tracker.Get("b"));
    }

    [Fact]
    public void InvalidateRuns_KeepsSamplesAndAdoptsNextUsageAsBaseline()
    {
        _tracker.StartRun("main");
        _tracker.ObserveText("main", 10);
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(_tracker.ObserveRunUsage("main", 100));

        _tracker.ObserveText("main", 10); // open window at invalidation
        _tracker.InvalidateRuns();
        Assert.Null(_tracker.Get("main").LiveTokensPerSecond);

        _tracker.ObserveText("main", 10);
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(_tracker.ObserveRunUsage("main", 5000));

        var stats = _tracker.Get("main");
        Assert.Equal(1, stats.SampleCount);
        Assert.Equal(50d, stats.LatestTokensPerSecond);
    }
}
