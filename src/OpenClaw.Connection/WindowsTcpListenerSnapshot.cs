using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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
    public static WindowsTcpListenerSnapshotResult Capture()
    {
        if (!OperatingSystem.IsWindows())
            return new([], Ipv4Complete: false, Ipv6Complete: false);

        var result = new List<WindowsTcpListenerInfo>();
        var ipv4Complete = CaptureIpv4(result);
        var ipv6Complete = CaptureIpv6(result);
        return new(result, ipv4Complete, ipv6Complete);
    }

    public readonly record struct AcceptedTcpEndpoint(
        IPAddress LocalAddress,
        int LocalPort,
        IPAddress RemoteAddress,
        int RemotePort,
        int ProcessId);

    public static int? MatchAcceptedProcess(
        IEnumerable<AcceptedTcpEndpoint> rows,
        IPEndPoint server,
        IPEndPoint client)
    {
        foreach (var row in rows)
        {
            if (row.LocalPort == server.Port &&
                row.RemotePort == client.Port &&
                AddressesMatch(row.LocalAddress, server.Address) &&
                AddressesMatch(row.RemoteAddress, client.Address))
                return row.ProcessId;
        }

        return null;
    }

    public static WindowsTcpListenerInfo? MatchListener(
        IEnumerable<WindowsTcpListenerInfo> listeners,
        IPEndPoint connected)
    {
        WindowsTcpListenerInfo? wildcard = null;
        foreach (var item in listeners)
        {
            if (item.Port != connected.Port)
                continue;
            if (AddressesMatch(item.Address, connected.Address))
                return item;
            if (wildcard is null && IsSameFamilyWildcard(item.Address, connected.Address))
                wildcard = item;
        }

        return wildcard;
    }

    public static int? AcceptedProcessId(IPEndPoint server, IPEndPoint client)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var rows = new List<AcceptedTcpEndpoint>();
        if (!CaptureConnections(rows))
            return null;
        return MatchAcceptedProcess(rows, server, client);
    }

    internal static bool AddressesMatch(IPAddress left, IPAddress right)
    {
        if (left.Equals(right))
            return true;
        if (left.IsIPv4MappedToIPv6 && left.MapToIPv4().Equals(right))
            return true;
        if (right.IsIPv4MappedToIPv6 && right.MapToIPv4().Equals(left))
            return true;
        return false;
    }

    private static bool IsSameFamilyWildcard(IPAddress listener, IPAddress connected)
    {
        if (connected.AddressFamily == AddressFamily.InterNetwork)
            return listener.Equals(IPAddress.Any);
        if (connected.AddressFamily == AddressFamily.InterNetworkV6)
            return listener.Equals(IPAddress.IPv6Any);
        return false;
    }

    private static bool CaptureConnections(List<AcceptedTcpEndpoint> rows)
    {
        var ipv4 = CaptureConnectionFamily(AfInet, rows);
        var ipv6 = CaptureConnectionFamily(AfInet6, rows);
        return ipv4 || ipv6;
    }

    private static bool CaptureConnectionFamily(int addressFamily, List<AcceptedTcpEndpoint> rows)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var bufferLength = 0;
            var status = GetExtendedTcpTable(
                IntPtr.Zero,
                ref bufferLength,
                sort: true,
                ipVersion: addressFamily,
                tableClass: TcpTableOwnerPidConnections,
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
                    tableClass: TcpTableOwnerPidConnections,
                    reserved: 0);
                if (status == ErrorInsufficientBuffer)
                    continue;
                if (status != ErrorSuccess)
                    return false;

                var rowCount = Marshal.ReadInt32(tablePtr);
                var rowPtr = IntPtr.Add(tablePtr, sizeof(int));
                var rowSize = addressFamily == AfInet6
                    ? Marshal.SizeOf<MibTcp6RowOwnerPid>()
                    : Marshal.SizeOf<MibTcpRowOwnerPid>();
                for (var i = 0; i < rowCount; i++)
                {
                    if (addressFamily == AfInet6)
                    {
                        var row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(rowPtr);
                        rows.Add(new AcceptedTcpEndpoint(
                            new IPAddress(row.LocalAddress, row.LocalScopeId),
                            ReadPort(row.LocalPort),
                            new IPAddress(row.RemoteAddress, row.RemoteScopeId),
                            ReadPort(row.RemotePort),
                            unchecked((int)row.OwningProcessId)));
                    }
                    else
                    {
                        var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                        rows.Add(new AcceptedTcpEndpoint(
                            new IPAddress(BitConverter.GetBytes(row.LocalAddress)),
                            ReadPort(row.LocalPort),
                            new IPAddress(BitConverter.GetBytes(row.RemoteAddress)),
                            ReadPort(row.RemotePort),
                            unchecked((int)row.OwningProcessId)));
                    }

                    rowPtr = IntPtr.Add(rowPtr, rowSize);
                }

                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(tablePtr);
            }
        }

        return false;
    }

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
    private const int TcpTableOwnerPidConnections = 4;
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
