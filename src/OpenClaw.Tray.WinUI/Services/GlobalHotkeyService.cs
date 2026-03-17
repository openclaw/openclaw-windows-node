using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace OpenClawTray.Services;

/// <summary>
/// Registers and handles global hotkeys using P/Invoke.
/// Default: Ctrl+Alt+Shift+C for Quick Send.
/// </summary>
public class GlobalHotkeyService : IDisposable
{
    private const int HOTKEY_ID = 9001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_SHIFT = 0x0004;
    private const uint VK_C = 0x43;
    private const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private const uint WM_QUIT = 0x0012;
    private const uint WM_USER = 0x0400;
    private const uint WM_APP_REGISTER = WM_USER + 1;
    private const uint WM_APP_UNREGISTER = WM_USER + 2;

    private const uint MOD_NOREPEAT = 0x4000;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    private IntPtr _hwnd;
    private bool _registered;
    private bool _disposed;
    private Thread? _messageThread;
    private WndProcDelegate? _wndProcDelegate; // prevent GC collection
    private volatile bool _running;

    private uint _messageThreadId;
    private readonly ManualResetEventSlim _windowReady = new(false);
    private readonly ManualResetEventSlim _opCompleted = new(false);

    public event EventHandler? HotkeyPressed;

    public GlobalHotkeyService()
    {
    }

    public bool Register()
    {
        if (_registered) return true;

        try
        {
            // Create message window on a dedicated thread with message loop
            EnsureMessageLoop();

            if (!_windowReady.Wait(TimeSpan.FromSeconds(2)))
            {
                Logger.Warn("Timed out waiting for hotkey message window");
                return false;
            }

            if (_hwnd == IntPtr.Zero)
            {
                Logger.Warn("Failed to create hotkey message window");
                return false;
            }

            _opCompleted.Reset();
            if (!PostMessage(_hwnd, WM_APP_REGISTER, IntPtr.Zero, IntPtr.Zero))
            {
                Logger.Warn("Failed to post WM_APP_REGISTER message for hotkey registration");
                _registered = false;
                return false;
            }

            if (!_opCompleted.Wait(TimeSpan.FromSeconds(2)))
            {
                Logger.Warn("Timed out waiting for hotkey registration operation to complete");
                _registered = false;
                return false;
            }
            return _registered;
        }
        catch (Exception ex)
        {
            Logger.Error($"Hotkey registration error: {ex.Message}");
            return false;
        }
    }

    private void EnsureMessageLoop()
    {
        if (_messageThread != null && _messageThread.IsAlive && _hwnd != IntPtr.Zero)
            return;

        // Reset state in case the previous thread died
        _messageThread = null;
        _hwnd = IntPtr.Zero;
        _windowReady.Reset();

        _running = true;
        _messageThread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "HotkeyMessageLoop"
        };
        _messageThread.Start();
    }

    private void MessageLoop()
    {
        try
        {
            _messageThreadId = GetCurrentThreadId();

            // Create window class
            _wndProcDelegate = WndProc;
            var wndClass = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                hInstance = GetModuleHandle(null),
                lpszClassName = "OpenClawHotkeyWindow"
            };

            RegisterClass(ref wndClass);

            // Create message-only window (HWND_MESSAGE parent)
            _hwnd = CreateWindowEx(0, "OpenClawHotkeyWindow", "", 0, 0, 0, 0, 0,
                new IntPtr(-3), // HWND_MESSAGE
                IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

            _windowReady.Set();

            // Message loop
            while (_running && GetMessage(out MSG msg, IntPtr.Zero, 0, 0))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Hotkey message loop error: {ex.Message}");
            _windowReady.Set();
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_REGISTER)
        {
            // Register from the message-loop thread that owns hWnd.
            _registered = RegisterHotKey(hWnd, HOTKEY_ID,
                MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT,
                VK_C);

            if (_registered)
            {
                Logger.Info("Global hotkey registered: Ctrl+Alt+Shift+C");
            }
            else
            {
                var err = Marshal.GetLastWin32Error();
                var errMsg = new Win32Exception(err).Message;
                Logger.Warn($"Failed to register global hotkey (Win32Error={err}: {errMsg})");
            }

            _opCompleted.Set();
            return IntPtr.Zero;
        }

        if (msg == WM_APP_UNREGISTER)
        {
            try
            {
                if (_registered)
                {
                    UnregisterHotKey(hWnd, HOTKEY_ID);
                    _registered = false;
                    Logger.Info("Global hotkey unregistered");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Hotkey unregistration error: {ex.Message}");
            }
            finally
            {
                _opCompleted.Set();
            }
            return IntPtr.Zero;
        }

        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            Logger.Info("Hotkey pressed: Ctrl+Alt+Shift+C");
            OnHotkeyPressed();
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Unregister()
    {
        if (!_registered) return;

        try
        {
            if (_hwnd == IntPtr.Zero) return;

            _opCompleted.Reset();
            if (!PostMessage(_hwnd, WM_APP_UNREGISTER, IntPtr.Zero, IntPtr.Zero))
            {
                Logger.Warn("Failed to post WM_APP_UNREGISTER message; message loop may have exited");
                _registered = false;
                return;
            }

            if (!_opCompleted.Wait(TimeSpan.FromSeconds(2)))
            {
                Logger.Warn("Timed out waiting for hotkey unregistration to complete");
                _registered = false;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Hotkey unregistration error: {ex.Message}");
        }
    }

    internal void OnHotkeyPressed()
    {
        HotkeyPressed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Unregister();

        _running = false;

        if (_messageThreadId != 0)
        {
            // WM_QUIT must be posted to the thread queue so the loop exits cleanly
            // and DestroyWindow runs on the owning thread.
            PostThreadMessage(_messageThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        _messageThread?.Join(2000);

        // Window should already be destroyed by the message loop exit,
        // but clean up if it wasn't.
        if (_hwnd != IntPtr.Zero)
        {
            try { DestroyWindow(_hwnd); } catch { }
            _hwnd = IntPtr.Zero;
        }

        _windowReady.Dispose();
        _opCompleted.Dispose();
    }
}
