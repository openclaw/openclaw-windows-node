namespace OpenClawTray.Chat;

/// <summary>Separates a pointer gesture from queued keyboard/automation value changes.</summary>
internal sealed class ChatEffortInputPolicy
{
    private long _revision;
    private uint? _pointer;
    private long _pointerRevision;

    public bool IsPointerActive => _pointer is not null;
    public long QueueValueChange() => ++_revision;
    public bool CanCommit(long revision) => _pointer is null && revision == _revision;

    public void BeginPointer(uint pointer)
    {
        _pointer = pointer;
        _pointerRevision = ++_revision;
    }

    public long? GetPointerRevision(uint pointer) => _pointer == pointer ? _pointerRevision : null;

    public bool CancelPointer(uint pointer, long revision)
    {
        if (_pointer != pointer || _pointerRevision != revision)
            return false;
        CancelPending();
        return true;
    }

    public bool EndPointer(uint pointer)
    {
        if (_pointer != pointer)
            return false;
        CancelPending();
        return true;
    }

    public void CancelPending()
    {
        _pointer = null;
        _revision++;
    }
}
