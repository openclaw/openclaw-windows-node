using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenClaw.Shared.Telemetry;

namespace OpenClaw.Shared.Tests.Telemetry;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class McpServerTelemetryCollection
{
    public const string Name = "MCP server telemetry";
}

[Collection(McpServerTelemetryCollection.Name)]
public sealed class McpServerTelemetryTests
{
    [Fact]
    public void ConstantsAndFiniteValues_AreStable()
    {
        Assert.Equal("openclaw.mcp.server.start", McpServerTelemetry.StartSpanName);
        Assert.Equal("openclaw.mcp.server.stop", McpServerTelemetry.StopSpanName);
        Assert.Equal("openclaw.mcp.server.request", McpServerTelemetry.RequestSpanName);
        Assert.Equal(
            "openclaw.mcp.server.lifecycle.operations",
            McpServerTelemetry.LifecycleOperationsMetricName);
        Assert.Equal("openclaw.mcp.server.requests", McpServerTelemetry.RequestsMetricName);
        Assert.Equal(
            "openclaw.mcp.server.listener.errors",
            McpServerTelemetry.ListenerErrorsMetricName);
        Assert.Equal(
            "openclaw.mcp.server.request.duration",
            McpServerTelemetry.RequestDurationMetricName);
        Assert.Equal("start", McpServerOperation.Start.ToTelemetryValue());
        Assert.Equal("stop", McpServerOperation.Stop.ToTelemetryValue());
        Assert.Equal("probe", McpServerRequestKind.Probe.ToTelemetryValue());
        Assert.Equal("json_rpc", McpServerRequestKind.JsonRpc.ToTelemetryValue());
        Assert.Equal("authentication_failed", McpServerErrorCategory.AuthenticationFailed.ToTelemetryValue());
        Assert.Equal("drain_timeout", McpServerErrorCategory.DrainTimeout.ToTelemetryValue());
    }

    [Fact]
    public void FiniteValueMappings_RejectUnknownEnumValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ((McpServerOperation)(-1)).ToTelemetryValue());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ((McpServerRequestKind)(-1)).ToTelemetryValue());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ((McpServerOutcome)(-1)).ToTelemetryValue());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ((McpServerErrorCategory)(-1)).ToTelemetryValue());
    }

    [Fact]
    public void Lifecycle_CreatesDetachedRoot_AndCompletesExactlyOnce()
    {
        using var activities = new ActivityCollector();
        using var metrics = new MetricCollector();
        using var ambient = new Activity("ambient").Start();
        using var operation = McpServerTelemetry.StartLifecycle(McpServerOperation.Start);

        Assert.Same(ambient, Activity.Current);

        var completion = operation.Complete(McpServerOutcome.Success);
        var duplicate = operation.Complete(
            McpServerOutcome.Failure,
            McpServerErrorCategory.InternalFailure);

        Assert.NotNull(completion);
        Assert.Null(duplicate);
        Assert.True(completion.DurationMilliseconds >= 0);
        Assert.Same(ambient, Activity.Current);

        var activity = Assert.Single(activities.Stopped);
        Assert.Equal(McpServerTelemetry.StartSpanName, activity.OperationName);
        Assert.Equal(default, activity.ParentSpanId);
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
        Assert.Equal(
            "start",
            activity.GetTagItem(McpServerTelemetry.OperationTag));
        Assert.Equal(
            "success",
            activity.GetTagItem(OpenClawTelemetryTagKey.Outcome.ToTelemetryName()));
        Assert.Null(activity.GetTagItem(OpenClawTelemetryTagKey.ErrorCategory.ToTelemetryName()));

        var measurement = Assert.Single(
            metrics.LongMeasurements,
            item => item.Name == McpServerTelemetry.LifecycleOperationsMetricName);
        Assert.Equal(1, measurement.Value);
        AssertTags(
            measurement.Tags,
            (McpServerTelemetry.OperationTag, "start"),
            (OpenClawTelemetryTagKey.Outcome.ToTelemetryName(), "success"),
            (OpenClawTelemetryTagKey.ErrorCategory.ToTelemetryName(), "none"));
    }

    [Fact]
    public void Request_RecordsDurationAndOnlyReviewedTags()
    {
        using var activities = new ActivityCollector();
        using var metrics = new MetricCollector();
        using var operation = McpServerTelemetry.StartRequest(McpServerRequestKind.JsonRpc);

        var completion = operation.Complete(
            McpServerOutcome.Failure,
            McpServerErrorCategory.AuthenticationFailed,
            typeof(UnauthorizedAccessException));

        Assert.NotNull(completion);
        Assert.True(completion.DurationMilliseconds >= 0);

        var activity = Assert.Single(activities.Stopped);
        Assert.Equal(System.Diagnostics.ActivityKind.Server, activity.Kind);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        AssertTags(
            activity.TagObjects.ToArray(),
            (McpServerTelemetry.RequestKindTag, "json_rpc"),
            (OpenClawTelemetryTagKey.Outcome.ToTelemetryName(), "failure"),
            (OpenClawTelemetryTagKey.ErrorCategory.ToTelemetryName(), "authentication_failed"),
            (OpenClawTelemetryTagKey.ErrorType.ToTelemetryName(), typeof(UnauthorizedAccessException).FullName));

        var count = Assert.Single(
            metrics.LongMeasurements,
            item => item.Name == McpServerTelemetry.RequestsMetricName);
        var duration = Assert.Single(
            metrics.DoubleMeasurements,
            item => item.Name == McpServerTelemetry.RequestDurationMetricName);
        Assert.Equal(1, count.Value);
        Assert.True(duration.Value >= 0);
        AssertTags(
            count.Tags,
            (McpServerTelemetry.RequestKindTag, "json_rpc"),
            (OpenClawTelemetryTagKey.Outcome.ToTelemetryName(), "failure"),
            (OpenClawTelemetryTagKey.ErrorCategory.ToTelemetryName(), "authentication_failed"));
        Assert.Equal(
            count.Tags.OrderBy(tag => tag.Key),
            duration.Tags.OrderBy(tag => tag.Key));
        Assert.DoesNotContain(
            count.Tags,
            tag => tag.Key == OpenClawTelemetryTagKey.ErrorType.ToTelemetryName());

        var reviewedKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            McpServerTelemetry.RequestKindTag,
            OpenClawTelemetryTagKey.Outcome.ToTelemetryName(),
            OpenClawTelemetryTagKey.ErrorCategory.ToTelemetryName(),
            OpenClawTelemetryTagKey.ErrorType.ToTelemetryName()
        };
        Assert.All(activity.TagObjects, tag => Assert.Contains(tag.Key, reviewedKeys));
        Assert.All(count.Tags, tag => Assert.Contains(tag.Key, reviewedKeys));
        Assert.All(duration.Tags, tag => Assert.Contains(tag.Key, reviewedKeys));
    }

    [Fact]
    public void ListenerError_RecordsOnlyFiniteCategory()
    {
        using var metrics = new MetricCollector();

        McpServerTelemetry.RecordListenerError(McpServerErrorCategory.ListenerAccept);

        var measurement = Assert.Single(
            metrics.LongMeasurements,
            item => item.Name == McpServerTelemetry.ListenerErrorsMetricName);
        AssertTags(
            measurement.Tags,
            (OpenClawTelemetryTagKey.ErrorCategory.ToTelemetryName(), "listener_accept"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => McpServerTelemetry.RecordListenerError(McpServerErrorCategory.Timeout));
    }

    [Fact]
    public void NoListeners_OperationsRemainBehaviorSafe()
    {
        using var lifecycle = McpServerTelemetry.StartLifecycle(McpServerOperation.Stop);
        using var request = McpServerTelemetry.StartRequest(McpServerRequestKind.Other);

        Assert.NotNull(lifecycle.Complete(McpServerOutcome.Success));
        Assert.NotNull(request.Complete(
            McpServerOutcome.Canceled,
            McpServerErrorCategory.Shutdown));
    }

    private static void AssertTags(
        IReadOnlyCollection<KeyValuePair<string, object?>> actual,
        params (string Key, object? Value)[] expected)
    {
        Assert.Equal(expected.Length, actual.Count);
        foreach (var (key, value) in expected)
        {
            Assert.Contains(
                actual,
                tag => tag.Key == key && Equals(tag.Value, value));
        }
    }

    private sealed class ActivityCollector : IDisposable
    {
        private readonly ActivityListener _listener;

        public ActivityCollector()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source =>
                    source.Name == OpenClawActivitySourceName.OpenClaw.ToTelemetryName(),
                Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                    options.Name.StartsWith("openclaw.mcp.server.", StringComparison.Ordinal)
                        ? ActivitySamplingResult.AllDataAndRecorded
                        : ActivitySamplingResult.None,
                ActivityStopped = activity =>
                {
                    if (activity.OperationName.StartsWith(
                        "openclaw.mcp.server.",
                        StringComparison.Ordinal))
                    {
                        Stopped.Enqueue(activity);
                    }
                }
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public ConcurrentQueue<Activity> Stopped { get; } = new();

        public void Dispose() => _listener.Dispose();
    }

    private sealed class MetricCollector : IDisposable
    {
        private readonly MeterListener _listener = new();

        public MetricCollector()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == OpenClawMeterName.OpenClaw.ToTelemetryName() &&
                    instrument.Name.StartsWith("openclaw.mcp.server.", StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
                LongMeasurements.Add(new MetricMeasurement<long>(
                    instrument.Name,
                    measurement,
                    tags.ToArray())));
            _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
                DoubleMeasurements.Add(new MetricMeasurement<double>(
                    instrument.Name,
                    measurement,
                    tags.ToArray())));
            _listener.Start();
        }

        public ConcurrentBag<MetricMeasurement<long>> LongMeasurements { get; } = new();
        public ConcurrentBag<MetricMeasurement<double>> DoubleMeasurements { get; } = new();

        public void Dispose() => _listener.Dispose();
    }

    private sealed record MetricMeasurement<T>(
        string Name,
        T Value,
        KeyValuePair<string, object?>[] Tags);
}
