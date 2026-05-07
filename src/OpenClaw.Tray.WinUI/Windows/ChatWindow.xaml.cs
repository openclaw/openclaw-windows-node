using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.Web.WebView2.Core;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WinUIEx;

namespace OpenClawTray.Windows;

public sealed partial class ChatWindow : WindowEx
{
    private readonly string _gatewayUrl;
    private readonly string _token;
    private string _chatUrl = "";
    private bool _webViewInitialized;
    public bool IsClosed { get; private set; }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attr, ref int val, int size);

    private const int GWL_EXSTYLE = -20;
    private const int GWL_STYLE = -16;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT2 { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT2 rcMonitor;
        public RECT2 rcWork;
        public int dwFlags;
    }

    public ChatWindow(string gatewayUrl, string token)
    {
        _gatewayUrl = gatewayUrl;
        _token = token;
        InitializeComponent();

        // No system title bar for popup panel — our custom header replaces it
        this.SetWindowSize(480, 640);
        this.SetIcon(IconHelper.GetStatusIconPath(ConnectionStatus.Connected));

        // Set as tool window (hidden from taskbar) + remove system caption
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
        var wStyle = GetWindowLong(hwnd, GWL_STYLE);
        SetWindowLong(hwnd, GWL_STYLE, wStyle & ~WS_CAPTION & ~WS_THICKFRAME);

        // Rounded corners (Windows 11)
        var cornerPref = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

        // Auto-hide when clicking outside the panel
        Activated += OnWindowActivated;

        // Hide instead of close — preserves WebView2 session for instant reopen
        Closed += OnWindowClosing;
        _ = InitializeWebViewAsync();
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            this.Hide();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        this.Hide();
    }

    /// <summary>Position near the system tray and show with animation.</summary>
    public void ShowNearTray()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // Get cursor position (near tray icon click)
        GetCursorPos(out POINT pt);

        // Get work area of the monitor containing the cursor
        var hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(hMon, ref mi);
        var work = mi.rcWork;

        // Get DPI scale for this monitor
        uint dpi = GetDpiForWindow(hwnd);
        double scale = dpi / 96.0;

        // Panel size in physical pixels
        int panelWPx = (int)(480 * scale);
        int panelHPx = (int)(640 * scale);

        // Position: bottom-right of work area, 8px margin from edges
        // This places the panel just above the taskbar, near the tray
        int margin = 8;
        int x = work.Right - panelWPx - margin;
        int y = work.Bottom - panelHPx - margin;

        this.Move(x, y);
        this.SetWindowSize(480, 640); // DIPs

        this.Show();
        SetForegroundWindow(hwnd);
        RequestChatInputFocus();
    }

    /// <summary>Show near tray. No animation — WebView2 doesn't participate in composition animations.</summary>
    public void ShowNearTrayAnimated()
    {
        ShowNearTray();
    }

    private void OnWindowClosing(object sender, WindowEventArgs args)
    {
        // Intercept close → hide instead (keeps WebView2 warm)
        args.Handled = true;
        this.Hide();
    }

    /// <summary>Actually close and dispose (called on app shutdown).</summary>
    public void ForceClose()
    {
        Closed -= OnWindowClosing;
        IsClosed = true;
        Close();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            PlaceholderPanel.Visibility = Visibility.Collapsed;
            LoadingRing.IsActive = true;
            LoadingRing.Visibility = Visibility.Visible;

            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenClawTray", "WebView2");
            Directory.CreateDirectory(userDataFolder);
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);

            await WebView.EnsureCoreWebView2Async();
            _webViewInitialized = true;

            WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;

            WebView.CoreWebView2.NavigationCompleted += (s, e) =>
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
                if (!e.IsSuccess)
                {
                    WebView.Visibility = Visibility.Collapsed;
                    ErrorPanel.Visibility = Visibility.Visible;
                    ErrorText.Text = e.WebErrorStatus switch
                    {
                        CoreWebView2WebErrorStatus.CannotConnect or
                        CoreWebView2WebErrorStatus.ConnectionReset or
                        CoreWebView2WebErrorStatus.ServerUnreachable or
                        CoreWebView2WebErrorStatus.Timeout =>
                            "The gateway is not reachable. Check that it is running and try again.",
                        _ => $"Unable to load chat. Please try again. ({e.WebErrorStatus})"
                    };
                }
                else
                {
                    // Successful navigation — ensure we're in the right visual state (e.g. after retry)
                    ErrorPanel.Visibility = Visibility.Collapsed;
                    WebView.Visibility = Visibility.Visible;
                    RequestChatInputFocus();
                }
            };

            // Build chat URL
            if (GatewayUrlHelper.TryNormalizeWebSocketUrl(_gatewayUrl, out var normalizedUrl) &&
                Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri))
            {
                var scheme = uri.Scheme == "wss" ? "https" : "http";
                _chatUrl = $"{scheme}://{uri.Host}:{uri.Port}?token={Uri.EscapeDataString(_token)}";
            }
            else
            {
                _chatUrl = $"http://127.0.0.1:19001?token={Uri.EscapeDataString(_token)}";
            }

            WebView.Visibility = Visibility.Visible;
            WebView.CoreWebView2.Navigate(_chatUrl);
        }
        catch (Exception ex)
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            PlaceholderPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorText.Text = $"WebView2 failed: {ex.Message}";
        }
    }

    private void OnHome(object sender, RoutedEventArgs e)
    {
        if (_webViewInitialized && !string.IsNullOrEmpty(_chatUrl))
            WebView.CoreWebView2?.Navigate(_chatUrl);
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_webViewInitialized) WebView.CoreWebView2?.Reload();
    }

    private void OnRetry(object sender, RoutedEventArgs e)
    {
        if (!_webViewInitialized || string.IsNullOrEmpty(_chatUrl)) return;
        ErrorPanel.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        WebView.Visibility = Visibility.Visible;
        WebView.CoreWebView2?.Navigate(_chatUrl);
    }

    private void OnPopout(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_chatUrl))
            try { Process.Start(new ProcessStartInfo(_chatUrl) { UseShellExecute = true }); } catch { }
    }

    private void RequestChatInputFocus()
    {
        WebView.Focus(FocusState.Programmatic);

        if (!_webViewInitialized || WebView.CoreWebView2 == null)
        {
            return;
        }

        _ = FocusChatInputAsync();
    }

    private async Task FocusChatInputAsync()
    {
        try
        {
            await WebView.CoreWebView2.ExecuteScriptAsync("""
                (() => {
                    const selectors = [
                        'textarea:not([disabled])',
                        'input[type="text"]:not([disabled])',
                        'input:not([type]):not([disabled])',
                        '[contenteditable="true"]',
                        '[role="textbox"]'
                    ];
                    const isVisible = element =>
                        !!(element.offsetWidth || element.offsetHeight || element.getClientRects().length);
                    const target = selectors
                        .flatMap(selector => Array.from(document.querySelectorAll(selector)))
                        .find(isVisible);
                    if (!target) {
                        return false;
                    }
                    target.focus({ preventScroll: true });
                    return document.activeElement === target || target.contains(document.activeElement);
                })();
                """);
        }
        catch (ObjectDisposedException ex)
        {
            Logger.Warn($"Failed to focus chat input: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            Logger.Warn($"Failed to focus chat input: {ex.Message}");
        }
        catch (COMException ex)
        {
            Logger.Warn($"Failed to focus chat input: {ex.Message}");
        }
    }
}
