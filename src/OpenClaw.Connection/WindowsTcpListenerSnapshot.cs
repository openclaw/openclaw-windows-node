using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace OpenClaw.Connection;

public sealed record WindowsTcpListenerInfo(
    IPAddress Address,
    int Port,
    int ProcessId,
    string? ProcessName,
    string? ProcessPath,
    DateTime? ProcessStartTimeUtc = null);

public sealed record WindowsTcpListenerSnapshotResult(
    IReadOnlyList<WindowsTcpListenerInfo> Listeners,
    bool Ipv4Complete,
    bool Ipv6Complete);

/// <summary>Address-specific TCP listener ownership from the Windows IP Helper API.</summary>
public static class WindowsTcpListenerSnapshot
{
    public const uint TcpStateEstablished = 5;

    public static WindowsTcpListenerSnapshotResult Capture()
    {
        if (!OperatingSystem.IsWindows())
            return new([], Ipv4Complete: false, Ipv6Complete: false);

        var result = new List<WindowsTcpListenerInfo>();
        var ipv4Complete = CaptureIpv4(result);
        var ipv6Complete = CaptureIpv6(result);
        return new(result, ipv4Complete, ipv6Complete);
    }

    /// <summary>
    /// True when an established TCP row is using <paramref name="port"/> on loopback.
    /// Listeners are not established, so the owned SSH socket alone does not match.
    /// </summary>
    public static bool HasEstablishedLoopbackConnection(int port)
    {
        if (!OperatingSystem.IsWindows() || port is < 1 or > 65535)
            return false;

        try
        {
            return ScanEstablished(AfInet, port, ipv6: false) ||
                ScanEstablished(AfInet6, port, ipv6: true);
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"Established TCP lookup failed for port {port}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public static bool IsEstablishedLoopbackForwardUse(
        uint state,
        IPAddress localAddress,
        int localPort,
        IPAddress remoteAddress,
        int remotePort,
        int forwardPort)
    {
        if (state != TcpStateEstablished || forwardPort is < 1 or > 65535)
            return false;
        if (localPort == forwardPort && IPAddress.IsLoopback(localAddress))
            return true;
        if (remotePort == forwardPort && IPAddress.IsLoopback(remoteAddress))
            return true;
        return false;
    }

    public static string EstablishedForwardKey(
        IPAddress localAddress,
        int localPort,
        IPAddress remoteAddress,
        int remotePort,
        int processId) =>
        $"{localAddress}|{localPort}|{remoteAddress}|{remotePort}|{processId}";

    public static bool IsUnseenEstablishedForwardUse(
        uint state,
        IPAddress localAddress,
        int localPort,
        IPAddress remoteAddress,
        int remotePort,
        int processId,
        int forwardPort,
        int? browserProcessId,
        IReadOnlySet<string> seen)
    {
        if (!IsEstablishedLoopbackForwardUse(
                state,
                localAddress,
                localPort,
                remoteAddress,
                remotePort,
                forwardPort))
        {
            return false;
        }

        // An accepted SSH socket is not the browser that opened the dashboard.
        if (localPort == forwardPort || remotePort != forwardPort)
            return false;
        if (browserProcessId is not int browser || browser <= 0 || processId != browser)
            return false;

        var key = EstablishedForwardKey(localAddress, localPort, remoteAddress, remotePort, processId);
        return !seen.Contains(key);
    }

    private readonly record struct EstablishedTcpRow(
        uint State,
        IPAddress LocalAddress,
        int LocalPort,
        IPAddress RemoteAddress,
        int RemotePort,
        int ProcessId);

    public static string? GetProcessCommandLine(int processId)
    {
        if (processId <= 0)
            return null;

        try
        {
            var psi = new ProcessStartInfo(
                "powershell.exe",
                $"-NoProfile -Command \"(Get-CimInstance Win32_Process -Filter 'ProcessId={processId}').CommandLine\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
                return null;

            var readTask = process.StandardOutput.ReadToEndAsync();
            var output = AwaitRedirectedOutput(process, readTask, timeoutMs: 5_000);
            return output?.Trim();
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"Windows process command-line lookup failed for PID {processId}: " +
                $"{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Wait for the child, then drain redirected stdout with the leftover
    /// timeout. WaitForExit returns when the child exits, but ReadToEnd
    /// completes only after the write end of the pipe closes. A descendant
    /// that inherited stdout can keep the pipe open, so unbounded
    /// GetResult() would hang past the inspection timeout.
    /// </summary>
    internal static string? AwaitRedirectedOutput(Process process, Task<string> readTask, int timeoutMs)
    {
        const int minDrainMs = 250;
        var sw = Stopwatch.StartNew();
        if (!process.WaitForExit(timeoutMs))
        {
            Trace.WriteLine(
                $"Windows process command-line lookup timed out waiting for PID {process.Id}.");
            try { process.Kill(entireProcessTree: true); } catch { }
            AbandonRead(process, readTask);
            return null;
        }

        var elapsedMs = (int)Math.Min(sw.ElapsedMilliseconds, timeoutMs);
        var drainBudgetMs = Math.Max(timeoutMs - elapsedMs, minDrainMs);
        try
        {
            if (!readTask.Wait(drainBudgetMs))
            {
                Trace.WriteLine(
                    $"Windows process command-line lookup timed out draining PID {process.Id} stdout.");
                try { process.Kill(entireProcessTree: true); } catch { }
                AbandonRead(process, readTask);
                return null;
            }
        }
        catch (AggregateException)
        {
            return null;
        }

        return readTask.Status == TaskStatus.RanToCompletion ? readTask.Result : null;
    }

    private static void AbandonRead(Process process, Task readTask)
    {
        ObserveQuietly(readTask);
        try { process.StandardOutput.Dispose(); } catch { }
    }

    private static void ObserveQuietly(Task task) =>
        _ = task.ContinueWith(
            static t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static bool CaptureIpv4(List<WindowsTcpListenerInfo> destination)
    {
        return CaptureTable(
            AfInet,
            Marshal.SizeOf<MibTcpRowOwnerPid>(),
            rowPtr =>
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                var address = new IPAddress(BitConverter.GetBytes(row.LocalAddress));
                return (address, ReadPort(row.LocalPort), unchecked((int)row.OwningProcessId));
            },
            destination);
    }

    private static bool CaptureIpv6(List<WindowsTcpListenerInfo> destination)
    {
        return CaptureTable(
            AfInet6,
            Marshal.SizeOf<MibTcp6RowOwnerPid>(),
            rowPtr =>
            {
                var row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(rowPtr);
                var address = new IPAddress(row.LocalAddress, row.LocalScopeId);
                return (address, ReadPort(row.LocalPort), unchecked((int)row.OwningProcessId));
            },
            destination);
    }

    private static bool CaptureTable(
        int addressFamily,
        int rowSize,
        Func<IntPtr, (IPAddress Address, int Port, int ProcessId)> readRow,
        List<WindowsTcpListenerInfo> destination)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var bufferLength = 0;
            var status = GetExtendedTcpTable(
                IntPtr.Zero,
                ref bufferLength,
                sort: true,
                ipVersion: addressFamily,
                tableClass: TcpTableOwnerPidListener,
                reserved: 0);
            if (status != ErrorInsufficientBuffer || bufferLength <= 0)
                return false;

            var tablePtr = Marshal.AllocHGlobal(bufferLength);
            try
            {
                status = GetExtendedTcpTable(
                    tablePtr,
                    ref bufferLength,
                    sort: true,
                    ipVersion: addressFamily,
                    tableClass: TcpTableOwnerPidListener,
                    reserved: 0);
                if (status == ErrorInsufficientBuffer)
                    continue; // listener table grew between size/read calls
                if (status != ErrorSuccess)
                    return false;

                var rowCount = Marshal.ReadInt32(tablePtr);
                var rowPtr = IntPtr.Add(tablePtr, sizeof(int));
                var captured = new List<WindowsTcpListenerInfo>(rowCount);
                for (var i = 0; i < rowCount; i++)
                {
                    var row = readRow(rowPtr);
                    if (row.Port is >= 1 and <= 65535)
                    {
                        ResolveProcess(
                            row.ProcessId,
                            out var processName,
                            out var processPath,
                            out var processStartTimeUtc);
                        captured.Add(new WindowsTcpListenerInfo(
                            row.Address,
                            row.Port,
                            row.ProcessId,
                            processName,
                            processPath,
                            processStartTimeUtc));
                    }
                    rowPtr = IntPtr.Add(rowPtr, rowSize);
                }
                destination.AddRange(captured);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(tablePtr);
            }
        }
        return false;
    }

    public static bool TryConfirmAcceptedLoopbackOwner(int listenPort, int clientPort, int processId)
    {
        if (!OperatingSystem.IsWindows() ||
            listenPort is < 1 or > 65535 ||
            clientPort is < 1 or > 65535 ||
            processId <= 0)
        {
            return false;
        }

        var found = false;
        var complete = true;
        bool Visit(EstablishedTcpRow row)
        {
            if (row.State == TcpStateEstablished &&
                row.LocalPort == listenPort &&
                row.RemotePort == clientPort &&
                IPAddress.IsLoopback(row.LocalAddress) &&
                IPAddress.IsLoopback(row.RemoteAddress) &&
                row.ProcessId == processId)
            {
                found = true;
                return true;
            }

            return false;
        }

        VisitEstablished(AfInet, ipv6: false, Visit, ref complete);
        if (!found)
            VisitEstablished(AfInet6, ipv6: true, Visit, ref complete);
        return found && complete;
    }

    private static void VisitEstablished(
        int addressFamily,
        bool ipv6,
        Func<EstablishedTcpRow, bool> visit,
        ref bool complete)
    {
        var rowSize = ipv6
            ? Marshal.SizeOf<MibTcp6RowOwnerPid>()
            : Marshal.SizeOf<MibTcpRowOwnerPid>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var bufferLength = 0;
            var status = GetExtendedTcpTable(
                IntPtr.Zero,
                ref bufferLength,
                sort: true,
                ipVersion: addressFamily,
                tableClass: TcpTableOwnerPidAll,
                reserved: 0);
            if (status != ErrorInsufficientBuffer || bufferLength <= 0)
            {
                complete = false;
                return;
            }

            var tablePtr = Marshal.AllocHGlobal(bufferLength);
            try
            {
                status = GetExtendedTcpTable(
                    tablePtr,
                    ref bufferLength,
                    sort: true,
                    ipVersion: addressFamily,
                    tableClass: TcpTableOwnerPidAll,
                    reserved: 0);
                if (status == ErrorInsufficientBuffer)
                    continue;
                if (status != ErrorSuccess)
                {
                    complete = false;
                    return;
                }

                var rowCount = Marshal.ReadInt32(tablePtr);
                var rowPtr = IntPtr.Add(tablePtr, sizeof(int));
                for (var index = 0; index < rowCount; index++)
                {
                    if (visit(ReadEstablishedRow(rowPtr, ipv6)))
                        return;
                    rowPtr = IntPtr.Add(rowPtr, rowSize);
                }

                return;
            }
            finally
            {
                Marshal.FreeHGlobal(tablePtr);
            }
        }

        complete = false;
    }

    private static EstablishedTcpRow ReadEstablishedRow(IntPtr rowPtr, bool ipv6)
    {
        if (ipv6)
        {
            var row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(rowPtr);
            return new EstablishedTcpRow(
                row.State,
                new IPAddress(row.LocalAddress, row.LocalScopeId),
                ReadPort(row.LocalPort),
                new IPAddress(row.RemoteAddress, row.RemoteScopeId),
                ReadPort(row.RemotePort),
                unchecked((int)row.OwningProcessId));
        }

        var ipv4 = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
        return new EstablishedTcpRow(
            ipv4.State,
            new IPAddress(BitConverter.GetBytes(ipv4.LocalAddress)),
            ReadPort(ipv4.LocalPort),
            new IPAddress(BitConverter.GetBytes(ipv4.RemoteAddress)),
            ReadPort(ipv4.RemotePort),
            unchecked((int)ipv4.OwningProcessId));
    }

    private static bool ScanEstablished(int addressFamily, int port, bool ipv6)
    {
        var rowSize = ipv6
            ? Marshal.SizeOf<MibTcp6RowOwnerPid>()
            : Marshal.SizeOf<MibTcpRowOwnerPid>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var bufferLength = 0;
            var status = GetExtendedTcpTable(
                IntPtr.Zero,
                ref bufferLength,
                sort: true,
                ipVersion: addressFamily,
                tableClass: TcpTableOwnerPidAll,
                reserved: 0);
            if (status != ErrorInsufficientBuffer || bufferLength <= 0)
                return false;

            var tablePtr = Marshal.AllocHGlobal(bufferLength);
            try
            {
                status = GetExtendedTcpTable(
                    tablePtr,
                    ref bufferLength,
                    sort: true,
                    ipVersion: addressFamily,
                    tableClass: TcpTableOwnerPidAll,
                    reserved: 0);
                if (status == ErrorInsufficientBuffer)
                    continue;
                if (status != ErrorSuccess)
                    return false;

                var rowCount = Marshal.ReadInt32(tablePtr);
                var rowPtr = IntPtr.Add(tablePtr, sizeof(int));
                for (var index = 0; index < rowCount; index++)
                {
                    if (ReadEstablishedLoopback(rowPtr, ipv6, port))
                        return true;
                    rowPtr = IntPtr.Add(rowPtr, rowSize);
                }

                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(tablePtr);
            }
        }

        return false;
    }

    private static bool ReadEstablishedLoopback(IntPtr rowPtr, bool ipv6, int port)
    {
        if (ipv6)
        {
            var row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(rowPtr);
            var local = new IPAddress(row.LocalAddress, row.LocalScopeId);
            var remote = new IPAddress(row.RemoteAddress, row.RemoteScopeId);
            return IsEstablishedLoopbackForwardUse(
                row.State,
                local,
                ReadPort(row.LocalPort),
                remote,
                ReadPort(row.RemotePort),
                port);
        }

        var ipv4 = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
        return IsEstablishedLoopbackForwardUse(
            ipv4.State,
            new IPAddress(BitConverter.GetBytes(ipv4.LocalAddress)),
            ReadPort(ipv4.LocalPort),
            new IPAddress(BitConverter.GetBytes(ipv4.RemoteAddress)),
            ReadPort(ipv4.RemotePort),
            port);
    }

    private static int ReadPort(byte[] bytes) =>
        bytes is { Length: >= 2 } ? (bytes[0] << 8) + bytes[1] : 0;

    private static void ResolveProcess(
        int processId,
        out string? processName,
        out string? processPath,
        out DateTime? processStartTimeUtc)
    {
        processName = null;
        processPath = null;
        processStartTimeUtc = null;
        if (processId <= 0)
            return;

        try
        {
            using var process = Process.GetProcessById(processId);
            processName = process.ProcessName;
            try { processPath = process.MainModule?.FileName; } catch { }
            try { processStartTimeUtc = process.StartTime.ToUniversalTime(); } catch { }
        }
        catch
        {
        }
    }

    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private const int TcpTableOwnerPidAll = 5;
    private const uint ErrorSuccess = 0;
    private const uint ErrorInsufficientBuffer = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int tcpTableLength,
        bool sort,
        int ipVersion,
        int tableClass,
        uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] LocalPort;
        public uint RemoteAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] RemotePort;
        public uint OwningProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddress;
        public uint LocalScopeId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddress;
        public uint RemoteScopeId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] RemotePort;
        public uint State;
        public uint OwningProcessId;
    }
}
