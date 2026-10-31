using System.Diagnostics;
using System.Text.Json;
using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    private static readonly TimeSpan SessionSwitchDiagnosticBudget = TimeSpan.FromSeconds(5);

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task SessionSwitchDiagnostics_PreserveOriginalFailureAndReport()
    {
        var original = new InvalidOperationException("Synthetic fixture diagnostic control.");
        string? artifacts = null;
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => WithAppAsync(
            _ => Task.FromException(original),
            captureFailure: async (run, failure) =>
            {
                artifacts = run.ArtifactsDirectory;
                Assert.Same(original, failure);
                await WriteSessionSwitchFailureAsync(run, GatewayScenario.MainSessionKey,
                    new { phase = "diagnostic-control", failureType = failure.GetType().Name });
            }));
        Assert.Same(original, observed);
        Assert.NotNull(artifacts);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(artifacts, "run.json")));
        Assert.Equal("failed", report.RootElement.GetProperty("outcome").GetString());
        using var diagnostics = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(artifacts, "session-switch-diagnostics.json")));
        Assert.Equal("diagnostic-control", diagnostics.RootElement.GetProperty("context").GetProperty("phase").GetString());
        Assert.True(diagnostics.RootElement.TryGetProperty("native", out _));
        Assert.False(File.Exists(Path.Combine(artifacts, "session-switch-diagnostics.png")));
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task SessionSwitchDiagnostics_CaptureErrorDoesNotReplaceOriginalFailure()
    {
        var original = new InvalidOperationException("Synthetic fixture diagnostic control.");
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => WithAppAsync(
            _ => Task.FromException(original),
            captureFailure: (_, _) => throw new IOException("Synthetic diagnostic failure.")));
        Assert.Same(original, observed);
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task RepeatedSessionSwitchingKeepsNativeTimelineResponsive()
    {
        var elapsed = new Stopwatch();
        var cycle = new Stopwatch();
        var iteration = 0;
        var expectedSession = GatewayScenario.LongSessionKey;
        var phase = "navigate-chat";
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            elapsed.Start();
            for (; iteration < 12; iteration++)
            {
                cycle.Restart();
                expectedSession = GatewayScenario.LongSessionKey;
                phase = "select-long";
                await SelectSessionAsync(run, GatewayScenario.LongSessionTitle, expectedSession);
                // Tail placement has its own strict test. A missed final row must
                // not prevent this independent crash/responsiveness exercise.
                phase = "visible-long-history";
                await WaitUiAsync(run, () => HasVisibleLongHistory(run), "visible mixed-height long history");
                Assert.False(IsVisibleInTimeline(run, GatewayScenario.OtherHistoryMarker));
                phase = "scroll-top";
                await ScrollToAsync(run, 0);
                phase = "scroll-bottom";
                await ScrollToAsync(run, 100);
                expectedSession = GatewayScenario.OtherSessionKey;
                phase = "select-other";
                await SelectSessionAsync(run, GatewayScenario.OtherSessionTitle, expectedSession);
                phase = "visible-other-history";
                await WaitUiAsync(run, () => IsVisibleInTimeline(run, GatewayScenario.OtherHistoryMarker),
                    "other session's visible history after scrolling");
                Assert.False(HasVisibleLongHistory(run));
                output.WriteLine($"Completed session-switch cycle {iteration + 1}/12 ({cycle.Elapsed.TotalSeconds:F3}s).");
            }
            await CaptureIfRequestedAsync(run, "session-switch-responsive.png");
        }, captureFailure: (run, failure) => WriteSessionSwitchFailureAsync(run, expectedSession, new
        {
            cycle = iteration + 1, phase, expectedSession,
            cycleSeconds = cycle.Elapsed.TotalSeconds, elapsedSeconds = elapsed.Elapsed.TotalSeconds,
            failureType = failure.GetType().Name, capturedAtUtc = DateTimeOffset.UtcNow
        }));
    }

    private async Task WriteSessionSwitchFailureAsync(GatewayFixtureRun run, string expectedSession, object context)
    {
        var metadata = new Dictionary<string, object?> { ["context"] = context };
        async Task Probe(string name, Func<Task<object?>> capture)
        {
            try { metadata[name] = await capture().WaitAsync(SessionSwitchDiagnosticBudget); }
            catch (Exception error) { metadata[name] = new { errorType = error.GetType().Name }; }
        }

        await Probe("rendered", () => Task.Run<object?>(() =>
        {
            var composer = FindById(run, "ChatComposerInput");
            object? rendered = null;
            if (!string.IsNullOrEmpty(composer?.Current.ItemStatus))
            {
                using var status = JsonDocument.Parse(composer.Current.ItemStatus);
                rendered = new
                {
                    selectedThreadId = status.RootElement.GetProperty("selectedThreadId").GetString(),
                    loadedThreadIds = status.RootElement.GetProperty("loadedThreadIds").EnumerateArray()
                        .Select(id => id.GetString()).ToArray()
                };
            }
            return new { rendered, composer = composer is null ? null : SessionSwitchElementMetadata(composer) };
        }));
        await Probe("native", () => Task.Run<object?>(() =>
        {
            var timeline = FindTimeline(run);
            var descendants = timeline?.FindAll(TreeScope.Descendants, Condition.TrueCondition)
                .Cast<AutomationElement>().ToArray() ?? [];
            return new
            {
                timeline = timeline is null ? null : SessionSwitchElementMetadata(timeline),
                descendantCount = descendants.Length,
                // Include raw exposed presenter/repeater peers and realized rows without text or names.
                descendants = descendants.Take(256).Select(SessionSwitchElementMetadata).ToArray(),
                descendantsTruncated = descendants.Length > 256,
                nativeExtent = "UIA exposes scroll percent/view size, not the native pixel extent."
            };
        }));
        await Probe("provider", async () =>
        {
            var snapshot = await run.InvokeAsync("app.chat.snapshot", new { threadId = expectedSession });
            var timeline = snapshot.GetProperty("selectedTimeline");
            return new
            {
                requestedThreadId = snapshot.GetProperty("requestedThreadId").GetString(),
                historyLoaded = timeline.ValueKind == JsonValueKind.Object
                    ? (bool?)timeline.GetProperty("historyLoaded").GetBoolean() : null,
                returnedEntryCount = timeline.ValueKind == JsonValueKind.Object
                    ? (int?)timeline.GetProperty("entries").GetArrayLength() : null,
                // The existing snapshot caps entries at 30; this is not the full history count.
                entryCountLimit = 30
            };
        });
        try
        {
            await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "session-switch-diagnostics.json"),
                JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error)
        {
            output.WriteLine($"Session-switch failure metadata unavailable: {error.GetType().Name}.");
        }
    }

    private static object SessionSwitchElementMetadata(AutomationElement element)
    {
        try
        {
            var bounds = element.Current.BoundingRectangle;
            var children = element.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>().ToArray();
            object? scroll = null;
            if (element.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern))
            {
                var state = ((ScrollPattern)pattern).Current;
                scroll = new { state.VerticallyScrollable, state.VerticalScrollPercent, state.VerticalViewSize };
            }
            return new
            {
                runtimeId = element.GetRuntimeId(), element.Current.AutomationId, element.Current.ClassName,
                controlType = element.Current.ControlType.ProgrammaticName, element.Current.IsOffscreen,
                bounds = bounds.IsEmpty ? null : new { bounds.X, bounds.Y, bounds.Width, bounds.Height },
                childCount = children.Length,
                visibleChildCount = children.Count(child => !child.Current.IsOffscreen),
                scroll
            };
        }
        catch (Exception error) { return new { errorType = error.GetType().Name }; }
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
