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
            if (!process.WaitForExit(5_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            return readTask.GetAwaiter().GetResult().Trim();
        }
        catch
        {
            return null;
        }
    }

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
