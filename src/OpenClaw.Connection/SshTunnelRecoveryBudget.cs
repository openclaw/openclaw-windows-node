namespace OpenClaw.Connection;

public sealed class SshTunnelRecoveryBudget
{
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    ];

    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _attempts = new();
    private SshTunnelConfig? _tunnel;
    private SshTunnelOwner _owner;

    public bool TryReserve(
        SshTunnelExit tunnelExit,
        DateTimeOffset now,
        out TimeSpan delay)
    {
        lock (_gate)
        {
            if (!Equals(_tunnel, tunnelExit.Tunnel) || _owner != tunnelExit.Owner)
            {
                _attempts.Clear();
                _tunnel = tunnelExit.Tunnel;
                _owner = tunnelExit.Owner;
            }

            while (_attempts.TryPeek(out var attempt) && now - attempt >= AttemptWindow)
                _attempts.Dequeue();

            if (_attempts.Count >= RetryDelays.Length)
            {
                delay = TimeSpan.Zero;
                return false;
            }

            delay = RetryDelays[_attempts.Count];
            _attempts.Enqueue(now);
            return true;
        }
    }

    public void ReportRecovered(SshTunnelExit tunnelExit)
    {
        lock (_gate)
        {
            if (!Equals(_tunnel, tunnelExit.Tunnel) || _owner != tunnelExit.Owner)
                return;

            ResetLocked();
        }
    }

    public void Reset()
    {
        lock (_gate)
            ResetLocked();
    }

    private void ResetLocked()
    {
        _attempts.Clear();
        _tunnel = null;
        _owner = SshTunnelOwner.Unspecified;
    }
}
