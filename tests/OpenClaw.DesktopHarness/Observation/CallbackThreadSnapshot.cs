#nullable disable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class CallbackThreadSnapshot
{
    public static Dictionary<string, object> Capture(uint eventThread)
    {
        var calls = new List<Dictionary<string, object>>();
        var fields = new Dictionary<string, object> {
            ["requestedEventThread"] = eventThread, ["calls"] = calls,
            ["threadLifetimeProven"] = false, ["ownershipTime"] = "callback-delivery-not-event-emission"
        };
        uint pid = 0;
        long begin = Environment.TickCount64;
        IntPtr snapshot = CreateToolhelp32Snapshot(4, 0);
        int error = Marshal.GetLastWin32Error();
        Record(calls, "CreateToolhelp32Snapshot-THREAD", begin, snapshot.ToInt64(), error);
        if (snapshot == new IntPtr(-1)) { fields["status"] = "unknown-thread-snapshot-unavailable"; return fields; }
        try
        {
            var entry = new ThreadEntry { Size = (uint)Marshal.SizeOf(typeof(ThreadEntry)) };
            begin = Environment.TickCount64;
            bool found = Thread32First(snapshot, ref entry);
            error = Marshal.GetLastWin32Error();
            Record(calls, "Thread32First", begin, found ? 1 : 0, error);
            while (found)
            {
                if (entry.Size >= 16 && entry.ThreadId == eventThread) { pid = entry.OwnerPid; break; }
                entry.Size = (uint)Marshal.SizeOf(typeof(ThreadEntry));
                begin = Environment.TickCount64;
                found = Thread32Next(snapshot, ref entry);
                error = Marshal.GetLastWin32Error();
                Record(calls, "Thread32Next", begin, found ? 1 : 0, error);
            }
        }
        finally
        {
            begin = Environment.TickCount64;
            bool closed = CloseHandle(snapshot);
            error = Marshal.GetLastWin32Error();
            Record(calls, "CloseHandle-thread-snapshot", begin, closed ? 1 : 0, error);
        }
        fields["snapshotOwnerPid"] = pid;
        fields["status"] = pid == 0 ? "unknown-no-current-thread-match" : "candidate-only-no-thread-lifetime";
        fields["candidateProcessIdentity"] = CallbackProcessIdentity.Capture(pid);
        return fields;
    }

    private static void Record(List<Dictionary<string, object>> calls, string operation, long begin, long value, int error)
    {
        calls.Add(new Dictionary<string, object> { ["operation"] = operation, ["beganTickCount64"] = begin,
            ["endedTickCount64"] = Environment.TickCount64, ["returnValue"] = value, ["lastErrorRaw"] = error });
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadEntry { public uint Size, Usage, ThreadId, OwnerPid; public int BasePriority, DeltaPriority; public uint Flags; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Thread32First(IntPtr snapshot, ref ThreadEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Thread32Next(IntPtr snapshot, ref ThreadEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
