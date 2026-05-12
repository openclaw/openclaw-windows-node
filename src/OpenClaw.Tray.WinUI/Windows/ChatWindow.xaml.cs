using OpenClaw.Chat;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WinUIEx;

namespace OpenClawTray.Windows;

public sealed partial class ChatWindow : WindowEx
{
    private string _gatewayUrl;
    private string _token;
    private string _chatUrl;
    private IDisposable? _chatHost;
    private IChatDataProvider? _mountedProvider;
    private bool _webViewInitialized;
    private bool _webViewMode;
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
        _chatUrl = BuildChatUrl(gatewayUrl, token);
        InitializeComponent();

        this.SetWindowSize(480, 640);
        this.SetIcon("Assets\\openclaw.ico");

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

        // Hide instead of close — preserves native chat state for instant reopen
        Closed += OnWindowClosing;

        // a11y: Esc to hide the popup + try to focus composer on first show.
        // KeyboardAccelerator on the root content gets first-class keyboard
        // handling without needing a focus host.
        if (this.Content is FrameworkElement contentRoot)
        {
            var escAccel = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
            {
                Key = global::Windows.System.VirtualKey.Escape,
            };
            escAccel.Invoked += (_, args) =>
            {
                args.Handled = true;
                this.Hide();
            };
            contentRoot.KeyboardAccelerators.Add(escAccel);
        }

        // Subscribe to global SettingsChanged so the surface swaps when the
        // user toggles "Use standard Gateway Chat interface" while the
        // pre-warmed window is alive.
        if (App.Current is App app)
        {
            app.SettingsChanged += OnAppSettingsChanged;
            app.ChatProviderChanged += OnAppChatProviderChanged;
        }

        // Per-surface debug override (DebugPage > "Debug Overrides").
        OpenClawTray.Chat.DebugChatSurfaceOverrides.Changed -= OnDebugOverrideChanged;
        OpenClawTray.Chat.DebugChatSurfaceOverrides.Changed += OnDebugOverrideChanged;

        ApplyChatSurface();
        ApplySystemBackdrop();
    }

    private void OnAppSettingsChanged(object? sender, EventArgs e) => ApplyChatSurface();

    private void OnAppChatProviderChanged(object? sender, EventArgs e)
    {
        if (IsClosed) return;

        var dispatcher = DispatcherQueue;
        if (dispatcher is null || dispatcher.HasThreadAccess)
        {
            ApplyChatSurface();
            return;
        }

        _ = dispatcher.TryEnqueue(ApplyChatSurface);
    }

    private void OnDebugOverrideChanged(object? sender, EventArgs e) => ApplyChatSurface();

    private void ApplySystemBackdrop()
    {
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
    }

    private void ApplyChatSurface()
    {
        var setting = (App.Current as App)?.Settings?.UseLegacyWebChat ?? false;
        var useLegacy = OpenClawTray.Chat.DebugChatSurfaceOverrides.ResolveUseLegacy(
            OpenClawTray.Chat.DebugChatSurfaceOverrides.TrayChat,
            setting);
        if (useLegacy)
            ShowWebViewSurface();
        else
            ShowFunctionalSurface();
    }

    private void ShowFunctionalSurface()
    {
        _webViewMode = false;
        WebView.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        TryMountFunctionalChat();
    }

    private void ShowWebViewSurface()
    {
        _webViewMode = true;

        // Tear down native chat so the WebView2 owns the row.
        DisposeChatHost();

        ChatHost.Visibility = Visibility.Collapsed;
        PlaceholderPanel.Visibility = Visibility.Collapsed;

        if (_webViewInitialized)
        {
            ErrorPanel.Visibility = Visibility.Collapsed;
            WebView.Visibility = Visibility.Visible;
            if (!string.IsNullOrEmpty(_chatUrl))
                WebView.CoreWebView2?.Navigate(_chatUrl);
            return;
        }

        _ = InitializeWebViewAsync();
    }

    /// <summary>
    /// Re-resolve the gateway URL and token, and reload the WebView2 if either changed.
    /// Bug 2 fix: ChatWindow caches credentials at construction. When the pre-warmed window
    /// is created before pairing completes, its cached token is empty/stale. App calls this
    /// before re-activating the cached window so the freshest credentials are used.
    /// </summary>
    public void RefreshCredentials(string gatewayUrl, string token)
    {
        gatewayUrl ??= string.Empty;
        token ??= string.Empty;

        _gatewayUrl = gatewayUrl;
        _token = token;
        _chatUrl = BuildChatUrl(_gatewayUrl, _token);

        // HIGH 4: never log the full chat URL — its query string contains the
        // auth token. Strip the query before logging.
        Logger.Info($"[ChatWindow] Refreshing to {SafeLogUrl(_chatUrl)}");

        // If WebView2 is already up, navigate it to the refreshed URL so the user gets a
        // working chat instead of the pre-warmed (auth-failed) view.
        // BUT only when we're actively in webview mode — otherwise this would
        // un-hide the WebView on top of the active native surface (e.g. when
        // the Debug Overrides force the Companion Chat UI on the Tray popup).
        if (_webViewMode && _webViewInitialized && WebView?.CoreWebView2 != null)
        {
            if (string.IsNullOrEmpty(_chatUrl))
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
                WebView.Visibility = Visibility.Collapsed;
                ErrorPanel.Visibility = Visibility.Visible;
                ErrorText.Text = "Unable to load chat. The gateway URL or token is not available.";
                return;
            }

            try
            {
                ErrorPanel.Visibility = Visibility.Collapsed;
                WebView.Visibility = Visibility.Visible;
                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;
                WebView.CoreWebView2.Navigate(_chatUrl);
            }
            catch (Exception ex)
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
                WebView.Visibility = Visibility.Collapsed;
                ErrorPanel.Visibility = Visibility.Visible;
                ErrorText.Text = $"Unable to load chat. Please try again. ({ex.Message})";
                Logger.Warn($"ChatWindow.RefreshCredentials navigate failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Bug 4 (PR #274): exposes a script executor wrapping CoreWebView2.ExecuteScriptAsync
    /// so callers (e.g. App.ShowChatWindow) can invoke BootstrapMessageInjector without
    /// the WebView2 control field leaking out of this window. Returns null if the
    /// CoreWebView2 isn't ready yet.
    /// </summary>
    public Func<string, Task<string>>? TryGetScriptExecutor()
    {
        if (!_webViewInitialized || WebView?.CoreWebView2 == null)
        {
            return null;
        }
        var core = WebView.CoreWebView2;
        return script => core.ExecuteScriptAsync(script).AsTask();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            PlaceholderPanel.Visibility = Visibility.Collapsed;
            LoadingRing.IsActive = true;
            LoadingRing.Visibility = Visibility.Visible;

            await GatewayChatHelper.InitializeWebView2Async(WebView);
            _webViewInitialized = true;

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
                    ErrorPanel.Visibility = Visibility.Collapsed;
                    WebView.Visibility = Visibility.Visible;
                    RequestChatInputFocus();
                    OpenClawTray.Services.BootstrapMessageInjector.ScriptExecutor exec = script => WebView.CoreWebView2.ExecuteScriptAsync(script).AsTask();
                    _ = OpenClawTray.Services.BootstrapMessageInjector.InjectAsync(exec, ((App)Microsoft.UI.Xaml.Application.Current).Settings, initialDelayMs: 500);
                }
            };

            WebView.Visibility = Visibility.Visible;
            if (!string.IsNullOrEmpty(_chatUrl))
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

    private void TryMountFunctionalChat()
    {
        var app = App.Current as App;
        var provider = app?.ChatProvider;
        Func<string, Task>? readAloud = app is null ? null : app.SpeakChatTextAsync;

        if (_chatHost is not null && ReferenceEquals(_mountedProvider, provider))
        {
            PlaceholderPanel.Visibility = Visibility.Collapsed;
            ChatHost.Visibility = Visibility.Visible;
            return;
        }

        DisposeChatHost();

        if (provider is null)
        {
            PlaceholderPanel.Visibility = Visibility.Visible;
            ChatHost.Visibility = Visibility.Collapsed;
            return;
        }

        PlaceholderPanel.Visibility = Visibility.Collapsed;
        ChatHost.Visibility = Visibility.Visible;
        _chatHost = ((Window)this).MountFunctionalChat(
            ChatHost,
            provider,
            onReadAloud: readAloud);
        _mountedProvider = provider;
    }

    private void DisposeChatHost()
    {
        var host = _chatHost;
        _chatHost = null;
        _mountedProvider = null;
        try { host?.Dispose(); } catch { /* tear-down race — non-fatal */ }
    }

    private static string BuildChatUrl(string gatewayUrl, string token)
    {
        return GatewayChatUrlBuilder.TryBuildChatUrl(gatewayUrl, token, out var url, out _)
            ? url
            : string.Empty;
    }

    private bool _backdropAppliedOnce;

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            // First time the window actually becomes visible/active — re-apply the
            // system backdrop. Setting SystemBackdrop on a pre-warmed (never shown)
            // window doesn't always attach the controller, which is why acrylic
            // appeared blank until the user toggled it from the exploration panel.
            if (!_backdropAppliedOnce)
            {
                _backdropAppliedOnce = true;
                ApplySystemBackdrop();
            }

            // a11y: place keyboard focus on the composer text box so the user
            // can start typing immediately. Defer to next dispatcher pass so
            // FunctionalUI has finished mounting the composer.
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (this.Content is FrameworkElement root && FindFirstFocusableTextBox(root) is { } tb)
                    tb.Focus(FocusState.Programmatic);
            });
            return;
        }

        // Pinned by debug automation — keep open for side-by-side manual testing.
        if (ChatWindowPinState.IsPinned) return;
        this.Hide();
    }

    private static Microsoft.UI.Xaml.Controls.TextBox? FindFirstFocusableTextBox(DependencyObject root)
    {
        if (root is Microsoft.UI.Xaml.Controls.TextBox tb && tb.IsEnabled && tb.Visibility == Visibility.Visible)
            return tb;
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (FindFirstFocusableTextBox(child) is { } found) return found;
        }
        return null;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        this.Hide();
    }

    /// <summary>Position near the system tray and show with animation.</summary>
    public void ShowNearTray()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        GetCursorPos(out POINT pt);

        var hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(hMon, ref mi);
        var work = mi.rcWork;

        uint dpi = GetDpiForWindow(hwnd);
        double scale = dpi / 96.0;

        int panelWPx = (int)(480 * scale);
        int panelHPx = (int)(640 * scale);

        int margin = 8;
        int x = work.Right - panelWPx - margin;
        int y = work.Bottom - panelHPx - margin;

        this.Move(x, y);
        this.SetWindowSize(480, 640);

        // Provider may have arrived after construction — re-apply surface so
        // a native-mode window swaps placeholder → live tree on first show.
        ApplyChatSurface();

        this.Show();
        SetForegroundWindow(hwnd);
        RequestChatInputFocus();
    }

    /// <summary>Show near tray. FunctionalUI renders synchronously so no animation gating needed.</summary>
    public void ShowNearTrayAnimated() => ShowNearTray();

    private void OnWindowClosing(object sender, WindowEventArgs args)
    {
        // Intercept close → hide instead (keeps native chat state warm).
        args.Handled = true;
        this.Hide();
    }

    /// <summary>Actually close and dispose (called on app shutdown).</summary>
    public void ForceClose()
    {
        Closed -= OnWindowClosing;
        if (App.Current is App app)
        {
            app.SettingsChanged -= OnAppSettingsChanged;
            app.ChatProviderChanged -= OnAppChatProviderChanged;
        }
        OpenClawTray.Chat.DebugChatSurfaceOverrides.Changed -= OnDebugOverrideChanged;
        IsClosed = true;
        DisposeChatHost();
        Close();
    }

    private void OnPopout(object sender, RoutedEventArgs e)
    {
        // Open in Companion app — route to the Hub window's chat tab so the
        // full companion experience is available, then dismiss the tray popup.
        try
        {
            (App.Current as App)?.ShowHub("chat");
            this.Hide();
        }
        catch { }
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

    /// <summary>
    /// Strip the query string (which carries <c>?token=…</c>) from a chat URL
    /// before logging. Returns the bare scheme + authority + path so the host
    /// is still recognisable for diagnostics.
    /// </summary>
    private static string SafeLogUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "(empty)";
        if (Uri.TryCreate(url, UriKind.Absolute, out var u))
            return u.GetLeftPart(UriPartial.Path);
        return "(unparseable)";
    }
}
