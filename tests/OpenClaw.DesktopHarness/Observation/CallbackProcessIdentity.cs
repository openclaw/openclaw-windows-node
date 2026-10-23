#nullable disable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class CallbackProcessIdentity
{
    private const uint QueryLimitedInformation = 0x1000;

    public static Dictionary<string, object> Capture(uint pid)
    {
        var calls = new List<Dictionary<string, object>>();
        var fields = new Dictionary<string, object> {
            ["requestedPid"] = pid, ["calls"] = calls,
            ["ownershipTime"] = "callback-delivery-not-event-emission",
            ["hwndLifetimeProven"] = false, ["acceptanceExclusionAuthorized"] = false
        };
        if (pid == 0) { fields["status"] = "unknown-no-live-window-owner"; return fields; }
        uint session = 0, currentSession = 0;
        bool sessionOk = false, currentSessionOk = false;
        Measure(calls, "ProcessIdToSessionId-owner", () => {
            sessionOk = ProcessIdToSessionId(pid, out session); return sessionOk ? 1 : 0;
        });
        Measure(calls, "ProcessIdToSessionId-observer", () => {
            currentSessionOk = ProcessIdToSessionId(unchecked((uint)System.Diagnostics.Process.GetCurrentProcess().Id), out currentSession);
            return currentSessionOk ? 1 : 0;
        });
        fields["ownerSessionId"] = session; fields["observerSessionId"] = currentSession;
        IntPtr handle = IntPtr.Zero;
        Measure(calls, "OpenProcess-QUERY_LIMITED_INFORMATION", () => {
            handle = OpenProcess(QueryLimitedInformation, false, pid); return handle.ToInt64();
        });
        fields["requestedAccess"] = QueryLimitedInformation;
        if (handle == IntPtr.Zero) { fields["status"] = "unknown-process-access-denied-or-unavailable"; return fields; }
        try
        {
            uint retainedPid = 0;
            Measure(calls, "GetProcessId-retained-handle", () => { retainedPid = GetProcessId(handle); return retainedPid; });
            long creation = 0, exit = 0, kernel = 0, user = 0;
            bool timesOk = false;
            Measure(calls, "GetProcessTimes-retained-handle", () => {
                timesOk = GetProcessTimes(handle, out creation, out exit, out kernel, out user); return timesOk ? 1 : 0;
            });
            var image = new StringBuilder(32768);
            uint size = (uint)image.Capacity;
            bool imageOk = false;
            Measure(calls, "QueryFullProcessImageNameW-retained-handle", () => {
                imageOk = QueryFullProcessImageNameW(handle, 0, image, ref size); return imageOk ? 1 : 0;
            });
            var system = new StringBuilder(32768);
            uint systemLength = 0;
            Measure(calls, "GetSystemDirectoryW", () => {
                systemLength = GetSystemDirectoryW(system, (uint)system.Capacity); return systemLength;
            });
            bool complete = retainedPid == pid && timesOk && creation != 0 && imageOk && sessionOk && currentSessionOk;
            fields["retainedPid"] = retainedPid; fields["creationFileTime"] = creation;
            fields["imagePath"] = imageOk ? image.ToString() : null;
            fields["processIdentityCaptured"] = complete;
            fields["ownerMatchesSessionSystemCsrssAtCallback"] = complete && session == currentSession &&
                systemLength > 0 && systemLength < system.Capacity &&
                string.Equals(image.ToString(), System.IO.Path.Combine(system.ToString(), "csrss.exe"), StringComparison.OrdinalIgnoreCase);
            fields["status"] = complete ? "retained-process-at-callback-not-HWND-lifetime" : "unknown-incomplete-process-identity";
        }
        finally
        {
            Measure(calls, "CloseHandle-process", () => CloseHandle(handle) ? 1 : 0);
        }
        return fields;
    }

    private static void Measure(List<Dictionary<string, object>> calls, string operation, Func<long> query)
    {
        long begin = Environment.TickCount64;
        SetLastError(0);
        long value = query();
        int error = Marshal.GetLastWin32Error();
        long end = Environment.TickCount64;
        calls.Add(new Dictionary<string, object> {
            ["operation"] = operation, ["beganTickCount64"] = begin, ["endedTickCount64"] = end,
            ["returnValue"] = value, ["lastErrorRaw"] = error, ["lastErrorClearedBeforeCall"] = true
        });
    }

    [DllImport("kernel32.dll")] private static extern void SetLastError(uint error);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetProcessId(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr handle, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr handle, uint flags, StringBuilder image, ref uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetSystemDirectoryW(StringBuilder path, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
