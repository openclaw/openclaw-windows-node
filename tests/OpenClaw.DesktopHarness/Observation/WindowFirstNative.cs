#nullable disable
using System;
using System.Runtime.InteropServices;
using System.Text;

public sealed class WindowFirstNative : IWindowFirstNative
{
    public WindowNativeResult Query(string operation, IntPtr window)
    {
        SetLastError(0);
        var result = new WindowNativeResult();
        switch (operation)
        {
            case "IsWindow":
            case "IsWindow-after":
                result.ReturnValue = IsWindow(window) ? 1 : 0;
                break;
            case "IsWindowVisible":
                result.ReturnValue = IsWindowVisible(window) ? 1 : 0;
                break;
            case "GetClassNameW":
                var name = new StringBuilder(256);
                result.ReturnValue = GetClassNameW(window, name, name.Capacity);
                result.Error = Marshal.GetLastWin32Error();
                result.ClassName = name.ToString();
                return result;
            case "GetWindowTextLengthW":
                result.ReturnValue = GetWindowTextLengthW(window);
                break;
            case "GetWindowThreadProcessId":
            case "GetWindowThreadProcessId-after":
                uint pid;
                result.ReturnValue = GetWindowThreadProcessId(window, out pid);
                result.Error = Marshal.GetLastWin32Error();
                result.ProcessId = pid;
                return result;
            case "GetWindowRect":
                Rect rect;
                result.ReturnValue = GetWindowRect(window, out rect) ? 1 : 0;
                result.Error = Marshal.GetLastWin32Error();
                result.Rectangle = new[] { rect.Left, rect.Top, rect.Right, rect.Bottom };
                return result;
            case "GetWindowLongPtrW-style":
            case "GetWindowLongPtrW-exStyle":
                int index = operation == "GetWindowLongPtrW-style" ? -16 : -20;
                result.ReturnValue = IntPtr.Size == 8
                    ? GetWindowLongPtrW(window, index).ToInt64() : GetWindowLongW(window, index);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
        result.Error = Marshal.GetLastWin32Error();
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("kernel32.dll")] private static extern void SetLastError(uint error);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int count);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowTextLengthW(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLongW(IntPtr hwnd, int index);
}
