#nullable disable
using System;
using System.Collections.Generic;

public sealed class WindowFirstFake : IWindowFirstNative
{
    public readonly List<string> Calls = new List<string>();
    public Action<string> BeforeQuery;
    public bool Invalid;
    public uint Pid = 77;
    public uint Tid = 88;
    public string Name = "ConsoleWindowClass";

    public WindowNativeResult Query(string operation, IntPtr window)
    {
        Calls.Add(operation);
        if (BeforeQuery != null) BeforeQuery(operation);
        if (Invalid) return new WindowNativeResult { Error = 1400 };
        switch (operation)
        {
            case "GetClassNameW": return new WindowNativeResult { ReturnValue = Name.Length, ClassName = Name };
            case "GetWindowTextLengthW": return new WindowNativeResult { ReturnValue = 13 };
            case "GetWindowThreadProcessId":
            case "GetWindowThreadProcessId-after": return new WindowNativeResult { ReturnValue = Tid, ProcessId = Pid };
            case "GetWindowRect": return new WindowNativeResult { ReturnValue = 1, Rectangle = new[] { -40, 10, 600, 480 } };
            case "GetWindowLongPtrW-style": return new WindowNativeResult { ReturnValue = 0x80000000L };
            case "GetWindowLongPtrW-exStyle": return new WindowNativeResult { ReturnValue = 0 };
            default: return new WindowNativeResult { ReturnValue = 1 };
        }
    }
}
