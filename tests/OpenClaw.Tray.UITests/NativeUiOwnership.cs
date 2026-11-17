// Verifies a UIA control against an owned native window, including windowless XAML fragments.
// Use IsOwnedDescendant(ownedWindow, control, appProcessId) before claiming control ownership.
// Inside a polling wait, use Classify: keep polling on Unproven, fail immediately on Foreign.
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace OpenClaw.Tray.UITests;

/// <summary>Combines native process ownership with the retained UIA window's raw ancestry.</summary>
internal static class NativeUiOwnership
{
    private const int MaxAncestorDepth = 64;

    /// <summary>
    /// Owned: the raw ancestry reaches the retained window inside the owning process.
    /// Unproven: no contrary evidence, but ownership cannot be shown yet. The peer is torn down or
    /// not yet attached (null parent, unavailable element) or reports no process id.
    /// Foreign: positive contrary evidence, such as a different process, a foreign native window, an
    /// ancestry that reaches another window or the desktop, or an invalid owner window.
    /// </summary>
    internal enum Evidence { Owned, Unproven, Foreign }

    /// <summary>Rejects foreign processes, other windows, and ancestry without a verified native owner.</summary>
    internal static bool IsOwnedDescendant(AutomationElement window, AutomationElement element, int processId) =>
        Classify(window, element, processId) == Evidence.Owned;

    /// <summary>
    /// Classifies ownership. Inside a polling wait, treat <see cref="Evidence.Unproven"/> as "not yet" and
    /// <see cref="Evidence.Foreign"/> as an immediate failure.
    /// </summary>
    internal static Evidence Classify(AutomationElement window, AutomationElement element, int processId) =>
        Classify(window, element, processId, ReadFacts, TreeWalker.RawViewWalker.GetParent, NativeProcessId);

    /// <summary>
    /// UIA facts the ancestry walk uses; separated so the decision rules are testable without a desktop.
    /// A null <see cref="RuntimeId"/> marks a partial read: the peer disappeared after the facts before it.
    /// </summary>
    internal readonly record struct NodeFacts(int ProcessId, string? FrameworkId, int NativeWindowHandle, bool IsWindow, int[]? RuntimeId);

    /// <summary>
    /// The ownership walk over any node source. Positive contrary evidence (Foreign) is checked before
    /// missing evidence (Unproven) at every node, so an unknown process id never hides a foreign window.
    /// </summary>
    internal static Evidence Classify<T>(T window, T element, int processId, Func<T, NodeFacts> read,
        Func<T, T?> parentOf, Func<int, uint> windowProcessId) where T : class
    {
        try
        {
            var owner = read(window);
            if (processId <= 0 || owner.ProcessId != processId ||
                owner.NativeWindowHandle != 0 && windowProcessId(owner.NativeWindowHandle) != (uint)processId)
                return Evidence.Foreign;
            if (owner.RuntimeId is null) return Evidence.Unproven;
            if (!owner.IsWindow || owner.NativeWindowHandle == 0) return Evidence.Foreign;
            if (owner.RuntimeId.Length == 0) return Evidence.Unproven;

            T? current = element;
            var unproven = false;
            for (var depth = 0; current is not null && depth < MaxAncestorDepth; depth++)
            {
                var info = read(current);
                if (info.NativeWindowHandle != 0)
                {
                    // Zero means the HWND is already destroyed (a closing popup): missing, not contrary, evidence.
                    var windowProcess = windowProcessId(info.NativeWindowHandle);
                    if (windowProcess == 0) unproven = true;
                    else if (windowProcess != (uint)processId) return Evidence.Foreign;
                }
                if (info.ProcessId != processId && info.ProcessId != 0)
                    return Evidence.Foreign;
                // XAML's windowless provider can return an explicit zero PID. Other frameworks give no proof,
                // but keep walking so contrary evidence higher up still wins.
                if (info.RuntimeId is null) return Evidence.Unproven;
                if (info.ProcessId == 0 && !string.Equals(info.FrameworkId, "XAML", StringComparison.Ordinal))
                    unproven = true;

                if (info.RuntimeId.SequenceEqual(owner.RuntimeId))
                {
                    // The owner changed identity during the walk: contrary evidence, not missing evidence.
                    var again = read(window);
                    if (again.ProcessId != processId ||
                        again.NativeWindowHandle != 0 && again.NativeWindowHandle != owner.NativeWindowHandle)
                        return Evidence.Foreign;
                    if (again.RuntimeId is null) return Evidence.Unproven;
                    if (again.NativeWindowHandle != owner.NativeWindowHandle ||
                        !again.RuntimeId.SequenceEqual(owner.RuntimeId) ||
                        windowProcessId(owner.NativeWindowHandle) != (uint)processId)
                        return Evidence.Foreign;
                    return unproven ? Evidence.Unproven : Evidence.Owned;
                }

                // Another window's ancestry ends at the desktop root, whose process is not the app's.
                current = parentOf(current);
            }
            // Detached (null parent) or unreasonably deep: no proof either way.
            return Evidence.Unproven;
        }
        catch (ElementNotAvailableException)
        {
            // A peer torn down mid-walk proves nothing about ownership.
            return Evidence.Unproven;
        }
    }

    // Each property is a separate live read. Read ownership facts first and keep them if the peer then
    // disappears, so evidence already read is never discarded.
    private static NodeFacts ReadFacts(AutomationElement element)
    {
        var current = element.Current;
        var processId = current.ProcessId;
        int handle;
        try { handle = current.NativeWindowHandle; }
        catch (ElementNotAvailableException) { return new(processId, null, 0, false, null); }
        try
        {
            return new(processId, current.FrameworkId, handle,
                current.ControlType == ControlType.Window, element.GetRuntimeId());
        }
        catch (ElementNotAvailableException) { return new(processId, null, handle, false, null); }
    }

    private static uint NativeProcessId(int hwnd)
    {
        var threadId = GetWindowThreadProcessId(new IntPtr(hwnd), out var processId);
        return threadId == 0 ? 0 : processId;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
