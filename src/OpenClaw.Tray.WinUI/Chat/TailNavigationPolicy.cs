namespace OpenClawTray.Chat;

internal readonly record struct TailNavigationRequest(int Index, string DisplayedTailKey);

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
