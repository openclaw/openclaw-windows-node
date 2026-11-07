using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenClaw.Shared;

namespace OpenClaw.Connection;

/// <summary>
/// Fixture-only check that a loopback browser-control port is owned by the
/// process that launched this app. Ordinary runs fail closed.
/// </summary>
public static class FixtureLoopbackListenerOwner
{
    public static bool IsOwnedByCurrentProcessParent(int port)
    {
        if (!OperatingSystem.IsWindows() || port is < 1 or > 65535)
            return false;
        try
        {
            if (!GatewayFixtureIsolation.IsEnabled)
                return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        try
        {
            using var current = Process.GetCurrentProcess();
            var parentId = GetParentProcessId(current.SafeHandle);
            if (parentId <= 0)
                return false;
            using var parent = Process.GetProcessById(parentId);
            // Keep this handle for the whole check. A later PID lookup can observe
            // a different process after Windows reuses the number.
            var parentHandle = parent.SafeHandle;
            if (parent.HasExited)
                return false;
            var parentStart = parent.StartTime.ToUniversalTime();
            var childStart = current.StartTime.ToUniversalTime();
            var snapshot = WindowsTcpListenerSnapshot.Capture();
            if (!snapshot.Ipv4Complete)
                return false;
            var onPort = snapshot.Listeners.Where(listener => listener.Port == port).ToArray();
            var owned = onPort.Length > 0 && onPort.All(listener =>
                IsLiveParentListener(
                    parent.HasExited,
                    parentStart,
                    childStart,
                    parent.Id,
                    listener));
            parent.Refresh();
            return owned
                && !parent.HasExited
                && parent.StartTime.ToUniversalTime() == parentStart
                && parentHandle == parent.SafeHandle;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or OverflowException)
        {
            return false;
        }
    }

    internal static bool IsLiveParentListener(
        bool parentHasExited,
        DateTime parentStartUtc,
        DateTime childStartUtc,
        int parentId,
        WindowsTcpListenerInfo listener)
    {
        if (parentHasExited || parentId <= 0 || listener.ProcessId != parentId)
            return false;
        if (parentStartUtc > childStartUtc)
            return false;
        if (!IPAddress.IsLoopback(listener.Address))
            return false;
        return listener.ProcessStartTimeUtc == parentStartUtc;
    }

    private static int GetParentProcessId(SafeProcessHandle process)
    {
        var status = NtQueryInformationProcess(
            process, 0, out var basic, (uint)Marshal.SizeOf<ProcessBasicInformation>(), out var returned);
        if (status != 0 || returned != (uint)Marshal.SizeOf<ProcessBasicInformation>())
            throw new InvalidOperationException("The process parent could not be verified.");
        return checked((int)basic.ParentProcessId);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public int ExitStatus;
        public IntPtr PebAddress;
        public nuint AffinityMask;
        public int BasePriority;
        public nuint ProcessId;
        public nuint ParentProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        SafeProcessHandle process,
        int informationClass,
        out ProcessBasicInformation information,
        uint length,
        out uint returned);
}
