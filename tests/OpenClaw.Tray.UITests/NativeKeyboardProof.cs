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
    private static bool IsEnabled =>
        Environment.GetEnvironmentVariable("OPENCLAW_RUN_KEYBOARD_PROOF") == "1" ||
        Environment.GetEnvironmentVariable("OPENCLAW_RUN_GATEWAY_FIXTURE_UI") == "1";

    public static async Task ActivateAsync(nint window)
    {
        if (!IsEnabled || window == 0)
            throw new InvalidOperationException("An explicitly enabled proof window is required.");
        for (var attempt = 0; attempt < 20; attempt++)
        {
            SetForegroundWindow(window);
            if (GetForegroundWindow() == window)
                return;
            await Task.Delay(100);
        }
        var foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out var process);
        throw new InvalidOperationException(
            $"Windows did not allow proof window 0x{window:X} (PID {Environment.ProcessId}) to become foreground. " +
            $"Foreground is 0x{foreground:X}, PID {process}.");
    }

    public static void Press(nint window, VirtualKey key, bool shift = false, bool control = false, bool alt = false)
    {
        RequireInputWindow(window);
        List<Input> sequence = [];
        if (control) sequence.Add(Keyboard(VirtualKey.Control));
        if (shift) sequence.Add(Keyboard(VirtualKey.Shift));
        if (alt) sequence.Add(Keyboard(VirtualKey.Menu));
        sequence.Add(Keyboard(key));
        sequence.Add(Keyboard(key, up: true));
        if (alt) sequence.Add(Keyboard(VirtualKey.Menu, up: true));
        if (shift) sequence.Add(Keyboard(VirtualKey.Shift, up: true));
        if (control) sequence.Add(Keyboard(VirtualKey.Control, up: true));
        Inject(sequence.ToArray());
    }

    public static void TypeText(nint window, string text)
    {
        RequireInputWindow(window);
        Inject(text.SelectMany(character => new[]
        {
            new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { ScanCode = character, Flags = 4 } } },
            new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { ScanCode = character, Flags = 6 } } }
        }).ToArray());
    }

    private static void RequireInputWindow(nint window)
    {
        if (!IsEnabled)
            throw new InvalidOperationException("Native keyboard proof was not explicitly enabled.");
        var foreground = GetForegroundWindow();
        if (window == 0 || foreground != window)
        {
            GetWindowThreadProcessId(foreground, out var process);
            throw new InvalidOperationException(
                $"The proof window 0x{window:X} must be foreground before injecting keys. Foreground is 0x{foreground:X}, PID {process}.");
        }
        foreach (var modifier in new[] { VirtualKey.Shift, VirtualKey.Control, VirtualKey.Menu,
                     VirtualKey.LeftWindows, VirtualKey.RightWindows })
        {
            if ((GetAsyncKeyState((int)modifier) & 0x8000) != 0)
                throw new InvalidOperationException($"Release {modifier} before running keyboard proof.");
        }
        if ((GetKeyState((int)VirtualKey.CapitalLock) & 1) != 0)
            throw new InvalidOperationException("Turn off Caps Lock before running keyboard proof.");

    }

    private static void Inject(Input[] keys)
    {
        if (keys.Length == 0) return;
        var inserted = SendInput((uint)keys.Length, keys, Marshal.SizeOf<Input>());
        if (inserted != keys.Length)
        {
            var error = Marshal.GetLastWin32Error();
            // A partial injection must not leave the test's modifiers held down.
            var release = keys.Where(input => (input.Data.Keyboard.Flags & 2) != 0).ToArray();
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
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);
}
