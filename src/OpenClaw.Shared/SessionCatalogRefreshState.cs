namespace OpenClaw.Shared;

/// <summary>
/// U2 companion reliability: tracks whether a session-catalog refresh was ignored
/// because it returned no rows while a valid catalog is still held. The UI layer can
/// surface a retry/error state from this without ever clearing the held catalog.
/// </summary>
internal sealed class SessionCatalogRefreshState
{
    private readonly object _gate = new();
    private int _ignoredEmptyRefreshes;
    private long _lastIgnoredUnixMs;

    /// <summary>True while at least one empty refresh has been ignored and no successful refresh has followed.</summary>
    public bool RetryRequired
    {
        get { lock (_gate) { return _ignoredEmptyRefreshes > 0; } }
    }

    public int IgnoredEmptyRefreshes
    {
        get { lock (_gate) { return _ignoredEmptyRefreshes; } }
    }

    public long LastIgnoredUnixMs
    {
        get { lock (_gate) { return _lastIgnoredUnixMs; } }
    }

    public void RecordIgnoredEmpty(long nowUnixMs)
    {
        lock (_gate)
        {
            _ignoredEmptyRefreshes++;
            _lastIgnoredUnixMs = nowUnixMs;
        }
    }

    public void RecordSuccessfulRefresh()
    {
        lock (_gate)
        {
            _ignoredEmptyRefreshes = 0;
        }
    }
}
