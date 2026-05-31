using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using OpenClaw.Shared;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

public sealed class ExecApprovalPromptService : IExecApprovalPromptHandler
{
    private readonly IOpenClawLogger _logger;

    public ExecApprovalPromptService(
        DispatcherQueue dispatcherQueue,
        Func<FrameworkElement?> rootProvider,
        IOpenClawLogger logger)
    {
        _logger = logger;
    }

    public Task<ExecApprovalPromptDecision> RequestAsync(
        ExecApprovalPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<ExecApprovalPromptDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(ExecApprovalPromptDecision.Deny("Approval prompt was cancelled"));

        var thread = new Thread(() =>
        {
            try
            {
                var decision = ShowNativePrompt(request);
                _logger.Info($"[ExecApproval] Prompt decision: {decision.Kind}");
                tcs.TrySetResult(decision);
            }
            catch (Exception ex)
            {
                _logger.Warn($"[ExecApproval] Prompt failed: {ex.Message}");
                tcs.TrySetResult(ExecApprovalPromptDecision.Deny("Approval prompt failed"));
            }
        })
        {
            IsBackground = true,
            Name = "OpenClaw Exec Approval Prompt"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return tcs.Task;
    }

    private static ExecApprovalPromptDecision ShowNativePrompt(ExecApprovalPromptRequest request)
    {
        var text =
            "A remote agent wants to run a local command on this Windows machine." +
            "\r\n\r\n" +
            request.Command +
            "\r\n\r\n" +
            $"Shell: {request.Shell ?? "auto"}" +
            "\r\n" +
            $"Reason: {request.Reason}";

        try
        {
            return new NativePromptWindow(text).Show();
        }
        catch
        {
            return ShowMessageBoxFallback(text);
        }
    }

    private static ExecApprovalPromptDecision ShowMessageBoxFallback(string text)
    {
        var fallbackText = text +
            Environment.NewLine + Environment.NewLine +
            "Yes = Allow once" +
            Environment.NewLine +
            "No or Cancel = Deny";

        var result = MessageBoxW(
            IntPtr.Zero,
            fallbackText,
            "Approve local command?",
            MessageBoxFlags.YesNoCancel |
            MessageBoxFlags.IconWarning |
            MessageBoxFlags.TopMost |
            MessageBoxFlags.SetForeground |
            MessageBoxFlags.DefaultButton3);

        return result switch
        {
            MessageBoxYes => ExecApprovalPromptDecision.AllowOnce(),
            _ => ExecApprovalPromptDecision.Deny()
        };
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, MessageBoxFlags type);

    private const int AllowOnceButtonId = 1001;
    private const int AlwaysAllowButtonId = 1002;
    private const int DenyButtonId = 1003;
    private const int MessageBoxYes = 6;

    [Flags]
    private enum MessageBoxFlags : uint
    {
        YesNoCancel = 0x00000003,
        IconWarning = 0x00000030,
        DefaultButton3 = 0x00000200,
        TopMost = 0x00040000,
        SetForeground = 0x00010000
    }

    private sealed class NativePromptWindow
    {
        private const string WindowClassName = "OpenClawExecApprovalPrompt";
        private const int WindowWidth = 640;
        private const int WindowHeight = 420;
        private const int ButtonWidth = 120;
        private const int ButtonHeight = 32;
        private const int ButtonTop = 340;
        private const int ButtonGap = 12;

        private static readonly object ClassLock = new();
        private static readonly object PromptLock = new();
        private static readonly Dictionary<IntPtr, NativePromptWindow> Prompts = new();
        private static readonly WndProc WindowProc = StaticWndProc;
        private static bool _classRegistered;

        private readonly string _text;
        private IntPtr _hwnd;
        private ExecApprovalPromptDecision _decision = ExecApprovalPromptDecision.Deny();
        private bool _completed;

        public NativePromptWindow(string text)
        {
            _text = text;
        }

        public ExecApprovalPromptDecision Show()
        {
            EnsureClassRegistered();

            var x = Math.Max(0, (GetSystemMetrics(SystemMetricScreenWidth) - WindowWidth) / 2);
            var y = Math.Max(0, (GetSystemMetrics(SystemMetricScreenHeight) - WindowHeight) / 2);

            _hwnd = CreateWindowExW(
                WindowExStyleTopMost | WindowExStyleDialogModalFrame,
                WindowClassName,
                "Approve local command?",
                WindowStyleCaption | WindowStyleSystemMenu | WindowStyleVisible,
                x,
                y,
                WindowWidth,
                WindowHeight,
                IntPtr.Zero,
                IntPtr.Zero,
                GetModuleHandleW(null),
                IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException("Could not create approval prompt window");
            }

            lock (PromptLock)
            {
                Prompts[_hwnd] = this;
            }

            CreateControls(_hwnd);
            ShowWindow(_hwnd, ShowWindowCommandShow);
            UpdateWindow(_hwnd);
            SetForegroundWindow(_hwnd);

            while (!_completed)
            {
                var messageResult = GetMessageW(out var message, IntPtr.Zero, 0, 0);
                if (messageResult == 0)
                {
                    PostQuitMessage(message.wParam.ToInt32());
                    break;
                }

                if (messageResult < 0)
                    throw new InvalidOperationException("Could not read approval prompt window message");

                TranslateMessage(ref message);
                DispatchMessageW(ref message);
            }

            return _decision;
        }

        private static void EnsureClassRegistered()
        {
            lock (ClassLock)
            {
                if (_classRegistered)
                    return;

                var wndClass = new WindowClass
                {
                    lpfnWndProc = WindowProc,
                    hInstance = GetModuleHandleW(null),
                    hbrBackground = new IntPtr(ColorWindow + 1),
                    lpszClassName = WindowClassName
                };

                if (RegisterClassW(ref wndClass) == 0)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != ErrorClassAlreadyExists)
                        throw new InvalidOperationException($"Could not register approval prompt window class: {error}");
                }

                _classRegistered = true;
            }
        }

        private void CreateControls(IntPtr hwnd)
        {
            var font = GetStockObject(DefaultGuiFont);
            CreateChild(hwnd, "STATIC", "Approve local command?", 20, 18, 590, 24, StaticStyleLeft, 0, font);
            CreateChild(
                hwnd,
                "EDIT",
                _text,
                20,
                52,
                590,
                265,
                WindowStyleVerticalScroll | EditStyleMultiline | EditStyleReadOnly | EditStyleAutoVerticalScroll | EditStyleWantReturn,
                0,
                font,
                WindowExStyleClientEdge);

            var totalButtonWidth = ButtonWidth * 3 + ButtonGap * 2;
            var firstLeft = WindowWidth - 20 - totalButtonWidth;
            CreateChild(hwnd, "BUTTON", "Allow once", firstLeft, ButtonTop, ButtonWidth, ButtonHeight, ButtonStylePushButton, AllowOnceButtonId, font);
            CreateChild(hwnd, "BUTTON", "Always allow", firstLeft + ButtonWidth + ButtonGap, ButtonTop, ButtonWidth, ButtonHeight, ButtonStylePushButton, AlwaysAllowButtonId, font);
            var denyButton = CreateChild(hwnd, "BUTTON", "Deny", firstLeft + (ButtonWidth + ButtonGap) * 2, ButtonTop, ButtonWidth, ButtonHeight, ButtonStyleDefaultPushButton, DenyButtonId, font);
            SetFocus(denyButton);
        }

        private static IntPtr CreateChild(
            IntPtr parent,
            string className,
            string text,
            int x,
            int y,
            int width,
            int height,
            uint controlStyle,
            int id,
            IntPtr font,
            uint extendedStyle = 0)
        {
            var hwnd = CreateWindowExW(
                extendedStyle,
                className,
                text,
                WindowStyleChild | WindowStyleVisible | controlStyle,
                x,
                y,
                width,
                height,
                parent,
                new IntPtr(id),
                GetModuleHandleW(null),
                IntPtr.Zero);

            if (hwnd != IntPtr.Zero && font != IntPtr.Zero)
                SendMessageW(hwnd, WindowMessageSetFont, font, new IntPtr(1));

            return hwnd;
        }

        private static IntPtr StaticWndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            NativePromptWindow? prompt;
            lock (PromptLock)
            {
                Prompts.TryGetValue(hwnd, out prompt);
            }

            if (prompt == null)
                return DefWindowProcW(hwnd, message, wParam, lParam);

            switch (message)
            {
                case WindowMessageCommand:
                    var buttonId = ButtonIdFromWParam(wParam);
                    if (buttonId is AllowOnceButtonId or AlwaysAllowButtonId or DenyButtonId)
                    {
                        prompt.Complete(buttonId);
                        return IntPtr.Zero;
                    }

                    return DefWindowProcW(hwnd, message, wParam, lParam);
                case WindowMessageClose:
                    prompt.Complete(DenyButtonId);
                    return IntPtr.Zero;
                case WindowMessageDestroy:
                    lock (PromptLock)
                    {
                        Prompts.Remove(hwnd);
                    }
                    prompt._completed = true;
                    return IntPtr.Zero;
                default:
                    return DefWindowProcW(hwnd, message, wParam, lParam);
            }
        }

        private void Complete(int buttonId)
        {
            _decision = buttonId switch
            {
                AllowOnceButtonId => ExecApprovalPromptDecision.AllowOnce(),
                AlwaysAllowButtonId => ExecApprovalPromptDecision.AlwaysAllow(),
                _ => ExecApprovalPromptDecision.Deny()
            };
            _completed = true;
            if (_hwnd != IntPtr.Zero)
                DestroyWindow(_hwnd);
        }

        private static int ButtonIdFromWParam(IntPtr wParam) => (int)((long)wParam & 0xffff);

        private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClass
        {
            public uint style;
            public WndProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpszMenuName;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Message
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassW(ref WindowClass lpWndClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(
            uint dwExStyle,
            string lpClassName,
            string lpWindowName,
            uint dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool UpdateWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int GetMessageW(out Message lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref Message lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessageW(ref Message lpMsg);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string? lpModuleName);

        [DllImport("gdi32.dll")]
        private static extern IntPtr GetStockObject(int fnObject);

        private const int ErrorClassAlreadyExists = 1410;
        private const int SystemMetricScreenWidth = 0;
        private const int SystemMetricScreenHeight = 1;
        private const int DefaultGuiFont = 17;
        private const int ShowWindowCommandShow = 5;
        private const int ColorWindow = 5;

        private const uint WindowMessageCommand = 0x0111;
        private const uint WindowMessageClose = 0x0010;
        private const uint WindowMessageDestroy = 0x0002;
        private const uint WindowMessageSetFont = 0x0030;

        private const uint WindowExStyleDialogModalFrame = 0x00000001;
        private const uint WindowExStyleTopMost = 0x00000008;
        private const uint WindowExStyleClientEdge = 0x00000200;

        private const uint WindowStyleCaption = 0x00C00000;
        private const uint WindowStyleSystemMenu = 0x00080000;
        private const uint WindowStyleVisible = 0x10000000;
        private const uint WindowStyleChild = 0x40000000;
        private const uint WindowStyleVerticalScroll = 0x00200000;

        private const uint StaticStyleLeft = 0x00000000;
        private const uint EditStyleMultiline = 0x00000004;
        private const uint EditStyleAutoVerticalScroll = 0x00000040;
        private const uint EditStyleReadOnly = 0x00000800;
        private const uint EditStyleWantReturn = 0x00001000;
        private const uint ButtonStylePushButton = 0x00000000;
        private const uint ButtonStyleDefaultPushButton = 0x00000001;
    }
}
