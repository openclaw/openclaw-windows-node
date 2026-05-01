using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WinUIEx;
using Windows.Foundation;

namespace OpenClawTray.Windows;

public sealed partial class WebChatWindow : WindowEx
{
    private readonly string _gatewayUrl;
    private readonly string _token;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
    
    // Store event handlers for cleanup
    private TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs>? _navigationCompletedHandler;
    private TypedEventHandler<CoreWebView2, CoreWebView2NavigationStartingEventArgs>? _navigationStartingHandler;
    private TypedEventHandler<CoreWebView2, CoreWebView2WebMessageReceivedEventArgs>? _webMessageReceivedHandler;
    private CancellationTokenSource? _navigationTimeoutCts;

    /// <summary>
    /// Fired when the SPA sends a message to the native side via
    /// <c>window.chrome.webview.postMessage(...)</c>.
    /// </summary>
    public event EventHandler<WebBridgeMessage>? BridgeMessageReceived;

    public bool IsClosed { get; private set; }

    public WebChatWindow(string gatewayUrl, string token)
    {
        Logger.Info($"WebChatWindow: Constructor called, gateway={gatewayUrl}");
        _gatewayUrl = gatewayUrl;
        _token = token;
        
        InitializeComponent();
        VisualTestCapture.CaptureOnLoaded(RootGrid, "WebChat");
        _dispatcherQueue = DispatcherQueue;
        
        // Window configuration
        this.SetWindowSize(520, 750);
        this.MinWidth = 380;
        this.MinHeight = 450;
        this.CenterOnScreen();
        this.SetIcon(IconHelper.GetStatusIconPath(ConnectionStatus.Connected));
        
        Closed += OnWindowClosed;
        
        Logger.Info("WebChatWindow: Starting InitializeWebViewAsync");
        _ = InitializeWebViewAsync();
    }

    private void OnWindowClosed(object sender, WindowEventArgs e)
    {
        IsClosed = true;
        CancelNavigationTimeout();
        
        // Cleanup WebView2 event handlers
        if (WebView.CoreWebView2 != null)
        {
            if (_navigationCompletedHandler != null)
                WebView.CoreWebView2.NavigationCompleted -= _navigationCompletedHandler;
            if (_navigationStartingHandler != null)
                WebView.CoreWebView2.NavigationStarting -= _navigationStartingHandler;
            if (_webMessageReceivedHandler != null)
                WebView.CoreWebView2.WebMessageReceived -= _webMessageReceivedHandler;
        }
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            Logger.Info("WebChatWindow: Initializing WebView2...");
            
            // Set up user data folder for WebView2
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenClawTray", "WebView2");
            
            Directory.CreateDirectory(userDataFolder);
            Logger.Info($"WebChatWindow: User data folder: {userDataFolder}");

            // Set environment variable for user data folder
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);
            
            Logger.Info("WebChatWindow: Calling EnsureCoreWebView2Async...");
            await WebView.EnsureCoreWebView2Async();
            Logger.Info("WebChatWindow: CoreWebView2 initialized successfully");
            
            // Configure WebView2
            WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            WebView.CoreWebView2.Settings.IsZoomControlEnabled = true;

            // Wire the bidirectional native↔SPA bridge
            // SPA → native: window.chrome.webview.postMessage({ type, payload })
            _webMessageReceivedHandler = (s, e) =>
            {
                if (!IsTrustedBridgeSource(e.Source))
                {
                    Logger.Warn($"WebChatWindow: rejected bridge message from untrusted source {SanitizeBridgeLogValue(e.Source)}");
                    return;
                }

                var msg = WebBridgeMessage.TryParse(e.WebMessageAsJson);
                if (msg != null)
                {
                    Logger.Debug($"WebChatWindow: bridge message from SPA, type={SanitizeBridgeLogValue(msg.Type)}");
                    BridgeMessageReceived?.Invoke(this, msg);
                }
                else
                {
                    Logger.Warn($"WebChatWindow: received unrecognised bridge message");
                }
            };
            WebView.CoreWebView2.WebMessageReceived += _webMessageReceivedHandler;

            // Handle navigation events (store for cleanup)
            _navigationCompletedHandler = (s, e) =>
            {
                Logger.Info($"WebChatWindow: Navigation completed, success={e.IsSuccess}, status={e.WebErrorStatus}");
                CancelNavigationTimeout();
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;

                if (e.IsSuccess)
                {
                    if (ErrorPanel.Visibility == Visibility.Visible)
                        return;

                    WebView.Visibility = Visibility.Visible;
                    ErrorPanel.Visibility = Visibility.Collapsed;
                    return;
                }
                 
                // Show friendly error if connection failed
                if (e.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionAborted ||
                    e.WebErrorStatus == CoreWebView2WebErrorStatus.CannotConnect ||
                    e.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionReset ||
                    e.WebErrorStatus == CoreWebView2WebErrorStatus.ServerUnreachable ||
                    e.WebErrorStatus == CoreWebView2WebErrorStatus.Unknown)
                {
                    Logger.Info("WebChatWindow: Gateway unreachable, showing friendly error");
                    ShowErrorMessage(LocalizationHelper.GetString("WebChat_ConnectionError") + "\n\n" +
                        string.Format(LocalizationHelper.GetString("WebChat_ConnectionErrorDetail"), _gatewayUrl));
                    return;
                }

                if (!e.IsSuccess &&
                    e.WebErrorStatus.ToString().Contains("Certificate", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Info("WebChatWindow: TLS certificate issue detected");
                    ShowErrorMessage(LocalizationHelper.GetString("WebChat_CertError"));
                    return;
                }

                Logger.Info($"WebChatWindow: Navigation failed with {e.WebErrorStatus}, showing friendly error");
                ShowErrorMessage(LocalizationHelper.GetString("WebChat_ConnectionError") + "\n\n" +
                    string.Format(LocalizationHelper.GetString("WebChat_ConnectionErrorDetail"), _gatewayUrl));
            };
            WebView.CoreWebView2.NavigationCompleted += _navigationCompletedHandler;

            _navigationStartingHandler = (s, e) =>
            {
                // Strip query params to avoid logging tokens
                var safeUri = e.Uri?.Split('?')[0] ?? "unknown";
                Logger.Info($"WebChatWindow: Navigation starting to {safeUri}");
                if (ErrorPanel.Visibility == Visibility.Visible &&
                    string.Equals(safeUri, "about:blank", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;
                WebView.Visibility = Visibility.Visible;
                ErrorPanel.Visibility = Visibility.Collapsed;
                StartNavigationTimeout();
            };
            WebView.CoreWebView2.NavigationStarting += _navigationStartingHandler;

            // Navigate to chat
            NavigateToChat();
        }
        catch (Exception ex)
        {
            Logger.Error($"WebView2 initialization failed: {ex.GetType().FullName}: {ex.Message}");
            Logger.Error($"WebView2 HResult: 0x{ex.HResult:X8}");
            if (ex.InnerException != null)
            {
                Logger.Error($"WebView2 inner exception: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
            }
            Logger.Error($"WebView2 stack trace: {ex.StackTrace}");
            
            // Show error in the dialog instead of falling back to browser
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            WebView.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
            
            var errorDetails = $"Exception: {ex.GetType().FullName}\n" +
                              $"HResult: 0x{ex.HResult:X8}\n" +
                              $"Message: {ex.Message}\n\n" +
                              $"App Directory: {AppContext.BaseDirectory}\n" +
                              $"Architecture: {RuntimeInformation.ProcessArchitecture}\n" +
                              $"OS: {RuntimeInformation.OSDescription}\n\n" +
                              $"Stack Trace:\n{ex.StackTrace}";
            
            if (ex.InnerException != null)
            {
                errorDetails += $"\n\nInner Exception: {ex.InnerException.GetType().FullName}\n{ex.InnerException.Message}";
            }
            
            ErrorText.Text = errorDetails;
            _ = VisualTestCapture.CaptureAsync(RootGrid, "WebChat");
        }
    }

    // Set to a test URL to bypass gateway (e.g., "https://www.bing.com"), or null for normal operation
    private const string? DEBUG_TEST_URL = null;

    /// <summary>
    /// Sends a bridge message to the SPA via the WebView2 native→web channel.
    /// The SPA receives this via <c>window.chrome.webview.addEventListener('message', e => { const msg = e.data; ... })</c>.
    /// This method is safe to call from background threads and is a no-op if the WebView2 core is not yet initialised.
    /// </summary>
    public void PostBridgeMessage(string type, object? payload = null)
    {
        if (IsClosed)
            return;

        if (_dispatcherQueue == null)
        {
            Logger.Warn("WebChatWindow: cannot post bridge message because DispatcherQueue is unavailable");
            return;
        }

        if (!_dispatcherQueue.TryEnqueue(() => PostBridgeMessageOnUiThread(type, payload)))
        {
            Logger.Warn($"WebChatWindow: failed to enqueue bridge message, type={SanitizeBridgeLogValue(type)}");
        }
    }

    private void PostBridgeMessageOnUiThread(string type, object? payload)
    {
        if (IsClosed || WebView.CoreWebView2 == null)
            return;

        try
        {
            var msg = new WebBridgeMessage(type);
            var json = msg.ToJson(payload);
            Logger.Debug($"WebChatWindow: posting bridge message, type={SanitizeBridgeLogValue(type)}");
            WebView.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (ArgumentException ex)
        {
            Logger.Warn($"WebChatWindow: invalid bridge message payload: {ex.Message}");
        }
        catch (COMException ex)
        {
            Logger.Warn($"WebChatWindow: bridge message post failed: {ex.Message}");
        }
        catch (ObjectDisposedException ex)
        {
            Logger.Warn($"WebChatWindow: bridge message post skipped after disposal: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            Logger.Warn($"WebChatWindow: bridge message post failed: {ex.Message}");
        }
    }

    private static bool IsLocalHost(Uri uri)
    {
        return uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsTrustedBridgeSource(string? source)
    {
        return TryGetUriOrigin(source, out var sourceOrigin) &&
            TryGetExpectedBridgeOrigin(out var expectedOrigin) &&
            UriOriginsEqual(sourceOrigin, expectedOrigin);
    }

    private bool TryGetExpectedBridgeOrigin(out Uri origin)
    {
        origin = null!;

        if (!GatewayUrlHelper.TryNormalizeWebSocketUrl(_gatewayUrl, out var normalizedGatewayUrl) ||
            !Uri.TryCreate(normalizedGatewayUrl, UriKind.Absolute, out var gatewayUri))
        {
            return false;
        }

        var webScheme = gatewayUri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase)
            ? "https"
            : "http";

        var builder = new UriBuilder(gatewayUri)
        {
            Scheme = webScheme,
            Path = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };

        origin = builder.Uri;
        return true;
    }

    private static bool TryGetUriOrigin(string? uriText, out Uri origin)
    {
        origin = null!;
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
            return false;

        var builder = new UriBuilder(uri)
        {
            Path = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };

        origin = builder.Uri;
        return true;
    }

    private static bool UriOriginsEqual(Uri left, Uri right)
    {
        return string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) &&
            left.Port == right.Port;
    }

    private static string SanitizeBridgeLogValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        Span<char> buffer = stackalloc char[Math.Min(value.Length, 80)];
        var count = 0;
        foreach (var ch in value)
        {
            if (count == buffer.Length)
                break;

            buffer[count++] = char.IsControl(ch) ? ' ' : ch;
        }

        var sanitized = new string(buffer[..count]);
        return value.Length > count ? sanitized + "..." : sanitized;
    }

    private bool TryBuildChatUrl(out string url, out string errorMessage)
    {
        url = string.Empty;
        errorMessage = string.Empty;

        if (!GatewayUrlHelper.TryNormalizeWebSocketUrl(_gatewayUrl, out var normalizedGatewayUrl) ||
            !Uri.TryCreate(normalizedGatewayUrl, UriKind.Absolute, out var gatewayUri))
        {
            errorMessage = string.Format(LocalizationHelper.GetString("WebChat_InvalidUrl"), _gatewayUrl);
            return false;
        }

        var webScheme = gatewayUri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase)
            ? "https"
            : "http";

        if (webScheme == "http" && !IsLocalHost(gatewayUri))
        {
            errorMessage = LocalizationHelper.GetString("WebChat_SecureContextRequired");
            return false;
        }

        var builder = new UriBuilder(gatewayUri)
        {
            Scheme = webScheme,
            Port = gatewayUri.Port
        };

        var baseUrl = builder.Uri.GetLeftPart(UriPartial.Authority);
        url = $"{baseUrl}?token={Uri.EscapeDataString(_token)}";
        return true;
    }

    private void ShowErrorMessage(string message)
    {
        CancelNavigationTimeout();
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        WebView.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorText.Text = message;
        _ = CaptureWebChatErrorAsync();
    }

    private async Task CaptureWebChatErrorAsync()
    {
        await Task.Delay(250);
        await VisualTestCapture.CaptureAsync(RootGrid, "WebChat");
        await Task.Delay(1000);
        await VisualTestCapture.CaptureAsync(RootGrid, "WebChat");
    }

    private void StartNavigationTimeout()
    {
        CancelNavigationTimeout();

        var timeoutCts = new CancellationTokenSource();
        _navigationTimeoutCts = timeoutCts;
        _ = ShowErrorIfNavigationTimesOutAsync(timeoutCts.Token);
    }

    private void CancelNavigationTimeout()
    {
        _navigationTimeoutCts?.Cancel();
        _navigationTimeoutCts = null;
    }

    private async Task ShowErrorIfNavigationTimesOutAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(GetNavigationTimeout(), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (IsClosed || cancellationToken.IsCancellationRequested || _dispatcherQueue == null)
            return;

        if (!_dispatcherQueue.TryEnqueue(() =>
        {
            if (IsClosed ||
                cancellationToken.IsCancellationRequested ||
                LoadingRing.Visibility != Visibility.Visible)
            {
                return;
            }

            Logger.Warn("WebChatWindow: navigation timed out, showing friendly error");
            ShowErrorMessage(LocalizationHelper.GetString("WebChat_ConnectionError") + "\n\n" +
                string.Format(LocalizationHelper.GetString("WebChat_ConnectionErrorDetail"), _gatewayUrl));
        }))
        {
            Logger.Warn("WebChatWindow: could not enqueue navigation timeout handler");
        }
    }

    private static TimeSpan GetNavigationTimeout() =>
        Environment.GetEnvironmentVariable("OPENCLAW_UI_AUTOMATION") == "1"
            ? TimeSpan.FromSeconds(2)
            : TimeSpan.FromSeconds(12);
    
    private void NavigateToChat()
    {
        if (WebView.CoreWebView2 == null) return;

        // If debug URL is set, use it instead of gateway
        if (!string.IsNullOrEmpty(DEBUG_TEST_URL))
        {
            Logger.Info($"WebChatWindow: DEBUG MODE - Navigating to test URL: {DEBUG_TEST_URL}");
            WebView.CoreWebView2.Navigate(DEBUG_TEST_URL);
            return;
        }

        if (!TryBuildChatUrl(out var url, out var errorMessage))
        {
            Logger.Warn($"WebChatWindow: {errorMessage}");
            ShowErrorMessage(errorMessage);
            return;
        }

        var safeBaseUrl = url.Split('?')[0];
        Logger.Info($"WebChatWindow: Navigating to {safeBaseUrl} (token hidden)");
        StartNavigationTimeout();
        WebView.CoreWebView2.Navigate(url);
    }

    private void OnHome(object sender, RoutedEventArgs e)
    {
        NavigateToChat();
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        WebView.CoreWebView2?.Reload();
    }

    private void OnPopout(object sender, RoutedEventArgs e)
    {
        if (!TryBuildChatUrl(out var url, out var errorMessage))
        {
            Logger.Warn($"WebChatWindow: {errorMessage}");
            ShowErrorMessage(errorMessage);
            return;
        }
        
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to open in browser: {ex.Message}");
        }
    }

    private void OnDevTools(object sender, RoutedEventArgs e)
    {
        WebView.CoreWebView2?.OpenDevToolsWindow();
    }
}
