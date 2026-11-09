using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    private const int SelectionDiagnosticCounterLimit = 4096;

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task RepeatedSessionSwitchingKeepsNativeTimelineResponsive()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            var journalPath = Path.Combine(run.ArtifactsDirectory, "session-switch-stages.jsonl");
            var lastProbePath = Path.Combine(run.ArtifactsDirectory, "session-switch-last-probe.json");
            var diagnostics = new SessionSelectionLog(
                json => File.AppendAllText(journalPath, json + Environment.NewLine),
                json =>
                {
                    File.WriteAllText(lastProbePath + ".tmp", json);
                    File.Move(lastProbePath + ".tmp", lastProbePath, overwrite: true);
                });
            for (var iteration = 0; iteration < 12; iteration++)
            {
                var cycle = System.Diagnostics.Stopwatch.StartNew();
                // Bounded journal plus one atomic last-probe snapshot, written before native
                // entry. Failure handling consumes cached evidence, never another UIA read.
                void RecordStage(SessionSelectionStage stage) => diagnostics.Record(iteration + 1, stage);
                await SelectSessionAsync(run, GatewayScenario.LongSessionTitle, GatewayScenario.LongSessionKey,
                    observe: RecordStage);
                // Tail placement has its own strict test. A missed final row must
                // not prevent this independent crash/responsiveness exercise.
                await WaitUiAsync(run, () => HasVisibleLongHistory(run), "visible mixed-height long history");
                Assert.False(IsVisibleInTimeline(run, GatewayScenario.OtherHistoryMarker));
                await ScrollToAsync(run, 0);
                await ScrollToAsync(run, 100);
                await SelectSessionAsync(run, GatewayScenario.OtherSessionTitle, GatewayScenario.OtherSessionKey,
                    observe: RecordStage);
                await WaitUiAsync(run, () => IsVisibleInTimeline(run, GatewayScenario.OtherHistoryMarker),
                    "other session's visible history after scrolling");
                Assert.False(HasVisibleLongHistory(run));
                output.WriteLine($"Completed session-switch cycle {iteration + 1}/12 ({cycle.Elapsed.TotalSeconds:F3}s).");
            }
            await CaptureIfRequestedAsync(run, "session-switch-responsive.png");
        });
    }

    // This state is local to one selection. Operations/stages are fixed call-site labels;
    // only whitelisted fixture agent IDs, booleans and bounded numeric metadata leave it.
    private sealed class SessionSelectionProbe
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly Action<SessionSelectionStage>? _observe;
        private int _operationStarted;
        private Exception? _recordedError;
        public SessionSelectionStage Current { get; private set; }

        public SessionSelectionProbe(string intendedAgent, Action<SessionSelectionStage>? observe)
        {
            _observe = observe;
            Current = new("selection-start", intendedAgent is "main" or "research" ? intendedAgent : "other", "unobserved");
            Emit(Current);
        }

        private int Elapsed => (int)Math.Min(_watch.ElapsedMilliseconds, int.MaxValue);
        public void Emit(SessionSelectionStage stage)
        {
            Current = stage with { ElapsedMs = Elapsed };
            _observe?.Invoke(Current);
        }

        public void BeginAttempt(string stage) => Emit(Current with
        {
            Stage = stage, Attempt = Math.Min(Current.Attempt + 1, SelectionDiagnosticCounterLimit),
            Operation = null, Edge = "probe-enter", OperationElapsedMs = 0, ExceptionCategory = null, HResult = null,
            CountersSaturated = Current.CountersSaturated || Current.Attempt >= SelectionDiagnosticCounterLimit,
            ObservedAgent = stage == "agent-acknowledgement" ? "unobserved" : Current.ObservedAgent,
            PopupCollapsed = stage == "agent-acknowledgement" ? null : Current.PopupCollapsed
        });

        public T Read<T>(string operation, Func<T> read)
        {
            var started = Elapsed;
            _operationStarted = started;
            _recordedError = null;
            Emit(Current with { Operation = operation, Edge = "enter", OperationElapsedMs = 0,
                ExceptionCategory = null, HResult = null });
            var value = read();
            _operationStarted = started;
            Emit(Current with { Operation = operation, Edge = "return", OperationElapsedMs = Elapsed - started });
            return value;
        }

        public void Run(string operation, Action action) => Read(operation, () => { action(); return 0; });

        public void Fault(Exception error)
        {
            if (ReferenceEquals(error, _recordedError)) return;
            _recordedError = error;
            var unavailable = error is ElementNotAvailableException;
            var category = error switch
            {
                ElementNotAvailableException => "element-not-available",
                NotSupportedException => "unsupported", InvalidOperationException => "invalid-operation",
                COMException => "com", TimeoutException => "timeout", _ => "other"
            };
            // Preserve the native failure/re-poll behavior even if evidence I/O itself fails.
            try
            {
                Emit(Current with { Edge = Current.Edge == "enter" ? "error" : error is TimeoutException ? "wait-timeout" : "phase-error",
                    OperationElapsedMs = Current.Edge == "enter" ? Elapsed - _operationStarted : Current.OperationElapsedMs,
                    ExceptionCategory = category, HResult = error.HResult,
                    UnavailableCount = unavailable ? Math.Min(Current.UnavailableCount + 1, SelectionDiagnosticCounterLimit) : Current.UnavailableCount,
                    LastUnavailableOperation = unavailable ? Current.Operation : Current.LastUnavailableOperation,
                    LastUnavailableHResult = unavailable ? error.HResult : Current.LastUnavailableHResult,
                    CountersSaturated = Current.CountersSaturated || unavailable && Current.UnavailableCount >= SelectionDiagnosticCounterLimit });
            }
            catch (Exception) { /* The original UIA/assertion failure remains authoritative. */ }
        }
    }

    // The injected writers are the file-I/O boundary, not a replacement UI producer.
    // Keeping a last snapshot after the journal fills avoids hiding the final blocked read.
    private sealed class SessionSelectionLog(Action<string> append, Action<string> replaceLast)
    {
        private const int JournalLimit = 2048;
        private int _written;
        private int _suppressed;
        public string? LastProbeJson { get; private set; }

        public void Record(int cycle, SessionSelectionStage stage)
        {
            var truncated = _written >= JournalLimit;
            if (truncated) _suppressed = Math.Min(_suppressed + 1, SelectionDiagnosticCounterLimit);
            var json = JsonSerializer.Serialize(new { cycle, observedAt = DateTimeOffset.UtcNow, stage,
                journalTruncated = truncated, suppressedEntries = _suppressed });
            LastProbeJson = json;
            replaceLast(json);
            if (truncated) return;
            append(json);
            _written++;
        }
    }

    [Fact]
    public void SelectionDiagnostics_BoundsJournalRetainsInFlightStateAndExcludesExceptionContent()
    {
        var journal = new List<string>();
        string? persisted = null;
        var log = new SessionSelectionLog(journal.Add, json => persisted = json);
        _ = new SessionSelectionProbe("SECRET-AGENT-KEY", stage => log.Record(1, stage));
        Assert.DoesNotContain("SECRET", persisted!);
        var probe = new SessionSelectionProbe("research", stage => log.Record(2, stage));
        probe.BeginAttempt("agent-acknowledgement");
        var unavailable = new ElementNotAvailableException("SECRET-UI-CONTENT");
        var observed = Assert.Throws<ElementNotAvailableException>(() => probe.Read<int>("selected-id", () =>
        {
            using var entry = JsonDocument.Parse(persisted!);
            Assert.Equal("enter", entry.RootElement.GetProperty("stage").GetProperty("Edge").GetString());
            throw unavailable;
        }));
        Assert.Same(unavailable, observed);
        probe.Fault(observed);
        probe.Fault(observed); // The poll hook and outer catch must not double-count the same failure.
        Assert.Equal(1, probe.Current.UnavailableCount);
        Assert.Equal("selected-id", probe.Current.LastUnavailableOperation);
        Assert.Equal(unavailable.HResult, probe.Current.LastUnavailableHResult);
        Assert.Equal("element-not-available", probe.Current.ExceptionCategory);
        Assert.DoesNotContain("SECRET", persisted!);
        var unsupported = new NotSupportedException("SECRET-PATTERN-CONTENT");
        Assert.Same(unsupported, Assert.Throws<NotSupportedException>(() =>
            probe.Read<int>("selection-item-pattern", () => throw unsupported)));
        probe.Fault(unsupported);
        Assert.Equal("unsupported", probe.Current.ExceptionCategory);
        Assert.Equal(unsupported.HResult, probe.Current.HResult);
        var actions = 0;
        probe.Run("select-once", () => actions++);
        Assert.Equal(1, actions);
        for (var attempt = 0; attempt < 4100; attempt++) probe.BeginAttempt("agent-acknowledgement");
        probe.Read("popup-state", () => true);
        Assert.Equal(2048, journal.Count);
        Assert.Equal(4096, probe.Current.Attempt);
        Assert.True(probe.Current.CountersSaturated);
        using var last = JsonDocument.Parse(persisted!);
        Assert.True(last.RootElement.GetProperty("journalTruncated").GetBoolean());
        Assert.InRange(last.RootElement.GetProperty("suppressedEntries").GetInt32(), 1, 4096);
        Assert.Equal("popup-state", last.RootElement.GetProperty("stage").GetProperty("Operation").GetString());
        Assert.Equal("return", last.RootElement.GetProperty("stage").GetProperty("Edge").GetString());
        Assert.Equal(persisted, log.LastProbeJson);
        Assert.Equal(new[] { "cycle", "observedAt", "stage", "journalTruncated", "suppressedEntries" },
            last.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(new[] { "Stage", "IntendedAgent", "ObservedAgent", "RowPresent", "RowOffscreen", "RowSelected",
            "Operation", "Edge", "Attempt", "ElapsedMs", "OperationElapsedMs", "PopupCollapsed", "UnavailableCount",
            "ExceptionCategory", "HResult", "LastUnavailableOperation", "LastUnavailableHResult", "CountersSaturated" },
            last.RootElement.GetProperty("stage").EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain("SECRET", persisted!);
        Assert.All(journal, json => Assert.DoesNotContain("SECRET", json));
    }

    private static bool HasVisibleLongHistory(GatewayFixtureRun run)
    {
        var timeline = FindTimeline(run);
        if (timeline is null)
            return false;
        var texts = timeline.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
        return texts.Cast<AutomationElement>().Any(element =>
        {
            var text = element.Current.Name;
            return (text.StartsWith("Fixture long message ", StringComparison.Ordinal)
                || text.Contains(GatewayScenario.LongEarlySentinel, StringComparison.Ordinal)
                || text.Contains(GatewayScenario.LongMiddleSentinel, StringComparison.Ordinal)
                || text.Contains(GatewayScenario.LongFinalSentinel, StringComparison.Ordinal)
                || text.Contains(GatewayScenario.LongHistoryFinalMarker, StringComparison.Ordinal))
                && IsVisibleInTimeline(timeline, element);
        });
    }
}
