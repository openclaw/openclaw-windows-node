using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace OpenClaw.Connection;

/// <summary>
/// Owns a loopback forward port after SSH teardown so a different process
/// cannot accept a dashboard URL that was already handed to the browser.
/// The listener stays up while a replacement SSH process binds a backend port.
/// </summary>
internal static class DashboardForwardPortGuard
{
    private sealed class Slot
    {
        public required TcpListener Listener { get; init; }
        public TcpListener? V6Listener { get; init; }
        public required CancellationTokenSource Cancel { get; init; }
        public int? BackendPort { get; set; }
        public int? BackendProcessId { get; set; }
        public string? User { get; set; }
        public string? Host { get; set; }
        public int? RemotePort { get; set; }
        public int? SshPort { get; set; }
        public DateTime? BackendStartTimeUtc { get; set; }
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<int, Slot> Listeners = new();

    internal static void Hold(int port)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        lock (Gate)
        {
            if (Listeners.ContainsKey(port))
                return;

            var listener = new TcpListener(IPAddress.Loopback, port);
            TcpListener? v6 = null;
            try
            {
                listener.Start();
                if (Socket.OSSupportsIPv6)
                {
                    v6 = new TcpListener(IPAddress.IPv6Loopback, port);
                    v6.Server.DualMode = false;
                    v6.Start();
                }
            }
            catch
            {
                try { listener.Stop(); } catch (SocketException) { }
                try { v6?.Stop(); } catch (SocketException) { }
                throw;
            }

            var cancel = new CancellationTokenSource();
            var slot = new Slot { Listener = listener, V6Listener = v6, Cancel = cancel };
            Listeners[port] = slot;
            _ = AcceptAsync(port, slot, listener);
            if (v6 is not null)
                _ = AcceptAsync(port, slot, v6);
        }
    }

    internal static void ClearBackend(int port)
    {
        lock (Gate)
        {
            if (!Listeners.TryGetValue(port, out var slot))
                return;
            slot.BackendPort = null;
            slot.BackendProcessId = null;
            slot.BackendStartTimeUtc = null;
        }
    }

    internal static bool AllowsDestination(int port, string user, string host, int remotePort, int sshPort)
    {
        lock (Gate)
        {
            if (!Listeners.TryGetValue(port, out var slot))
                return true;
            if (slot.User is null || slot.Host is null || slot.RemotePort is null || slot.SshPort is null)
            {
                slot.User = user;
                slot.Host = host;
                slot.RemotePort = remotePort;
                slot.SshPort = sshPort;
                return true;
            }

            return string.Equals(slot.User, user, StringComparison.Ordinal) &&
                string.Equals(slot.Host, host, StringComparison.Ordinal) &&
                slot.RemotePort == remotePort &&
                slot.SshPort == sshPort;
        }
    }

    internal static void SetBackend(int port, int backendPort, int backendProcessId, DateTime backendStartTimeUtc)
    {
        lock (Gate)
        {
            if (Listeners.TryGetValue(port, out var slot))
            {
                slot.BackendPort = backendPort;
                slot.BackendProcessId = backendProcessId;
                slot.BackendStartTimeUtc = backendStartTimeUtc;
            }
        }
    }

    internal static void Release(int port)
    {
        Slot? slot;
        lock (Gate)
        {
            if (!Listeners.Remove(port, out slot))
                return;
        }

        slot.Cancel.Cancel();
        try
        {
            slot.Listener.Stop();
        }
        catch (SocketException)
        {
        }

        try
        {
            slot.V6Listener?.Stop();
        }
        catch (SocketException)
        {
        }
    }

    internal static bool IsHolding(int port)
    {
        lock (Gate)
            return Listeners.ContainsKey(port);
    }

    internal static int? BackendPort(int port)
    {
        lock (Gate)
            return Listeners.TryGetValue(port, out var slot) ? slot.BackendPort : null;
    }

    private static async Task AcceptAsync(int port, Slot slot, TcpListener listener)
    {
        try
        {
            while (!slot.Cancel.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(slot.Cancel.Token).ConfigureAwait(false);
                _ = Pump(port, client);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task Pump(int port, TcpClient client)
    {
        using (client)
        {
            int? backendPort;
            int? backendProcessId;
            lock (Gate)
            {
                if (!Listeners.TryGetValue(port, out var slot))
                    return;
                backendPort = slot.BackendPort;
                backendProcessId = slot.BackendProcessId;
            }

            DateTime? backendStart;
            lock (Gate)
            {
                backendStart = Listeners.TryGetValue(port, out var remembered) ? remembered.BackendStartTimeUtc : null;
            }

            if (backendPort is not int target || backendProcessId is not int processId || processId <= 0)
                return;
            if (!ProcessStillStartedAt(processId, backendStart))
                return;

            try
            {
                using var backend = new TcpClient();
                backend.Connect(IPAddress.Loopback, target);
                if (backend.Client.LocalEndPoint is not IPEndPoint clientEnd ||
                    backend.Client.RemoteEndPoint is not IPEndPoint serverEnd ||
                    serverEnd.Port != target ||
                    !IPAddress.IsLoopback(serverEnd.Address) ||
                    !WindowsTcpListenerSnapshot.TryConfirmAcceptedLoopbackOwner(
                        serverEnd.Port,
                        clientEnd.Port,
                        processId))
                {
                    return;
                }

                var left = client.GetStream();
                var right = backend.GetStream();
                using var stop = new CancellationTokenSource();
                var toRight = left.CopyToAsync(right, stop.Token);
                var toLeft = right.CopyToAsync(left, stop.Token);
                await Task.WhenAny(toRight, toLeft).ConfigureAwait(false);
                stop.Cancel();
                client.Close();
                backend.Close();
                try
                {
                    await Task.WhenAll(toRight, toLeft).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static bool ProcessStillStartedAt(int processId, DateTime? startTimeUtc)
    {
        if (startTimeUtc is null)
            return false;

        try
        {
            using var process = Process.GetProcessById(processId);
            var actual = process.StartTime.ToUniversalTime();
            return Math.Abs((actual - startTimeUtc.Value).TotalSeconds) < 2;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

}
