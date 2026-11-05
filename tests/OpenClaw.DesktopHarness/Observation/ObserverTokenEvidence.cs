#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

public static class ObserverTokenEvidence
{
    private const uint TokenQuery = 0x0008;
    private const int NoToken = 1008;

    public static Dictionary<string, object?> Capture()
    {
        using var process = Process.GetCurrentProcess();
        var fields = new Dictionary<string, object?>
        {
            ["processId"] = process.Id,
            ["processCreatedUtc"] = process.StartTime.ToUniversalTime().ToString("O"),
            ["sessionId"] = process.SessionId,
            ["observerThreadId"] = GetCurrentThreadId()
        };
        bool opened = OpenThreadToken(GetCurrentThread(), TokenQuery, true, out IntPtr threadToken);
        int? error = opened ? null : Marshal.GetLastWin32Error();
        fields["threadTokenOpened"] = opened;
        fields["threadTokenOpenError"] = error;
        fields["threadHasNoToken"] = !opened && error == NoToken;
        fields["openAsSelf"] = true;
        if (opened)
        {
            try { fields["threadToken"] = Read(threadToken); }
            finally { fields["threadTokenCloseError"] = Close(threadToken); }
        }
        bool processOpened = OpenProcessToken(GetCurrentProcess(), TokenQuery, out IntPtr processToken);
        fields["processTokenOpened"] = processOpened;
        fields["processTokenOpenError"] = processOpened ? null : Marshal.GetLastWin32Error();
        if (processOpened)
        {
            try { fields["processToken"] = Read(processToken); }
            finally { fields["processTokenCloseError"] = Close(processToken); }
        }
        fields["effectiveTokenSource"] = opened ? "thread" :
            error == NoToken && processOpened ? "process-no-thread-token" : "unresolved";
        return fields;
    }

    private static int? Close(IntPtr handle) => CloseHandle(handle) ? null : Marshal.GetLastWin32Error();

    private static Dictionary<string, object?> Read(IntPtr token)
    {
        var fields = new Dictionary<string, object?>();
        ReadField(token, 1, "sid", pointer =>
            new SecurityIdentifier(Marshal.ReadIntPtr(pointer)).Value, fields);
        ReadField(token, 25, "integritySid", pointer =>
            new SecurityIdentifier(Marshal.ReadIntPtr(pointer)).Value, fields);
        ReadField(token, 8, "tokenType", pointer => Marshal.ReadInt32(pointer), fields);
        ReadField(token, 9, "impersonationLevel", pointer => Marshal.ReadInt32(pointer), fields);
        ReadField(token, 18, "elevationType", pointer => Marshal.ReadInt32(pointer), fields);
        ReadField(token, 20, "elevated", pointer => Marshal.ReadInt32(pointer) != 0, fields);
        return fields;
    }

    private static void ReadField(IntPtr token, int kind, string name,
        Func<IntPtr, object> decode, Dictionary<string, object?> fields)
    {
        GetTokenInformation(token, kind, IntPtr.Zero, 0, out uint size);
        int sizeError = Marshal.GetLastWin32Error();
        if (size == 0)
        {
            fields[name + "Error"] = sizeError;
            return;
        }
        IntPtr buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            if (GetTokenInformation(token, kind, buffer, size, out _))
                fields[name] = decode(buffer);
            else
                fields[name + "Error"] = Marshal.GetLastWin32Error();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenThreadToken(IntPtr thread, uint access, bool openAsSelf, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int kind, IntPtr buffer,
        uint size, out uint needed);
}
