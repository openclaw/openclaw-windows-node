#nullable disable
using System;
using System.Collections.Generic;

public sealed class WindowNativeResult
{
    public long ReturnValue;
    public int Error;
    public string ClassName;
    public uint ProcessId;
    public int[] Rectangle;
}

public interface IWindowFirstNative
{
    WindowNativeResult Query(string operation, IntPtr window);
}

public sealed class WindowFirstSnapshot
{
    public readonly Dictionary<string, object> Fields = new Dictionary<string, object>();
    public int ClassLength;
    public string ClassName;
    public int ClassError;
    public uint OwnerPid;
    public uint OwnerThread;
    public int OwnerError;
    public bool ValidBefore;
    public bool ValidAfter;
    public uint OwnerPidAfter;
    public uint OwnerThreadAfter;
    public bool Visible;
    public long ClassBegin, ClassEnd, OwnerBegin, OwnerEnd;
}

public static class WindowFirstCapture
{
    // No process, thread, token, logging or tree queries may precede this boundary.
    public static WindowFirstSnapshot Capture(IWindowFirstNative native, IntPtr window, Func<long> clock)
    {
        var result = new WindowFirstSnapshot();
        var calls = new List<Dictionary<string, object>>();
        result.Fields["window"] = QuietCaptureLedger.WindowKey(window);
        result.Fields["pointerBits"] = IntPtr.Size * 8;
        result.Fields["calls"] = calls;
        foreach (string operation in new[] { "IsWindow", "GetClassNameW", "GetWindowTextLengthW",
            "GetWindowThreadProcessId", "GetWindowRect", "GetWindowLongPtrW-style",
            "GetWindowLongPtrW-exStyle", "IsWindowVisible", "IsWindow-after",
            "GetWindowThreadProcessId-after" })
        {
            long begin = clock();
            WindowNativeResult value = native.Query(operation, window);
            long end = clock();
            var call = new Dictionary<string, object> {
                ["operation"] = operation, ["beganTickCount64"] = begin, ["endedTickCount64"] = end,
                ["returnValue"] = value.ReturnValue, ["lastErrorRaw"] = value.Error,
                ["lastErrorClearedBeforeCall"] = true
            };
            if (value.Rectangle != null) call["rectangle"] = (int[])value.Rectangle.Clone();
            if (operation == "GetClassNameW")
            {
                result.ClassLength = checked((int)value.ReturnValue);
                result.ClassName = value.ClassName;
                result.ClassError = value.Error;
                result.ClassBegin = begin; result.ClassEnd = end;
                call["className"] = value.ClassName;
            }
            if (operation.StartsWith("GetWindowThreadProcessId", StringComparison.Ordinal))
            {
                call["pid"] = value.ProcessId;
                if (operation == "GetWindowThreadProcessId")
                {
                    result.OwnerPid = value.ProcessId;
                    result.OwnerThread = unchecked((uint)value.ReturnValue);
                    result.OwnerError = value.Error;
                    result.OwnerBegin = begin; result.OwnerEnd = end;
                }
                else
                {
                    result.OwnerPidAfter = value.ProcessId;
                    result.OwnerThreadAfter = unchecked((uint)value.ReturnValue);
                    result.Fields["ownerUnchangedAcrossFastCalls"] =
                        result.OwnerPid != 0 && result.OwnerThread != 0 &&
                        result.OwnerPid == value.ProcessId && result.OwnerThread == unchecked((uint)value.ReturnValue);
                }
            }
            if (operation == "IsWindow") result.ValidBefore = value.ReturnValue != 0;
            if (operation == "IsWindow-after") result.ValidAfter = value.ReturnValue != 0;
            if (operation == "IsWindowVisible") result.Visible = value.ReturnValue != 0;
            calls.Add(call);
        }
        // Matching PID/TID brackets are not a retained HWND lifetime identity.
        result.Fields["hwndLifetimeProven"] = false;
        return result;
    }
}
