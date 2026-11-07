namespace OpenClaw.Connection;

/// <summary>
/// Carries an expected tunnel generation through an async restoration callback.
/// Implementations check and advance it under the lock protecting tunnel mutations.
/// It does not reserve the tunnel or block independent owners.
/// </summary>
public sealed class SshTunnelOwnershipGuard
{
    private readonly AsyncLocal<Scope?> _current = new();

    public Scope Begin(long expectedGeneration)
    {
        var scope = new Scope(this, _current.Value, expectedGeneration);
        _current.Value = scope;
        return scope;
    }

    public void RequireCurrent(long generation)
    {
        if (_current.Value is { } scope && scope.Generation != generation)
            throw new InvalidOperationException(
                "SSH tunnel ownership changed during rollback; the newer tunnel was preserved.");
    }

    public void Advance(long generation)
    {
        if (_current.Value is { } scope)
            scope.Generation = generation;
    }

    public sealed class Scope : IDisposable
    {
        private readonly SshTunnelOwnershipGuard _owner;
        private readonly Scope? _previous;
        private bool _disposed;

        internal Scope(SshTunnelOwnershipGuard owner, Scope? previous, long generation)
        {
            _owner = owner;
            _previous = previous;
            Generation = generation;
        }

        public long Generation { get; internal set; }

        public void Dispose()
        {
            if (_disposed)
                return;
            _owner._current.Value = _previous;
            _disposed = true;
        }
    }
}
