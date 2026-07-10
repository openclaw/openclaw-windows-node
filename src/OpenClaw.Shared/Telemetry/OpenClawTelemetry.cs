using System.Diagnostics;

namespace OpenClaw.Shared.Telemetry;

/// <summary>
/// Small helpers for OpenClaw code paths that want to expose traced spans.
/// Exporter configuration remains owned by the app process.
/// </summary>
public static class OpenClawTelemetry
{
    public static Activity? StartActivity(
        ActivitySource source,
        string spanName,
        IEnumerable<OpenClawTelemetryTag>? tags = null,
        System.Diagnostics.ActivityKind kind = System.Diagnostics.ActivityKind.Internal)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(spanName))
            throw new ArgumentException("Span name cannot be empty.", nameof(spanName));

        var activity = source.StartActivity(spanName, kind);
        ApplyTags(activity, tags);
        return activity;
    }

    public static void Trace(
        ActivitySource source,
        string spanName,
        Action action,
        IEnumerable<OpenClawTelemetryTag>? tags = null,
        System.Diagnostics.ActivityKind kind = System.Diagnostics.ActivityKind.Internal)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var activity = StartActivity(source, spanName, tags, kind);

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

    public static T Trace<T>(
        ActivitySource source,
        string spanName,
        Func<T> action,
        IEnumerable<OpenClawTelemetryTag>? tags = null,
        System.Diagnostics.ActivityKind kind = System.Diagnostics.ActivityKind.Internal)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var activity = StartActivity(source, spanName, tags, kind);

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

    public static async Task TraceAsync(
        ActivitySource source,
        string spanName,
        Func<CancellationToken, Task> action,
        IEnumerable<OpenClawTelemetryTag>? tags = null,
        System.Diagnostics.ActivityKind kind = System.Diagnostics.ActivityKind.Internal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var activity = StartActivity(source, spanName, tags, kind);

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

    public static async Task<T> TraceAsync<T>(
        ActivitySource source,
        string spanName,
        Func<CancellationToken, Task<T>> action,
        IEnumerable<OpenClawTelemetryTag>? tags = null,
        System.Diagnostics.ActivityKind kind = System.Diagnostics.ActivityKind.Internal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var activity = StartActivity(source, spanName, tags, kind);

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

    private static void ApplyTags(Activity? activity, IEnumerable<OpenClawTelemetryTag>? tags)
    {
        if (activity == null || tags == null)
            return;

        foreach (var tag in tags)
            activity.SetTag(tag.Key, tag.Value);
    }

    private static void MarkSuccess(Activity? activity) =>
        activity?.SetStatus(ActivityStatusCode.Ok);

    private static void MarkCanceled(Activity? activity) =>
        activity?.SetTag(OpenClawTelemetryTags.Outcome, "canceled");

    private static void MarkFailure(Activity? activity, Exception ex)
    {
        if (activity == null)
            return;

        activity.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
        activity.SetTag(OpenClawTelemetryTags.ErrorType, ex.GetType().FullName);
    }
}
