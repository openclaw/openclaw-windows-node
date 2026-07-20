using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace OpenClaw.TestSupport;

/// <summary>
/// Loopback WebSocket server that completes the upgrade handshake and then stays
/// silent — the client connects successfully but never receives a message, so code
/// that waits for a gateway challenge/response parks in its wait. Lets a test drive
/// the real <c>ClientWebSocket</c> connect path and then exercise cancellation while
/// the wait is in flight, without any running gateway. Shared single source so the
/// helper can't drift across test projects.
/// </summary>
public sealed class SilentWebSocketServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _upgraded =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The loopback port the server is listening on (use as <c>ws://127.0.0.1:{Port}</c>).</summary>
    public int Port { get; }

    /// <summary>Completes once the server has sent the WebSocket 101 upgrade to a client — a
    /// deterministic barrier a test can await before cancelling, instead of guessing a delay.</summary>
    public Task UpgradeCompleted => _upgraded.Task;

    public SilentWebSocketServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = HandleAsync(tcp);
        }
    }

    private async Task HandleAsync(TcpClient tcp)
    {
        try
        {
            var stream = tcp.GetStream();
            var buf = new byte[16384];
            var sb = new StringBuilder();
            while (!sb.ToString().Contains("\r\n\r\n"))
            {
                var n = await stream.ReadAsync(buf, _cts.Token);
                if (n <= 0) return;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
            }
            var key = sb.ToString().Split("\r\n")
                .FirstOrDefault(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                ?.Split(':', 2)[1].Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(
                Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var resp = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n" +
                       "Connection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(resp), _cts.Token);
            _upgraded.TrySetResult();
            await Task.Delay(Timeout.Infinite, _cts.Token);
        }
        catch { /* torn down with the test */ }
        finally { try { tcp.Dispose(); } catch { } }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
