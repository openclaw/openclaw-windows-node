namespace OpenClawTray.Presentation;

/// <summary>
/// WinUI-free state for reopening a flyout while its previous close is still running. WinUI raises
/// Closing even for Hide() on an already-closed flyout and then never raises Closed, so only the
/// close of a shown flyout counts. A request during a real close is replayed once, after it ends.
/// </summary>
internal sealed class DeferredFlyoutShowState
{
    private bool _shown;
    private bool _closing;
    private bool _pending;

    public void Opened()
    {
        _shown = true;
        _closing = false;
    }

    public void Closing(bool cancelled)
    {
        if (_shown && !cancelled) _closing = true;
    }

    public void Closed() => _shown = false;

    /// <summary>Returns true when the request must wait for the close to finish.</summary>
    public bool TryDefer()
    {
        if (!_closing) return false;
        _pending = true;
        return true;
    }

    public void Cancel() => _pending = false;

    /// <summary>Ends the close. Returns true exactly once for a deferred request to replay.</summary>
    public bool Settle()
    {
        _closing = false;
        if (!_pending) return false;
        _pending = false;
        return true;
    }
}
