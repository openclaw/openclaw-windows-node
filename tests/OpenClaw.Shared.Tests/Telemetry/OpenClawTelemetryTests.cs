using System.Diagnostics;
using OpenClaw.Shared.Telemetry;

namespace OpenClaw.Shared.Tests.Telemetry;

public sealed class OpenClawTelemetryTests
{
    [Fact]
    public void Constants_AreStable()
    {
        Assert.Equal("openclaw", OpenClawActivitySources.OpenClaw);
        Assert.Equal(["openclaw"], OpenClawActivitySources.ExportedNames);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)OpenClawActivitySources.ExportedNames)[0] = "changed");
        Assert.Equal("openclaw-windows-tray", OpenClawResourceNames.Tray);
        Assert.Equal("openclaw-windows-node", OpenClawResourceNames.WindowsNode);
        Assert.Equal("openclaw.source", OpenClawTelemetryTags.Source);
        Assert.Equal("openclaw.outcome", OpenClawTelemetryTags.Outcome);
        Assert.Equal("openclaw.errorCategory", OpenClawTelemetryTags.ErrorCategory);
        Assert.Equal("openclaw.exporter", OpenClawTelemetryTags.Exporter);
        Assert.Equal("openclaw.exporter.protocol", OpenClawTelemetryTags.ExporterProtocol);
        Assert.Equal("openclaw.reason", OpenClawTelemetryTags.Reason);
        Assert.Equal("openclaw.signal", OpenClawTelemetryTags.Signal);
        Assert.Equal("openclaw.status", OpenClawTelemetryTags.Status);
        Assert.Equal("error.type", OpenClawTelemetryTags.ErrorType);
    }

    [Fact]
    public void Trace_NoListener_RunsActionAndReturnsResult()
    {
        var ran = false;

        var result = OpenClawTelemetry.Trace(
            OpenClawActivitySources.OpenClawSource,
            "test.no_listener",
            () =>
            {
                ran = true;
                return 42;
            });

        Assert.True(ran);
        Assert.Equal(42, result);
    }

    [Fact]
    public void Trace_WithListener_RecordsSuccessAndTags()
    {
        using var collector = ActivityCollector.Listen(OpenClawActivitySources.OpenClaw);

        OpenClawTelemetry.Trace(
            OpenClawActivitySources.OpenClawSource,
            "test.success",
            () => { },
            [
                OpenClawTelemetryTag.String(OpenClawTelemetryTags.Source, "unit-test"),
                OpenClawTelemetryTag.String(OpenClawTelemetryTags.Exporter, "tray-otel")
            ]);

        var activity = Assert.Single(collector.Stopped);
        Assert.Equal("test.success", activity.OperationName);
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
        Assert.Contains(activity.Tags, tag => tag.Key == OpenClawTelemetryTags.Source && tag.Value == "unit-test");
        Assert.Contains(activity.Tags, tag => tag.Key == OpenClawTelemetryTags.Exporter && tag.Value == "tray-otel");
    }

    [Fact]
    public void Trace_Exception_MarksErrorAndRethrowsOriginal()
    {
        using var collector = ActivityCollector.Listen(OpenClawActivitySources.OpenClaw);
        var expected = new InvalidOperationException("boom");

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            OpenClawTelemetry.Trace(OpenClawActivitySources.OpenClawSource, "test.error", () => throw expected));

        Assert.Same(expected, thrown);
        var activity = Assert.Single(collector.Stopped);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(nameof(InvalidOperationException), activity.StatusDescription);
        Assert.Contains(
            activity.Tags,
            tag => tag.Key == OpenClawTelemetryTags.ErrorType && tag.Value == typeof(InvalidOperationException).FullName);
    }

    [Fact]
    public async Task TraceAsync_WithListener_RecordsSuccessAndReturnsResult()
    {
        using var collector = ActivityCollector.Listen(OpenClawActivitySources.OpenClaw);

        var result = await OpenClawTelemetry.TraceAsync(
            OpenClawActivitySources.OpenClawSource,
            "test.async.success",
            _ => Task.FromResult("ok"));

        Assert.Equal("ok", result);
        var activity = Assert.Single(collector.Stopped);
        Assert.Equal("test.async.success", activity.OperationName);
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
    }

    [Fact]
    public async Task TraceAsync_CanceledTask_MarksCanceledAndPreservesCancellation()
    {
        using var collector = ActivityCollector.Listen(OpenClawActivitySources.OpenClaw);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            OpenClawTelemetry.TraceAsync(
                OpenClawActivitySources.OpenClawSource,
                "test.async.cancel",
                token => Task.FromCanceled(token),
                cancellationToken: cts.Token));

        var activity = Assert.Single(collector.Stopped);
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        Assert.Contains(activity.Tags, tag => tag.Key == OpenClawTelemetryTags.Outcome && tag.Value == "canceled");
    }

    [Fact]
    public void StringTag_BoundsLongValues()
    {
        var tag = OpenClawTelemetryTag.String(OpenClawTelemetryTags.Source, "abcdef", maxLength: 3);

        Assert.Equal("abc", tag.Value);
    }

    [Fact]
    public void TelemetryTag_IsReferenceType_WithValidatedConstruction()
    {
        Assert.False(typeof(OpenClawTelemetryTag).IsValueType);
        Assert.Throws<ArgumentException>(() => new OpenClawTelemetryTag("", "value"));
    }

    private sealed class ActivityCollector : IDisposable
    {
        private readonly ActivityListener _listener;

        private ActivityCollector(string sourceName)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == sourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => Stopped.Add(activity)
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public List<Activity> Stopped { get; } = new();

        public static ActivityCollector Listen(string sourceName) => new(sourceName);

        public void Dispose() => _listener.Dispose();
    }
}
