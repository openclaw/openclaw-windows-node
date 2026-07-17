using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OpenClaw.Shared.Telemetry;

/// <summary>
/// Small helpers for OpenClaw code paths that want to expose traced spans and metrics.
/// Exporter configuration remains owned by the app process.
/// </summary>
public static class OpenClawTelemetry
{
    private const string OutcomeSuccess = "success";
    private const string OutcomeCanceled = "canceled";
    private const string OutcomeFailure = "failure";

    /// <summary>
    /// Starts a span for work that needs manual lifetime control.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="Trace(string, Action, IEnumerable{OpenClawTelemetryTag}?, System.Diagnostics.ActivityKind, OpenClawActivitySourceName)"/>
    /// or <see cref="TraceAsync(string, Func{CancellationToken, Task}, IEnumerable{OpenClawTelemetryTag}?, System.Diagnostics.ActivityKind, CancellationToken, OpenClawActivitySourceName)"/>
    /// when the span should cover a single function call. Use this method when a span must cross multiple calls,
    /// has custom lifetime boundaries, or needs manual success/failure/cancellation marking.
    /// <para>
    /// The returned <see cref="Activity"/> can be <see langword="null"/> when no listener is recording this
    /// source or the configured sampler drops the span. Callers should use null-safe disposal and mark helpers.
    /// </para>
    /// </remarks>
    public static Activity? StartActivity(
        string spanName,
        IEnumerable<OpenClawTelemetryTag>? tags = null,
        System.Diagnostics.ActivityKind kind = System.Diagnostics.ActivityKind.Internal,
        OpenClawActivitySourceName source = OpenClawActivitySourceName.OpenClaw)
    {
        if (string.IsNullOrWhiteSpace(spanName))
            throw new ArgumentException("Span name cannot be empty.", nameof(spanName));

        var activity = source.ToActivitySource().StartActivity(spanName, kind);
        ApplyTags(activity, tags);
        return activity;
    }

    /// <summary>
    /// Starts a manually-controlled span without leaving it as the ambient activity.
    /// </summary>
    /// <remarks>
    /// Use this for operations that begin in one asynchronous callback and finish in another.
    /// The caller owns the returned activity and must finish it with
    /// <see cref="StopDetachedActivity(Activity?)"/> so stopping it cannot replace a newer
    /// ambient activity with the context captured when this span started.
    /// </remarks>
    public static Activity? StartDetachedActivity(
        string spanName,
        IEnumerable<OpenClawTelemetryTag>? tags = null,
        System.Diagnostics.ActivityKind kind = System.Diagnostics.ActivityKind.Internal,
        OpenClawActivitySourceName source = OpenClawActivitySourceName.OpenClaw)
    {
        var previous = Activity.Current;
        try
        {
            return StartActivity(spanName, tags, kind, source);
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    /// <summary>
    /// Starts a manually-controlled child span without leaving it as the ambient activity.
    /// </summary>
    public static Activity? StartDetachedActivity(
        string spanName,
        ActivityContext parentContext,
        IEnumerable<OpenClawTelemetryTag>? tags = null,
        System.Diagnostics.ActivityKind kind = System.Diagnostics.ActivityKind.Internal,
        OpenClawActivitySourceName source = OpenClawActivitySourceName.OpenClaw)
    {
        if (string.IsNullOrWhiteSpace(spanName))
            throw new ArgumentException("Span name cannot be empty.", nameof(spanName));

        var previous = Activity.Current;
        try
        {
            Activity.Current = null;
            var activity = source.ToActivitySource().StartActivity(spanName, kind, parentContext);
            ApplyTags(activity, tags);
            return activity;
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    /// <summary>
    /// Stops and disposes a detached activity without changing the caller's ambient activity.
    /// </summary>
    public static void StopDetachedActivity(Activity? activity)
    {
        if (activity == null)
            return;

        var current = Activity.Current;
        try
        {
            activity.Stop();
            activity.Dispose();
        }
        finally
        {
            Activity.Current = current;
        }
    }

    /// <summary>
    /// Runs a synchronous action inside a span and automatically marks success, cancellation, or failure.
    /// </summary>
    /// <remarks>
    /// Use this for the common case where a span should cover exactly one function call. Exceptions and
    /// cancellations are rethrown unchanged after the span is annotated.
    /// </remarks>
    public static void Trace(
        string spanName,
        Action action,
        IEnumerable<OpenClawTelemetryTag>? tags = null,
        System.Diagnostics.ActivityKind kind = System.Diagnostics.ActivityKind.Internal,
        OpenClawActivitySourceName source = OpenClawActivitySourceName.OpenClaw)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var activity = StartActivity(spanName, tags, kind, source);

        try
        {
            action();
            MarkSuccess(activity);
        }
        catch (OperationCanceledException)
        {
            MarkCanceled(activity);
            throw;
        }
        catch (Exception ex)
        {
            MarkFailure(activity, ex);
            throw;
        }
    }

    /// <summary>
    /// Runs a synchronous function inside a span and returns its result.
    /// </summary>
    /// <remarks>
    /// Use this for the common case where a span should cover exactly one value-returning function call.
    /// Exceptions and cancellations are rethrown unchanged after the span is annotated.
    /// </remarks>
    public static T Trace<T>(
        string spanName,
        Func<T> action,
        IEnumerable<OpenClawTelemetryTag>? tags = null,
        System.Diagnostics.ActivityKind kind = System.Diagnostics.ActivityKind.Internal,
        OpenClawActivitySourceName source = OpenClawActivitySourceName.OpenClaw)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var activity = StartActivity(spanName, tags, kind, source);

        try
        {
            var result = action();
            MarkSuccess(activity);
            return result;
        }
        catch (OperationCanceledException)
        {
            MarkCanceled(activity);
            throw;
        }
        catch (Exception ex)
        {
            MarkFailure(activity, ex);
            throw;
        }
    }

    /// <summary>
    /// Runs an asynchronous action inside a span and automatically marks success, cancellation, or failure.
    /// </summary>
    /// <remarks>
    /// Use this for the common case where a span should cover exactly one asynchronous operation. The supplied
    /// cancellation token is passed to <paramref name="action"/> and cancellations are rethrown unchanged.
    /// </remarks>
    public static async Task TraceAsync(
        string spanName,
        Func<CancellationToken, Task> action,
        IEnumerable<OpenClawTelemetryTag>? tags = null,
        System.Diagnostics.ActivityKind kind = System.Diagnostics.ActivityKind.Internal,
        CancellationToken cancellationToken = default,
        OpenClawActivitySourceName source = OpenClawActivitySourceName.OpenClaw)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var activity = StartActivity(spanName, tags, kind, source);

        try
        {
            await action(cancellationToken).ConfigureAwait(false);
            MarkSuccess(activity);
        }
        catch (OperationCanceledException)
        {
            MarkCanceled(activity);
            throw;
        }
        catch (Exception ex)
        {
            MarkFailure(activity, ex);
            throw;
        }
    }

    /// <summary>
    /// Runs an asynchronous function inside a span and returns its result.
    /// </summary>
    /// <remarks>
    /// Use this for the common case where a span should cover exactly one value-returning asynchronous
    /// operation. The supplied cancellation token is passed to <paramref name="action"/> and cancellations are
    /// rethrown unchanged.
    /// </remarks>
    public static async Task<T> TraceAsync<T>(
        string spanName,
        Func<CancellationToken, Task<T>> action,
        IEnumerable<OpenClawTelemetryTag>? tags = null,
        System.Diagnostics.ActivityKind kind = System.Diagnostics.ActivityKind.Internal,
        CancellationToken cancellationToken = default,
        OpenClawActivitySourceName source = OpenClawActivitySourceName.OpenClaw)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var activity = StartActivity(spanName, tags, kind, source);

        try
        {
            var result = await action(cancellationToken).ConfigureAwait(false);
            MarkSuccess(activity);
            return result;
        }
        catch (OperationCanceledException)
        {
            MarkCanceled(activity);
            throw;
        }
        catch (Exception ex)
        {
            MarkFailure(activity, ex);
            throw;
        }
    }

    /// <summary>
    /// Creates a long-lived counter instrument for monotonically increasing measurements.
    /// </summary>
    /// <remarks>
    /// Store the returned counter in a static field near the instrumentation point and call
    /// <see cref="Add(Counter{long}, long, IEnumerable{OpenClawTelemetryTag}?)"/> whenever an event occurs.
    /// Use counters for counts such as attempts, completions, or probe sends.
    /// </remarks>
    public static Counter<long> CreateCounter(
        string metricName,
        string? unit = null,
        string? description = null,
        OpenClawMeterName meter = OpenClawMeterName.OpenClaw)
    {
        ValidateMetricName(metricName);
        return meter.ToMeter().CreateCounter<long>(metricName, unit, description);
    }

    /// <summary>
    /// Creates a long-lived histogram instrument for recording distributions.
    /// </summary>
    /// <remarks>
    /// Store the returned histogram in a static field near the instrumentation point and call
    /// <see cref="Record(Histogram{double}, double, IEnumerable{OpenClawTelemetryTag}?)"/> for each value.
    /// Use histograms for durations, sizes, or other values where percentiles are useful.
    /// </remarks>
    public static Histogram<double> CreateHistogram(
        string metricName,
        string? unit = null,
        string? description = null,
        OpenClawMeterName meter = OpenClawMeterName.OpenClaw)
    {
        ValidateMetricName(metricName);
        return meter.ToMeter().CreateHistogram<double>(metricName, unit, description);
    }

    /// <summary>
    /// Adds a measurement to a counter instrument.
    /// </summary>
    public static void Add(
        Counter<long> counter,
        long delta = 1,
        IEnumerable<OpenClawTelemetryTag>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(counter);
        var tagList = ToTagList(tags);
        counter.Add(delta, in tagList);
    }

    /// <summary>
    /// Records a value in a histogram instrument.
    /// </summary>
    public static void Record(
        Histogram<double> histogram,
        double value,
        IEnumerable<OpenClawTelemetryTag>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(histogram);
        var tagList = ToTagList(tags);
        histogram.Record(value, in tagList);
    }

    private static void ApplyTags(Activity? activity, IEnumerable<OpenClawTelemetryTag>? tags)
    {
        if (activity == null || tags == null)
            return;

        foreach (var tag in tags)
            activity.SetTag(tag.Key, tag.Value);
    }

    private static TagList ToTagList(IEnumerable<OpenClawTelemetryTag>? tags)
    {
        var tagList = new TagList();
        if (tags == null)
            return tagList;

        foreach (var tag in tags)
            tagList.Add(tag.Key, tag.Value);

        return tagList;
    }

    private static void ValidateMetricName(string metricName)
    {
        if (string.IsNullOrWhiteSpace(metricName))
            throw new ArgumentException("Metric name cannot be empty.", nameof(metricName));
    }

    /// <summary>
    /// Marks a manually-created span as successful.
    /// </summary>
    /// <remarks>
    /// This method accepts a nullable activity because <see cref="StartActivity"/> may return
    /// <see langword="null"/> when telemetry is not recording.
    /// </remarks>
    public static void MarkSuccess(Activity? activity)
    {
        if (activity == null)
            return;

        activity.SetStatus(ActivityStatusCode.Ok);
        activity.SetTag(OpenClawTelemetryTagKey.Outcome.ToTelemetryName(), OutcomeSuccess);
    }

    /// <summary>
    /// Marks a manually-created span as canceled without setting error status.
    /// </summary>
    /// <remarks>
    /// Use this for expected cancellation paths so observability backends do not count normal cancellation as
    /// a failed operation.
    /// </remarks>
    public static void MarkCanceled(Activity? activity) =>
        activity?.SetTag(OpenClawTelemetryTagKey.Outcome.ToTelemetryName(), OutcomeCanceled);

    /// <summary>
    /// Marks a manually-created span as failed and records the exception type.
    /// </summary>
    /// <remarks>
    /// The exception message is intentionally not recorded to avoid leaking user content or secrets.
    /// </remarks>
    public static void MarkFailure(Activity? activity, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (activity == null)
            return;

        activity.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
        activity.SetTag(OpenClawTelemetryTagKey.Outcome.ToTelemetryName(), OutcomeFailure);
        activity.SetTag(OpenClawTelemetryTagKey.ErrorType.ToTelemetryName(), exception.GetType().FullName);
    }
}
