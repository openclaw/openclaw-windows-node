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
    public static WindowsTcpListenerSnapshotResult Capture()
    {
        if (!OperatingSystem.IsWindows())
            return new([], Ipv4Complete: false, Ipv6Complete: false);

        var result = new List<WindowsTcpListenerInfo>();
        var ipv4Complete = CaptureIpv4(result);
        var ipv6Complete = CaptureIpv6(result);
        return new(result, ipv4Complete, ipv6Complete);
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
