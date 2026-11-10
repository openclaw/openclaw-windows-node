namespace OpenClawTray.Chat;

internal readonly record struct TailNavigationRequest(int Index, string DisplayedTailKey);

// Initial navigation defers coordinates until dispatch, but retains the originating generation.
internal readonly record struct InitialTailNavigationRequest(int Version)
{
    public bool TryResolve(
        int currentVersion,
        int currentTailIndex,
        string? currentDisplayedTailKey,
        int itemCount,
        out TailNavigationRequest request)
    {
        request = default;
        return Version == currentVersion
            && TailNavigationPolicy.TryCapture(
                currentTailIndex,
                currentDisplayedTailKey,
                itemCount,
                out request);
    }
}

internal static class TailNavigationPolicy
{
    public static bool TryCapture(
        int tailIndex,
        string? displayedTailKey,
        int itemCount,
        out TailNavigationRequest request)
    {
        if (tailIndex < 0 || tailIndex >= itemCount || string.IsNullOrEmpty(displayedTailKey))
        {
            request = default;
            return false;
        }

        request = new TailNavigationRequest(tailIndex, displayedTailKey);
        return true;
    }

    public static bool CanExecute(
        TailNavigationRequest request,
        int currentTailIndex,
        string? currentDisplayedTailKey,
        int itemCount) =>
        request.Index >= 0
        && request.Index < itemCount
        && request.Index == currentTailIndex
        && string.Equals(
            request.DisplayedTailKey,
            currentDisplayedTailKey,
            StringComparison.Ordinal);

    // Initial history positioning must land even if an intermediate view change
    // reported a position away from the tail; only reflow correction is follow-gated.
    public static bool CanDispatch(bool reflowCorrection, bool following) =>
        !reflowCorrection || following;

    public static bool CanFollowTail(bool following, bool initialPending) =>
        following && !initialPending;
}

internal sealed class TailNavigationQueue
{
    private (int Version, TailNavigationRequest Request)? _pending;

    public bool Enqueue(int version, TailNavigationRequest request)
    {
        _pending = (version, request);
        if (IsScheduled)
            return false;

        IsScheduled = true;
        return true;
    }

    public bool TryDequeue(int currentVersion, out TailNavigationRequest request)
    {
        IsScheduled = false;
        var pending = _pending;
        _pending = null;
        if (pending is not { } value || value.Version != currentVersion)
        {
            request = default;
            return false;
        }

        request = value.Request;
        return true;
    }

    public void Clear() => _pending = null;

    public void SchedulingFailed()
    {
        IsScheduled = false;
        _pending = null;
    }

    public bool IsScheduled { get; private set; }
}

internal enum InitialTailLanding
{
    Wait,
    Landed,
    Retry,
    CorrectOffset,
    GiveUp,
}

// Content-free value copy for the existing GiveUp warning. It never retains the
// target object or correlation/generation identities and cannot drive navigation.
internal readonly record struct InitialTailDiagnosticSnapshot(
    int ObservedFrames,
    int Retries,
    int SettledFrames,
    bool HasExpectedTarget,
    bool HasCorrelation,
    bool BringCompleted,
    bool AttemptActive);

// One attempt's numeric observations, never navigation inputs. Null means the
// read was unavailable; zero is a real observed offset. No native objects survive.
internal readonly record struct InitialTailAttemptDiagnostics(
    double? ProposedOffset,
    double? BringOffset,
    double? BringScrollableHeight,
    int? TargetIndex,
    double? CompletedOffset,
    double? CompletedScrollableHeight,
    bool? CompletionSourceItemMatches,
    double? MaxObservedOffset);

// Values at our callback only: later native listeners may still change the args.
// Null is an unavailable/failed read, not false. Never used to control navigation.
internal readonly record struct InitialTailBringOptionsObservation(
    bool? CancelAtCallback,
    bool? AnimationDesiredAtCallback);

// A dispatched bring-into-view can be dropped or reset by a concurrent layout
// (for example a session switch restoring cached rows), leaving the reader at
// the top. Initial positioning stays pending until the settled view is at the tail.
internal sealed class InitialTailConfirmation
{
    // ItemsView retains its bring target as an anchor for four rendering callbacks
    // after ScrollCompleted. Do not interrupt that native layout stabilization.
    public const int SettledFramesBeforeDecision = 4;
    public const int MaxRetries = 3;
    public const int MaxObservedFrames = 120;
    // Reserve an equal share of the existing observation budget for each attempt
    // when no native bring event arrives.
    public const int MissingBringFramesBeforeDecision = MaxObservedFrames / (MaxRetries + 1);

    private int _settledFrames;
    private int _retries;
    private int _observedFrames;
    private int _generation;
    private int? _correlationId;
    private object? _targetItem;
    private bool _bringCompleted;
    private bool _attemptActive;
    private bool _offsetCorrection;
    private InitialTailAttemptDiagnostics _attemptDiagnostics;
    private InitialTailBringOptionsObservation _bringOptionsObservation;

    public InitialTailBringOptionsObservation BringOptionsObservation => _bringOptionsObservation;

    public InitialTailAttemptDiagnostics AttemptDiagnostics => _attemptDiagnostics;

    public InitialTailDiagnosticSnapshot Diagnostics => new(
        _observedFrames, _retries, _settledFrames, _targetItem is not null,
        _correlationId is not null, _bringCompleted, _attemptActive);

    public int Reset(object? targetItem = null)
    {
        _retries = 0;
        _observedFrames = 0;
        BeginAttempt(targetItem);
        return ++_generation;
    }

    public bool IsCurrent(int generation) => generation == _generation;

    public bool CanCorrectOffset(object? targetItem) =>
        !_attemptActive && _bringCompleted && _correlationId is not null
        && targetItem is not null && ReferenceEquals(_targetItem, targetItem);

    public void Invalidate()
    {
        _generation++;
        _attemptActive = false;
        _targetItem = null;
        _attemptDiagnostics = default;
        _bringOptionsObservation = default;
    }

    public void BeginAttempt(object? targetItem = null)
    {
        _settledFrames = 0;
        _correlationId = null;
        _targetItem = targetItem;
        _bringCompleted = false;
        _attemptActive = true;
        _offsetCorrection = false;
        _attemptDiagnostics = default;
        _bringOptionsObservation = default;
    }

    public void OffsetCorrectionStarted(int correlationId)
    {
        if (!_attemptActive || _targetItem is null || correlationId < 0)
            return;

        _offsetCorrection = true;
        _correlationId = correlationId;
        _bringCompleted = false;
        _settledFrames = 0;
    }

    public void BringingIntoView(int correlationId, object? targetItem,
        (double ProposedOffset, double Offset, double ScrollableHeight)? geometry = null, int? targetIndex = null)
    {
        if (!_attemptActive || _offsetCorrection || correlationId < 0 || _targetItem is null
            || !ReferenceEquals(_targetItem, targetItem))
            return;

        _correlationId = correlationId;
        _bringCompleted = false;
        _settledFrames = 0;
        _bringOptionsObservation = default;
        _attemptDiagnostics = _attemptDiagnostics with
        {
            ProposedOffset = Finite(geometry?.ProposedOffset),
            BringOffset = Finite(geometry?.Offset),
            BringScrollableHeight = Finite(geometry?.ScrollableHeight),
            TargetIndex = targetIndex,
            CompletedOffset = null,
            CompletedScrollableHeight = null,
            CompletionSourceItemMatches = null,
        };
        ObserveOffset(geometry?.Offset);
    }

    public void ObserveBringOptions(int correlationId, object? targetItem,
        bool? cancelAtCallback, bool? animationDesiredAtCallback)
    {
        if (!_attemptActive || _correlationId != correlationId || _targetItem is null
            || !ReferenceEquals(_targetItem, targetItem))
            return;

        _bringOptionsObservation = new(cancelAtCallback, animationDesiredAtCallback);
    }

    public void ScrollCompleted(int correlationId,
        (double Offset, double ScrollableHeight)? geometry = null,
        object? currentSourceItem = null, bool sourceReadSucceeded = false)
    {
        if (!_attemptActive || _correlationId != correlationId)
            return;

        _bringCompleted = true;
        _settledFrames = 0;
        _attemptDiagnostics = _attemptDiagnostics with
        {
            CompletedOffset = Finite(geometry?.Offset),
            CompletedScrollableHeight = Finite(geometry?.ScrollableHeight),
            CompletionSourceItemMatches = sourceReadSucceeded
                ? _targetItem is not null && ReferenceEquals(_targetItem, currentSourceItem) : null,
        };
        ObserveOffset(geometry?.Offset);
    }

    public void ObserveOffset(double? offset)
    {
        if (_attemptActive && _correlationId is not null && Finite(offset) is { } value)
            _attemptDiagnostics = _attemptDiagnostics with
            {
                MaxObservedOffset = Math.Max(_attemptDiagnostics.MaxObservedOffset ?? value, value),
            };
    }

    private static double? Finite(double? value) =>
        value is { } number && double.IsFinite(number) ? number : null;

    public void GeometryChanged() => _settledFrames = 0;

    public InitialTailLanding Observe(bool idle, bool nearBottom)
    {
        if (++_observedFrames > MaxObservedFrames)
            return InitialTailLanding.GiveUp;

        if (!idle || !_attemptActive || (_correlationId is not null && !_bringCompleted))
        {
            _settledFrames = 0;
            return InitialTailLanding.Wait;
        }

        var framesToSettle = _bringCompleted
            ? SettledFramesBeforeDecision
            : MissingBringFramesBeforeDecision;
        if (++_settledFrames < framesToSettle)
            return InitialTailLanding.Wait;

        _attemptActive = false;
        if (nearBottom)
            return InitialTailLanding.Landed;

        if (_retries >= MaxRetries)
            return InitialTailLanding.GiveUp;

        _retries++;
        _settledFrames = 0;
        return _bringCompleted ? InitialTailLanding.CorrectOffset : InitialTailLanding.Retry;
    }
}

internal sealed class TailReflowCorrection
{
    private bool _available;

    public void Arm() => _available = true;

    // A pending initial request already targets the tail. Starting a correction
    // then would merge with it and impose the correction's follow gate on it.
    public bool TryConsume(bool following, bool nearBottom, bool initialPending)
    {
        if (!_available || !following || nearBottom || initialPending)
            return false;

        _available = false;
        return true;
    }

    public void Clear() => _available = false;
}
