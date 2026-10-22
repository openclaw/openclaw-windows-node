#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public interface IObserverThreadNative
{
    IntPtr OpenLimited(uint threadId);
    uint Owner(IntPtr thread);
    bool Times(IntPtr thread, out long created, out long exited);
    int LastError { get; }
    bool Close(IntPtr thread);
}

public sealed record ThreadOwnershipAttempt(
    uint ThreadId, bool Opened, int? OpenError, uint OwnerPid, int? OwnerError,
    bool TimesSucceeded, int? TimesError, long? CreatedFileTime, long? ExitFileTime,
    int? CloseError)
{
    public Dictionary<string, object?> Fields() => new()
    {
        ["threadId"] = ThreadId, ["requestedAccess"] = 0x0800,
        ["opened"] = Opened, ["openError"] = OpenError,
        ["ownerPid"] = OwnerPid, ["ownerError"] = OwnerError,
        ["timesSucceeded"] = TimesSucceeded, ["timesError"] = TimesError,
        ["createdFileTime"] = CreatedFileTime, ["exitFileTime"] = ExitFileTime,
        ["closeError"] = CloseError,
        ["identityIsCurrentQueryNotEventTimeProof"] = true
    };
}

public static class ThreadOwnershipProbe
{
    public static ThreadOwnershipAttempt Query(IObserverThreadNative native, uint threadId)
    {
        IntPtr thread = native.OpenLimited(threadId);
        if (thread == IntPtr.Zero)
            return new(threadId, false, native.LastError, 0, null, false, null, null, null, null);

        uint owner = 0;
        int? ownerError = null, timesError = null, closeError = null;
        long created = 0, exited = 0;
        bool times = false;
        try
        {
            owner = native.Owner(thread);
            if (owner == 0) ownerError = native.LastError;
            times = native.Times(thread, out created, out exited);
            if (!times) timesError = native.LastError;
        }
        finally
        {
            if (!native.Close(thread)) closeError = native.LastError;
        }
        return new(threadId, true, null, owner, ownerError, times, timesError,
            times ? created : null, times ? exited : null, closeError);
    }
}

public sealed class ObserverThreadNative : IObserverThreadNative
{
    public const uint QueryLimitedInformation = 0x0800;
    public IntPtr OpenLimited(uint threadId) => OpenThread(QueryLimitedInformation, false, threadId);
    public uint Owner(IntPtr thread) => GetProcessIdOfThread(thread);
    public int LastError => Marshal.GetLastWin32Error();
    public bool Times(IntPtr thread, out long created, out long exited) =>
        GetThreadTimes(thread, out created, out exited, out _, out _);
    public bool Close(IntPtr thread) => CloseHandle(thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint access, bool inherit, uint thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessIdOfThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadTimes(IntPtr thread, out long created, out long exited,
        out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
