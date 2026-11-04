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

    // ScrollPresenter uses a wrapping 31-bit counter. Outstanding operations span
    // less than half that range, so a wrapped new ID still follows the boundary.
    public static bool CanCancelForScrollCompletion(int correlationId, int requestBoundary) =>
        correlationId >= 0
        && (requestBoundary < 0
            || ((correlationId - requestBoundary) & int.MaxValue) < (1 << 30));
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
