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
    public Dictionary<string, long> CallTicks { get; init; } = new();
    public Dictionary<string, object?> Fields() => new()
    {
        ["threadId"] = ThreadId, ["requestedAccess"] = 0x0800,
        ["opened"] = Opened, ["openError"] = OpenError,
        ["ownerPid"] = OwnerPid, ["ownerError"] = OwnerError,
        ["timesSucceeded"] = TimesSucceeded, ["timesError"] = TimesError,
        ["createdFileTime"] = CreatedFileTime, ["exitFileTime"] = ExitFileTime,
        ["exitFileTimeRequiresIndependentTerminationEvidence"] = true,
        ["closeError"] = CloseError,
        ["callTickCount64"] = CallTicks,
        ["identityIsCurrentQueryNotEventTimeProof"] = true
    };
}

public static class ThreadOwnershipProbe
{
    public static ThreadOwnershipAttempt Query(IObserverThreadNative native, uint threadId,
        Func<long>? tickCount = null)
    {
        tickCount ??= () => Environment.TickCount64;
        var ticks = new Dictionary<string, long>();
        ticks["openBegin"] = tickCount();
        IntPtr thread = native.OpenLimited(threadId);
        int? openError = thread == IntPtr.Zero ? native.LastError : null;
        ticks["openEnd"] = tickCount();
        if (thread == IntPtr.Zero)
            return new(threadId, false, openError, 0, null, false, null, null, null, null) { CallTicks = ticks };

        uint owner = 0;
        int? ownerError = null, timesError = null, closeError = null;
        long created = 0, exited = 0;
        bool times = false;
        try
        {
            ticks["ownerBegin"] = tickCount();
            owner = native.Owner(thread);
            if (owner == 0) ownerError = native.LastError;
            ticks["ownerEnd"] = tickCount();
            ticks["timesBegin"] = tickCount();
            times = native.Times(thread, out created, out exited);
            if (!times) timesError = native.LastError;
            ticks["timesEnd"] = tickCount();
        }
        finally
        {
            ticks["closeBegin"] = tickCount();
            if (!native.Close(thread)) closeError = native.LastError;
            ticks["closeEnd"] = tickCount();
        }
        return new(threadId, true, null, owner, ownerError, times, timesError,
            times ? created : null, times ? exited : null, closeError) { CallTicks = ticks };
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
