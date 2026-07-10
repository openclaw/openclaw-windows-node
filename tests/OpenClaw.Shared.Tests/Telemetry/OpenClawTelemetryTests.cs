using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenClaw.Shared.Telemetry;

namespace OpenClaw.Shared.Tests.Telemetry;

public sealed class OpenClawTelemetryTests
{
    [Fact]
    public void Constants_AreStable()
    {
        Assert.Equal("openclaw", OpenClawActivitySourceName.OpenClaw.ToTelemetryName());
        Assert.Equal("openclaw", OpenClawActivitySources.OpenClawSource.Name);
        Assert.Equal("openclaw", OpenClawMeterName.OpenClaw.ToTelemetryName());
        Assert.Equal("openclaw", OpenClawMeters.OpenClawMeter.Name);
        Assert.Equal("openclaw-windows-tray", OpenClawResourceName.WindowsTray.ToServiceName());
        Assert.Equal("openclaw-windows-node", OpenClawResourceName.WindowsNode.ToServiceName());
        Assert.Equal("openclaw.source", OpenClawTelemetryTagKey.Source.ToTelemetryName());
        Assert.Equal("openclaw.outcome", OpenClawTelemetryTagKey.Outcome.ToTelemetryName());
        Assert.Equal("openclaw.error.category", OpenClawTelemetryTagKey.ErrorCategory.ToTelemetryName());
        Assert.Equal("openclaw.reason", OpenClawTelemetryTagKey.Reason.ToTelemetryName());
        Assert.Equal("openclaw.status", OpenClawTelemetryTagKey.Status.ToTelemetryName());
        Assert.Equal("error.type", OpenClawTelemetryTagKey.ErrorType.ToTelemetryName());
    }

    [Fact]
    public void Trace_NoListener_RunsActionAndReturnsResult()
    {
        var ran = false;

        var result = OpenClawTelemetry.Trace(
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
    public void MarkHelpers_AreSafeForNullActivities()
    {
        var exception = new InvalidOperationException("boom");

        OpenClawTelemetry.MarkSuccess(null);
        OpenClawTelemetry.MarkCanceled(null);
        OpenClawTelemetry.MarkFailure(null, exception);
    }

    [Fact]
    public void StartActivity_WithListener_AllowsManualMarking()
    {
        using var collector = ActivityCollector.Listen(OpenClawActivitySourceName.OpenClaw.ToTelemetryName());
        using var activity = OpenClawTelemetry.StartActivity(
            "test.manual",
            [OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Source, "unit-test")]);

        OpenClawTelemetry.MarkSuccess(activity);

        Assert.NotNull(activity);
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
        Assert.Contains(activity.Tags, tag => tag.Key == OpenClawTelemetryTagKey.Source.ToTelemetryName() && tag.Value == "unit-test");
        Assert.Contains(activity.Tags, tag => tag.Key == OpenClawTelemetryTagKey.Outcome.ToTelemetryName() && tag.Value == "success");
    }

    [Fact]
    public void Trace_WithListener_RecordsSuccessAndTags()
    {
        using var collector = ActivityCollector.Listen(OpenClawActivitySourceName.OpenClaw.ToTelemetryName());

        OpenClawTelemetry.Trace(
            "test.success",
            () => { },
            [
                OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Source, "unit-test"),
                OpenClawTelemetryTag.String("openclaw.test.exporter", "tray-otel")
            ]);

        var activity = Assert.Single(collector.Stopped);
        Assert.Equal("test.success", activity.OperationName);
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
        Assert.Contains(activity.Tags, tag => tag.Key == OpenClawTelemetryTagKey.Source.ToTelemetryName() && tag.Value == "unit-test");
        Assert.Contains(activity.Tags, tag => tag.Key == "openclaw.test.exporter" && tag.Value == "tray-otel");
        Assert.Contains(activity.Tags, tag => tag.Key == OpenClawTelemetryTagKey.Outcome.ToTelemetryName() && tag.Value == "success");
    }

    [Fact]
    public void Trace_Exception_MarksErrorAndRethrowsOriginal()
    {
        using var collector = ActivityCollector.Listen(OpenClawActivitySourceName.OpenClaw.ToTelemetryName());
        var expected = new InvalidOperationException("boom");

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            OpenClawTelemetry.Trace("test.error", () => throw expected));

        Assert.Same(expected, thrown);
        var activity = Assert.Single(collector.Stopped);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(nameof(InvalidOperationException), activity.StatusDescription);
        Assert.Contains(activity.Tags, tag => tag.Key == OpenClawTelemetryTagKey.Outcome.ToTelemetryName() && tag.Value == "failure");
        Assert.Contains(
            activity.Tags,
            tag => tag.Key == OpenClawTelemetryTagKey.ErrorType.ToTelemetryName() && tag.Value == typeof(InvalidOperationException).FullName);
    }

    [Fact]
    public async Task TraceAsync_WithListener_RecordsSuccessAndReturnsResult()
    {
        using var collector = ActivityCollector.Listen(OpenClawActivitySourceName.OpenClaw.ToTelemetryName());

        var result = await OpenClawTelemetry.TraceAsync(
            "test.async.success",
            _ => Task.FromResult("ok"));

        Assert.Equal("ok", result);
        var activity = Assert.Single(collector.Stopped);
        Assert.Equal("test.async.success", activity.OperationName);
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
        Assert.Contains(activity.Tags, tag => tag.Key == OpenClawTelemetryTagKey.Outcome.ToTelemetryName() && tag.Value == "success");
    }

    [Fact]
    public async Task TraceAsync_CanceledTask_MarksCanceledAndPreservesCancellation()
    {
        using var collector = ActivityCollector.Listen(OpenClawActivitySourceName.OpenClaw.ToTelemetryName());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            OpenClawTelemetry.TraceAsync(
                "test.async.cancel",
                token => Task.FromCanceled(token),
                cancellationToken: cts.Token));

        var activity = Assert.Single(collector.Stopped);
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        Assert.Contains(activity.Tags, tag => tag.Key == OpenClawTelemetryTagKey.Outcome.ToTelemetryName() && tag.Value == "canceled");
    }

    [Fact]
    public void Trace_OperationCanceled_MarksCanceledAndRethrowsOriginal()
    {
        using var collector = ActivityCollector.Listen(OpenClawActivitySourceName.OpenClaw.ToTelemetryName());
        var expected = new OperationCanceledException();

        var thrown = Assert.Throws<OperationCanceledException>(() =>
            OpenClawTelemetry.Trace("test.sync.cancel", () => throw expected));

        Assert.Same(expected, thrown);
        var activity = Assert.Single(collector.Stopped);
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        Assert.Contains(activity.Tags, tag => tag.Key == OpenClawTelemetryTagKey.Outcome.ToTelemetryName() && tag.Value == "canceled");
    }

    [Fact]
    public void StringTag_PreservesStringValues()
    {
        const string value = "diagnostic-label";

        var tag = OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Source, value);

        Assert.Equal(value, tag.Value);
    }

    [Fact]
    public void LocalStringTag_UsesLocalKey()
    {
        var tag = OpenClawTelemetryTag.String("openclaw.test.local", "value");

        Assert.Equal("openclaw.test.local", tag.Key);
        Assert.Equal("value", tag.Value);
    }

    [Fact]
    public void TelemetryTag_IsReferenceType_WithValidatedConstruction()
    {
        Assert.False(typeof(OpenClawTelemetryTag).IsValueType);
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpenClawTelemetryTag((OpenClawTelemetryTagKey)999, "value"));
        Assert.Throws<ArgumentException>(() => OpenClawTelemetryTag.String("", "value"));
    }

    [Fact]
    public void CounterMetric_WithListener_RecordsMeasurementAndTags()
    {
        var metricName = $"test.counter.{Guid.NewGuid():N}";
        using var collector = MetricCollector.Listen(OpenClawMeterName.OpenClaw.ToTelemetryName());
        var counter = OpenClawTelemetry.CreateCounter(metricName, unit: "{event}");

        OpenClawTelemetry.Add(
            counter,
            2,
            [
                OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Source, "unit-test"),
                OpenClawTelemetryTag.Number("openclaw.test.count", 7)
            ]);

        var measurement = Assert.Single(collector.LongMeasurements, m => m.Name == metricName);
        Assert.Equal(2, measurement.Value);
        Assert.Contains(measurement.Tags, tag => tag.Key == OpenClawTelemetryTagKey.Source.ToTelemetryName() && (string?)tag.Value == "unit-test");
        Assert.Contains(measurement.Tags, tag => tag.Key == "openclaw.test.count" && (long)tag.Value! == 7);
    }

    [Fact]
    public void HistogramMetric_WithListener_RecordsMeasurementAndTags()
    {
        var metricName = $"test.histogram.{Guid.NewGuid():N}";
        using var collector = MetricCollector.Listen(OpenClawMeterName.OpenClaw.ToTelemetryName());
        var histogram = OpenClawTelemetry.CreateHistogram(metricName, unit: "ms");

        OpenClawTelemetry.Record(
            histogram,
            42.5,
            [OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Source, "unit-test")]);

        var measurement = Assert.Single(collector.DoubleMeasurements, m => m.Name == metricName);
        Assert.Equal(42.5, measurement.Value);
        Assert.Contains(measurement.Tags, tag => tag.Key == OpenClawTelemetryTagKey.Source.ToTelemetryName() && (string?)tag.Value == "unit-test");
    }

    [Fact]
    public void MarkFailure_RequiresException()
    {
        Assert.Throws<ArgumentNullException>(() => OpenClawTelemetry.MarkFailure(null, null!));
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

    private sealed class MetricCollector : IDisposable
    {
        private readonly MeterListener _listener;

        private MetricCollector(string meterName)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == meterName)
                        listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
                LongMeasurements.Add(new MetricMeasurement<long>(instrument.Name, measurement, tags.ToArray())));
            _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
                DoubleMeasurements.Add(new MetricMeasurement<double>(instrument.Name, measurement, tags.ToArray())));
            _listener.Start();
        }

        public List<MetricMeasurement<long>> LongMeasurements { get; } = new();
        public List<MetricMeasurement<double>> DoubleMeasurements { get; } = new();

        public static MetricCollector Listen(string meterName) => new(meterName);

        public void Dispose() => _listener.Dispose();
    }

    private sealed record MetricMeasurement<T>(
        string Name,
        T Value,
        KeyValuePair<string, object?>[] Tags);
}
