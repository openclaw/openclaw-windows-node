using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.System;

namespace OpenClaw.Tray.UITests;

public sealed class NativeKeyboardTheoryAttribute : TheoryAttribute
{
    public NativeKeyboardTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_RUN_KEYBOARD_PROOF") != "1")
            Skip = "Requires OPENCLAW_RUN_KEYBOARD_PROOF=1 on an isolated interactive Windows desktop.";
    }
}

internal static class NativeKeyboardProof
{
    public static (System.Drawing.Bitmap Bitmap, System.Drawing.Rectangle Bounds) Capture(nint window)
    {
        if (window == 0 || GetForegroundWindow() != window)
            throw new InvalidOperationException("The owned proof window must be foreground for desktop pixel capture.");
        GetWindowThreadProcessId(window, out var processId);
        if (processId != Environment.ProcessId)
            throw new InvalidOperationException("Pixel capture requires the owned proof window.");
        if (!GetClientRect(window, out var rect))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        Marshal.ThrowExceptionForHR(DwmFlush());
        if (GetForegroundWindow() != window)
            throw new InvalidOperationException("The owned proof window lost foreground before pixel capture.");
        var origin = ClientPoint(window, rect.Left, rect.Top);
        var bounds = new System.Drawing.Rectangle(origin.X, origin.Y, rect.Right - rect.Left, rect.Bottom - rect.Top);
        var bitmap = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
        return (bitmap, bounds);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out NativeRect rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
    private static bool _leftButtonHeld;
    private static bool _shiftHeld;
    public static async Task ActivateAsync(nint window)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_RUN_KEYBOARD_PROOF") != "1" || window == 0)
            throw new InvalidOperationException("An explicitly enabled proof window is required.");
        for (var attempt = 0; attempt < 20; attempt++)
        {
            SetForegroundWindow(window);
            if (GetForegroundWindow() == window)
                return;
            await Task.Delay(100);
        }
        throw new InvalidOperationException("Windows did not allow the keyboard proof window to become foreground.");
    }

    public static void Press(nint window, VirtualKey key, bool shift = false, bool control = false)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_RUN_KEYBOARD_PROOF") != "1")
            throw new InvalidOperationException("Native keyboard proof was not explicitly enabled.");
        if (window == 0 || GetForegroundWindow() != window)
            throw new InvalidOperationException("The proof window must be foreground before injecting keys.");
        GetWindowThreadProcessId(window, out var processId);
        if (processId != Environment.ProcessId)
            throw new InvalidOperationException("The proof window must belong to this test process.");
        foreach (var modifier in new[] { VirtualKey.Shift, VirtualKey.Control, VirtualKey.Menu,
                     VirtualKey.LeftWindows, VirtualKey.RightWindows })
        {
            if ((GetAsyncKeyState((int)modifier) & 0x8000) != 0)
                throw new InvalidOperationException($"Release {modifier} before running keyboard proof.");
        }
        if ((GetKeyState((int)VirtualKey.CapitalLock) & 1) != 0)
            throw new InvalidOperationException("Turn off Caps Lock before running keyboard proof.");

        Input[] keys = control
            ? [Keyboard(VirtualKey.Control), Keyboard(key), Keyboard(key, up: true), Keyboard(VirtualKey.Control, up: true)]
            : shift
            ? [Keyboard(VirtualKey.Shift), Keyboard(key), Keyboard(key, up: true), Keyboard(VirtualKey.Shift, up: true)]
            : [Keyboard(key), Keyboard(key, up: true)];
        var inserted = SendInput((uint)keys.Length, keys, Marshal.SizeOf<Input>());
        if (inserted != keys.Length)
        {
            var error = Marshal.GetLastWin32Error();
            // A partial injection must not leave the test's modifiers held down.
            Input[] release = shift
                ? [Keyboard(key, up: true), Keyboard(VirtualKey.Shift, up: true)]
                : control ? [Keyboard(key, up: true), Keyboard(VirtualKey.Control, up: true)]
                : [Keyboard(key, up: true)];
            var released = SendInput((uint)release.Length, release, Marshal.SizeOf<Input>());
            throw new Win32Exception(error,
                $"Keyboard proof injected {inserted}/{keys.Length} events; cleanup injected {released}/{release.Length} releases.");
        }
    }

    public static void Mouse(nint window, int x, int y, uint buttonFlags = 0, bool shift = false)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_RUN_KEYBOARD_PROOF") != "1" ||
            window == 0 || GetForegroundWindow() != window)
            throw new InvalidOperationException("Only the foreground owned proof window may receive input.");
        GetWindowThreadProcessId(window, out var processId);
        if (processId != Environment.ProcessId)
            throw new InvalidOperationException("The proof window must belong to this test process.");
        if (GetAncestor(WindowFromPoint(new NativePoint { X = x, Y = y }), 2) != window)
            throw new InvalidOperationException("The pointer target must be inside the owned proof window.");
        foreach (var key in new[] { VirtualKey.Shift, VirtualKey.Control, VirtualKey.Menu,
                         VirtualKey.LeftWindows, VirtualKey.RightWindows })
            if ((GetAsyncKeyState((int)key) & 0x8000) != 0)
                throw new InvalidOperationException($"Release {key} before running pointer proof.");
        var left = GetSystemMetrics(76);
        var top = GetSystemMetrics(77);
        var width = GetSystemMetrics(78);
        var height = GetSystemMetrics(79);
        var input = new Input
        {
            Type = 0,
            Data = new InputUnion
            {
                Mouse = new MouseInput
                {
                    X = (int)Math.Round((x - left) * 65535d / (width - 1)),
                    Y = (int)Math.Round((y - top) * 65535d / (height - 1)),
                    Flags = 0x8000 | 0x4000 | 0x0001 | buttonFlags,
                }
            },
        };
        Input[] inputs = shift ? [Keyboard(VirtualKey.Shift), input] : [input];
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
        {
            Input[] release = [Keyboard(VirtualKey.Shift, up: true),
                    new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = 4 } } }];
            SendInput((uint)release.Length, release, Marshal.SizeOf<Input>());
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Native pointer injection failed.");
        }
        _shiftHeld = shift;
        if ((buttonFlags & 2) != 0)
            _leftButtonHeld = true;
        if ((buttonFlags & 4) != 0)
            _leftButtonHeld = false;
    }

    public static void ReleaseShift(nint window)
    {
        if (GetForegroundWindow() != window)
            throw new InvalidOperationException("The owned proof window lost foreground.");
        Input[] release = [Keyboard(VirtualKey.Shift, up: true)];
        if (SendInput(1, release, Marshal.SizeOf<Input>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        _shiftHeld = false;
    }

    public static void ReleaseOwnedInput(nint window)
    {
        if (!_leftButtonHeld && !_shiftHeld)
            return;
        if (GetForegroundWindow() != window)
            throw new InvalidOperationException("Owned input cleanup requires the proof window in foreground.");
        if (_shiftHeld)
            ReleaseShift(window);
        if (_leftButtonHeld)
        {
            Input[] release = [new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = 4 } } }];
            if (SendInput(1, release, Marshal.SizeOf<Input>()) != 1)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _leftButtonHeld = false;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out int processId);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    public static (int X, int Y) ClientPoint(nint window, int x, int y)
    {
        var point = new NativePoint { X = x, Y = y };
        if (!ClientToScreen(window, ref point))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return (point.X, point.Y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint window, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);
    private static Input Keyboard(VirtualKey key, bool up = false) => new()
    {
        Type = 1,
        Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = (ushort)key, Flags = up ? 2u : 0u } },
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    // INPUT's native union is sized by MOUSEINPUT, even for keyboard events.
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);
}
