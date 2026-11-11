// Verifies a UIA control against an owned native window, including windowless XAML fragments.
// Use IsOwnedDescendant(ownedWindow, control, appProcessId) before claiming control ownership.
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace OpenClaw.Tray.UITests;

/// <summary>Combines native process ownership with the retained UIA window's raw ancestry.</summary>
internal static class NativeUiOwnership
{
    private const int MaxAncestorDepth = 64;

    /// <summary>Rejects foreign processes, other windows, and ancestry without a verified native owner.</summary>
    internal static bool IsOwnedDescendant(AutomationElement window, AutomationElement element, int processId)
    {
        var owner = window.Current;
        if (processId <= 0 || owner.ProcessId != processId || owner.ControlType != ControlType.Window ||
            owner.NativeWindowHandle == 0 || NativeProcessId(owner.NativeWindowHandle) != (uint)processId)
            return false;

        var ownerRuntimeId = window.GetRuntimeId();
        if (ownerRuntimeId.Length == 0) return false;

        AutomationElement? current = element;
        for (var depth = 0; current is not null && depth < MaxAncestorDepth; depth++)
        {
            var info = current.Current;
            // XAML's windowless provider can return an explicit zero PID. It is not ownership evidence.
            if (info.ProcessId != processId &&
                (info.ProcessId != 0 || !string.Equals(info.FrameworkId, "XAML", StringComparison.Ordinal)))
                return false;
            if (info.NativeWindowHandle != 0 && NativeProcessId(info.NativeWindowHandle) != (uint)processId)
                return false;

            if (current.GetRuntimeId().SequenceEqual(ownerRuntimeId))
                return window.Current.NativeWindowHandle == owner.NativeWindowHandle &&
                    window.Current.ProcessId == processId &&
                    window.GetRuntimeId().SequenceEqual(ownerRuntimeId) &&
                    NativeProcessId(owner.NativeWindowHandle) == (uint)processId;

            current = TreeWalker.RawViewWalker.GetParent(current);
        }
        return false;
    }

    private static uint NativeProcessId(int hwnd)
    {
        var threadId = GetWindowThreadProcessId(new IntPtr(hwnd), out var processId);
        return threadId == 0 ? 0 : processId;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
