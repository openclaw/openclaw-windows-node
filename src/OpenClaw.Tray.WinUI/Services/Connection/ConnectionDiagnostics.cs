using OpenClaw.Shared;

namespace OpenClawTray.Services.Connection;

/// <summary>
/// Fixed-capacity ring buffer of timestamped connection diagnostic events.
/// Thread-safe via lock. Fires <see cref="EventRecorded"/> synchronously.
/// </summary>
public sealed class ConnectionDiagnostics
{
    private readonly ConnectionDiagnosticEvent[] _buffer;
    private readonly IClock _clock;
    private int _head; // next write position
    private int _count;
    private readonly object _lock = new();

    public event EventHandler<ConnectionDiagnosticEvent>? EventRecorded;

    public ConnectionDiagnostics(int capacity = 500, IClock? clock = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new ConnectionDiagnosticEvent[capacity];
        _clock = clock ?? SystemClock.Instance;
    }

    public int Capacity => _buffer.Length;

    public void Record(string category, string message, string? detail = null)
    {
        var evt = new ConnectionDiagnosticEvent(_clock.UtcNow, category, message, detail);
        lock (_lock)
        {
            _buffer[_head] = evt;
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length) _count++;
        }
        EventRecorded?.Invoke(this, evt);
    }

    public void RecordStateChange(OverallConnectionState from, OverallConnectionState to)
    {
        Record("state", $"{from} → {to}");
    }

    public void RecordCredentialResolution(GatewayCredential? credential)
    {
        if (credential == null)
            Record("credential", "No credential resolved");
        else
            Record("credential", $"Resolved: {credential.Source}", $"IsBootstrap={credential.IsBootstrapToken}");
    }

    public void RecordWebSocketEvent(string eventName, string? detail = null)
    {
        Record("websocket", eventName, detail);
    }

    public IReadOnlyList<ConnectionDiagnosticEvent> GetRecent(int count = 100)
    {
        lock (_lock)
        {
            var result = new List<ConnectionDiagnosticEvent>(Math.Min(count, _count));
            var start = (_head - Math.Min(count, _count) + _buffer.Length) % _buffer.Length;
            for (int i = 0; i < Math.Min(count, _count); i++)
            {
                result.Add(_buffer[(start + i) % _buffer.Length]);
            }
            return result;
        }
    }

    public IReadOnlyList<ConnectionDiagnosticEvent> GetAll()
    {
        lock (_lock)
        {
            var result = new List<ConnectionDiagnosticEvent>(_count);
            var start = (_head - _count + _buffer.Length) % _buffer.Length;
            for (int i = 0; i < _count; i++)
            {
                result.Add(_buffer[(start + i) % _buffer.Length]);
            }
            return result;
        }
    }

    public int Count
    {
        get { lock (_lock) return _count; }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_buffer);
            _head = 0;
            _count = 0;
        }
    }
}
