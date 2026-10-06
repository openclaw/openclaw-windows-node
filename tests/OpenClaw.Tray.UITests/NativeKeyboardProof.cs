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

    public static void Press(nint window, VirtualKey key, bool shift = false)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_RUN_KEYBOARD_PROOF") != "1")
            throw new InvalidOperationException("Native keyboard proof was not explicitly enabled.");
        if (window == 0 || GetForegroundWindow() != window)
            throw new InvalidOperationException("The proof window must be foreground before injecting keys.");
        foreach (var modifier in new[] { VirtualKey.Shift, VirtualKey.Control, VirtualKey.Menu,
                     VirtualKey.LeftWindows, VirtualKey.RightWindows })
        {
            if ((GetAsyncKeyState((int)modifier) & 0x8000) != 0)
                throw new InvalidOperationException($"Release {modifier} before running keyboard proof.");
        }
        if ((GetKeyState((int)VirtualKey.CapitalLock) & 1) != 0)
            throw new InvalidOperationException("Turn off Caps Lock before running keyboard proof.");

        Input[] keys = shift
            ? [Keyboard(VirtualKey.Shift), Keyboard(key), Keyboard(key, up: true), Keyboard(VirtualKey.Shift, up: true)]
            : [Keyboard(key), Keyboard(key, up: true)];
        var inserted = SendInput((uint)keys.Length, keys, Marshal.SizeOf<Input>());
        if (inserted != keys.Length)
        {
            var error = Marshal.GetLastWin32Error();
            // A partial injection must not leave the test's modifiers held down.
            Input[] release = shift
                ? [Keyboard(key, up: true), Keyboard(VirtualKey.Shift, up: true)]
                : [Keyboard(key, up: true)];
            var released = SendInput((uint)release.Length, release, Marshal.SizeOf<Input>());
            throw new Win32Exception(error,
                $"Keyboard proof injected {inserted}/{keys.Length} events; cleanup injected {released}/{release.Length} releases.");
        }
    }

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
