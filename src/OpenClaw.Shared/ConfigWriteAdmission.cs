// Binds a queued configuration write to one observed Gateway profile.
// Usage: capture client.ConfigWriteAdmission with a config snapshot, then pass it
// to PatchConfigOnConnectionAsync together with the captured connection epoch.
using System.Net.WebSockets;

namespace OpenClaw.Shared;

/// <summary>A client-owned profile lifetime consumed at final configuration-send admission.</summary>
public sealed class ConfigWriteAdmission
{
    private readonly object _gate = new();
    private bool _current = true;

    internal ConfigWriteAdmission() { }

    /// <summary>Whether the profile that produced the snapshot is still current.</summary>
    public bool IsCurrent
    {
        get { lock (_gate) return _current; }
    }

    internal void Invalidate()
    {
        lock (_gate) _current = false;
    }

    internal bool TryStartSend(WebSocket socket, ReadOnlyMemory<byte> frame,
        CancellationToken transportCancellation, out ValueTask send)
    {
        lock (_gate)
        {
            if (!_current)
            {
                send = default;
                return false;
            }

            // This invocation is the admission linearization point. Invalidation
            // either wins before any I/O, or follows an already-admitted frame.
            // Do not hold the monitor while awaiting the shared socket write.
            send = socket.SendAsync(frame, WebSocketMessageType.Text, true, transportCancellation);
            return true;
        }
    }
}
