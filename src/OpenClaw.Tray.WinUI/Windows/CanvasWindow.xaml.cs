using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using WinUIEx;
using Windows.Storage.Streams;

namespace OpenClawTray.Windows;

/// <summary>
/// Canvas window - WebView2-based surface for displaying agent content
/// </summary>
public sealed partial class CanvasWindow : WindowEx
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const int SW_SHOWNORMAL = 1;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private bool _isWebViewInitialized;
    private string? _pendingUrl;
    private string? _pendingHtml;
    private readonly TaskCompletionSource<bool> _webViewReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool>? _navigationTcs;

    private readonly string _canvasDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenClawTray", "canvas");
    private FileSystemWatcher? _canvasWatcher;
    private long _lastReloadTicks = 0;
    
    // HTML sanitization — block embedded iframes/objects/embeds/applets
    private static readonly Regex s_sanitizeBlock = new(
        @"<\s*(iframe|object|embed|applet)\b[^>]*>.*?<\s*/\s*\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex s_sanitizeSelfClose = new(
        @"<\s*(iframe|object|embed|applet)\b[^>]*/?\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // URL validation - block dangerous schemes and private networks (IPv4 + IPv6)
    private static readonly Regex DangerousUrlPattern = new(
        @"^(file|javascript|data|vbscript):|" +                           // Dangerous schemes
        @"^https?://(localhost|127\.|10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[01])\.|169\.254\.)|" + // Private IPv4
        @"^https?://\[(::1|0:0:0:0:0:0:0:1|::)\]",                        // IPv6 localhost
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    
    /// <summary>
    /// Validates a URL for security - returns true if URL is safe
    /// </summary>
    private bool IsUrlSafe(string url)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return IsSafeDataUrl(url);
        }
        // Allow URLs from the canvas virtual host
        if (url.StartsWith("https://openclaw-canvas.local/", StringComparison.OrdinalIgnoreCase) ||
            url.Equals("https://openclaw-canvas.local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // Allow URLs from the trusted gateway origin with strict boundary check
        if (!string.IsNullOrEmpty(_trustedGatewayOrigin) &&
            url.StartsWith(_trustedGatewayOrigin, StringComparison.OrdinalIgnoreCase) &&
            (url.Length == _trustedGatewayOrigin.Length ||
             url[_trustedGatewayOrigin.Length] == '/' ||
             url[_trustedGatewayOrigin.Length] == '?' ||
             url[_trustedGatewayOrigin.Length] == '#'))
        {
            return true;
        }
        return !DangerousUrlPattern.IsMatch(url);
    }
    
    private static bool IsSafeDataUrl(string url)
    {
        // Allow only text/html and text/plain data URLs
        var commaIndex = url.IndexOf(',');
        if (commaIndex < 0) return false;
        
        var header = url.Substring(5, commaIndex - 5);
        if (string.IsNullOrWhiteSpace(header))
        {
            // Defaults to text/plain;charset=US-ASCII per RFC 2397
            return true;
        }
        
        var mediaType = header.Split(';', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        if (string.IsNullOrEmpty(mediaType))
        {
            return true;
        }
        
        return mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Equals("text/plain", StringComparison.OrdinalIgnoreCase);
    }
    
    public bool IsClosed { get; private set; }
    private string? _trustedGatewayOrigin;
    private string? _gatewayOriginForRewrite;
    private string? _gatewayToken;

    /// <summary>
    /// Allow URLs from the connected gateway origin. Call after creating the window
    /// so that canvas.present URLs served by the gateway are not blocked.
    /// Also rewrites gateway URLs to use the node's effective connection
    /// (e.g., localhost when connected via SSH tunnel).
    /// </summary>
    public void SetTrustedGatewayOrigin(string? gatewayUrl, string? token = null)
    {
        if (string.IsNullOrEmpty(gatewayUrl)) return;
        _gatewayToken = token;
        try
        {
            var uri = new Uri(GatewayUrlHelper.NormalizeForWebSocket(gatewayUrl));
            var httpScheme = uri.Scheme == "wss" ? "https" : "http";
            _trustedGatewayOrigin = $"{httpScheme}://{uri.Host}:{uri.Port}";
            _gatewayOriginForRewrite = _trustedGatewayOrigin;
            Logger.Info($"[Canvas] Trusted gateway origin: {_trustedGatewayOrigin}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Canvas] Failed to parse gateway origin: {ex.Message}");
        }
    }

    /// <summary>
    /// Rewrite a gateway URL to use the node's effective connection.
    /// If connected via SSH tunnel to localhost, rewrites 192.168.x.x → localhost.
    /// </summary>
    private string RewriteGatewayUrl(string url)
    {
        if (string.IsNullOrEmpty(_gatewayOriginForRewrite)) return url;

        try
        {
            // Handle relative paths — prepend the gateway origin
            if (url.StartsWith("/"))
            {
                var rewritten = _gatewayOriginForRewrite + url;
                rewritten = AppendGatewayToken(rewritten);
                Logger.Info($"[Canvas] Resolved relative URL to gateway origin");
                return rewritten;
            }

            var uri = new Uri(url);
            var httpScheme = uri.Scheme;
            var urlOrigin = $"{httpScheme}://{uri.Host}:{uri.Port}";

            // If the URL's origin differs from our effective gateway origin, rewrite it
            if (!urlOrigin.Equals(_gatewayOriginForRewrite, StringComparison.OrdinalIgnoreCase))
            {
                var rewritten = _gatewayOriginForRewrite + uri.PathAndQuery;
                rewritten = AppendGatewayToken(rewritten);
                Logger.Info($"[Canvas] Rewrote URL to effective gateway origin");
                return rewritten;
            }

            // Same origin — just add token if needed
            url = AppendGatewayToken(url);

            // If this is a canvas document path and we have it locally, use the virtual host
            if (url.Contains("/__openclaw__/canvas/documents/") && !string.IsNullOrEmpty(_canvasDir))
            {
                var pathPart = new Uri(url).AbsolutePath;
                var localRelative = pathPart.Replace("/__openclaw__/canvas/documents/", "");
                var localPath = Path.GetFullPath(Path.Combine(_canvasDir, localRelative.Replace('/', Path.DirectorySeparatorChar)));
                // Containment check — block directory traversal
                if (localPath.StartsWith(_canvasDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(localPath))
                {
                    var localUrl = $"https://openclaw-canvas.local/{localRelative}";
                    Logger.Info($"[Canvas] Using local file: {localUrl}");
                    return localUrl;
                }
            }

            return url;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Canvas] URL rewrite failed: {ex.Message}");
        }
        return url;
    }

    private string AppendGatewayToken(string url)
    {
        // Auth is handled via WebResourceRequested Bearer header injection,
        // not query params. This method is kept as a no-op for safety.
        return url;
    }
    
    public CanvasWindow()
    {
        this.InitializeComponent();
        this.SetIcon("Assets\\openclaw.ico");
        this.Closed += OnWindowClosed;
        
        // Initialize WebView2
        InitializeWebViewAsync();
    }
    
    private async void InitializeWebViewAsync()
    {
        try
        {
            LoadingRing.IsActive = true;
            CanvasWebView.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;
            
            await CanvasWebView.EnsureCoreWebView2Async();

            // Map local canvas files to a virtual hostname so canvas content
            // can be served without hitting the gateway HTTP server.
            // Files in %LOCALAPPDATA%/OpenClawTray/canvas/ are served at
            // https://openclaw-canvas.local/
            Directory.CreateDirectory(_canvasDir);
            CanvasWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "openclaw-canvas.local",
                _canvasDir,
                CoreWebView2HostResourceAccessKind.Allow);
            Logger.Info($"[Canvas] Virtual host mapped: openclaw-canvas.local → {_canvasDir}");

            // Watch for local canvas file changes and auto-reload
            _canvasWatcher = new FileSystemWatcher(_canvasDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true
            };
            _canvasWatcher.Changed += OnCanvasFileChanged;
            _canvasWatcher.Created += OnCanvasFileChanged;
            _canvasWatcher.Renamed += (s, e) => OnCanvasFileChanged(s, e);

            // Configure WebView2
            CanvasWebView.CoreWebView2.Settings.IsScriptEnabled = true;
            CanvasWebView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
            CanvasWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            CanvasWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            
            // Inject auth token for gateway requests
            if (!string.IsNullOrEmpty(_trustedGatewayOrigin) && !string.IsNullOrEmpty(_gatewayToken))
            {
                var gatewayFilter = $"{_trustedGatewayOrigin}/*";
                CanvasWebView.CoreWebView2.AddWebResourceRequestedFilter(gatewayFilter, CoreWebView2WebResourceContext.All);
                var trustedOrigin = _trustedGatewayOrigin;
                var token = _gatewayToken;
                CanvasWebView.CoreWebView2.WebResourceRequested += (s, args) =>
                {
                    // Only inject auth for requests to the trusted gateway origin
                    if (args.Request.Uri.StartsWith(trustedOrigin, StringComparison.OrdinalIgnoreCase))
                    {
                        args.Request.Headers.SetHeader("Authorization", $"Bearer {token}");
                    }
                };
                Logger.Info("[Canvas] WebView2 auth header injection configured for gateway requests");
            }

            // Handle navigation events
            CanvasWebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            
            _isWebViewInitialized = true;
            _webViewReadyTcs.TrySetResult(true);
            
            LoadingRing.IsActive = false;
            CanvasWebView.Visibility = Visibility.Visible;
            
            // Load pending content (re-validate for security)
            if (_pendingUrl != null)
            {
                var url = _pendingUrl;
                _pendingUrl = null;
                
                // Re-validate URL before navigation (defense in depth)
                if (IsUrlSafe(url))
                {
                    CanvasWebView.CoreWebView2.Navigate(url);
                }
                else
                {
                    Logger.Warn($"[Canvas] Blocked pending URL: {url.Substring(0, Math.Min(50, url.Length))}...");
                }
            }
            else if (_pendingHtml != null)
            {
                var html = _pendingHtml;
                _pendingHtml = null;
                CanvasWebView.CoreWebView2.NavigateToString(html);
            }
            else
            {
                // Default blank page with styling
                CanvasWebView.CoreWebView2.NavigateToString($@"
                    <!DOCTYPE html>
                    <html>
                    <head>
                        <style>
                            body {{ 
                                margin: 0; 
                                padding: 20px;
                                font-family: 'Segoe UI', sans-serif;
                                background: transparent;
                                color: #333;
                            }}
                            @media (prefers-color-scheme: dark) {{
                                body {{ color: #eee; }}
                            }}
                        </style>
                    </head>
                    <body>
                        <h2>{LocalizationHelper.GetString("Canvas_ReadyTitle")}</h2>
                        <p>{LocalizationHelper.GetString("Canvas_WaitingForContent")}</p>
                    </body>
                    </html>
                ");
            }
        }
        catch (Exception ex)
        {
            LoadingRing.IsActive = false;
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorText.Text = $"Failed to initialize WebView2: {ex.Message}";
            _webViewReadyTcs.TrySetException(ex);
        }
    }
    
    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (_navigationTcs != null)
        {
            var tcs = _navigationTcs;
            _navigationTcs = null;
            if (args.IsSuccess)
            {
                tcs.TrySetResult(true);
            }
            else
            {
                tcs.TrySetException(new InvalidOperationException($"Navigation failed: {args.WebErrorStatus}"));
            }
        }
        
        if (!args.IsSuccess)
        {
            // Show error for failed navigation
            ErrorPanel.Visibility = Visibility.Visible;
            CanvasWebView.Visibility = Visibility.Collapsed;
            ErrorText.Text = $"Navigation failed: {args.WebErrorStatus}";
        }
        else
        {
            ErrorPanel.Visibility = Visibility.Collapsed;
            CanvasWebView.Visibility = Visibility.Visible;
        }
    }
    
    private void OnCanvasFileChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce — ignore rapid file changes within 500ms (thread-safe)
        var nowTicks = DateTime.UtcNow.Ticks;
        var prevTicks = Interlocked.Exchange(ref _lastReloadTicks, nowTicks);
        if ((nowTicks - prevTicks) < TimeSpan.FromMilliseconds(500).Ticks) return;

        Logger.Info($"[Canvas] File changed: {e.Name}, reloading");
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isWebViewInitialized && !IsClosed)
            {
                CanvasWebView.CoreWebView2.Reload();
            }
        });
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        IsClosed = true;
        _canvasWatcher?.Dispose();
        _canvasWatcher = null;
    }
    
    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        InitializeWebViewAsync();
    }
    
    /// <summary>
    /// Navigate to a URL (validates URL security)
    /// </summary>
    public void Navigate(string url)
    {
        // Rewrite gateway URLs to use the node's effective connection
        // (e.g., gateway sends 192.168.1.254 but we're tunneled to localhost)
        url = RewriteGatewayUrl(url);

        // Validate URL - block dangerous schemes and private networks
        if (!IsUrlSafe(url))
        {
            throw new ArgumentException($"URL blocked for security: {url.Substring(0, Math.Min(50, url.Length))}...");
        }
        
        if (_isWebViewInitialized)
        {
            CanvasWebView.CoreWebView2.Navigate(url);
        }
        else
        {
            _pendingUrl = url;
        }
    }
    
    /// <summary>
    /// Load HTML content directly (sanitizes embedded navigation)
    /// </summary>
    public void LoadHtml(string html)
    {
        Logger.Debug($"[Canvas] Loading HTML content ({html.Length} chars)");
        
        // Sanitize: strip iframes/objects/embeds that could bypass URL validation
        html = SanitizeHtml(html);
        
        if (_isWebViewInitialized)
        {
            CanvasWebView.CoreWebView2.NavigateToString(html);
        }
        else
        {
            _pendingHtml = html;
        }
    }
    
    /// <summary>
    /// Strip dangerous embedded elements (iframe, object, embed, applet) from HTML.
    /// This prevents bypassing URL validation via inline HTML content.
    /// </summary>
    private static string SanitizeHtml(string html)
    {
        // Remove <iframe>, <object>, <embed>, <applet> tags and their content
        html = s_sanitizeBlock.Replace(html, "<!-- blocked -->");
        // Remove self-closing variants
        html = s_sanitizeSelfClose.Replace(html, "<!-- blocked -->");
        return html;
    }
    
    /// <summary>
    /// Execute JavaScript and return result (logs for audit)
    /// </summary>
    public async Task<string> EvalAsync(string script)
    {
        await EnsureWebViewReadyAsync();
        if (!_isWebViewInitialized)
            throw new InvalidOperationException("WebView2 not initialized");
        
        var truncatedScript = script.Length > 100 ? script.Substring(0, 100) + "..." : script;
        Logger.Debug($"[Canvas] Executing script: {truncatedScript}");
        
        var result = await CanvasWebView.CoreWebView2.ExecuteScriptAsync(script);
        return result;
    }
    
    /// <summary>
    /// Capture the canvas content as base64 image
    /// </summary>
    public async Task<string> CaptureSnapshotAsync(string format = "png")
    {
        await EnsureWebViewReadyAsync();
        if (!_isWebViewInitialized)
            throw new InvalidOperationException("WebView2 not initialized");
        
        using var stream = new InMemoryRandomAccessStream();
        
        var imageFormat = format.ToLowerInvariant() == "jpeg" 
            ? CoreWebView2CapturePreviewImageFormat.Jpeg 
            : CoreWebView2CapturePreviewImageFormat.Png;
        
        await CanvasWebView.CoreWebView2.CapturePreviewAsync(imageFormat, stream);
        
        // Read stream to bytes
        stream.Seek(0);
        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        
        return Convert.ToBase64String(bytes);
    }
    
    /// <summary>
    /// Set window position
    /// </summary>
    public void SetPosition(int x, int y)
    {
        if (x >= 0 && y >= 0)
        {
            this.Move(x, y);
        }
        else
        {
            // Center on screen
            this.CenterOnScreen();
        }
    }
    
    /// <summary>
    /// Set window size
    /// </summary>
    public void SetSize(int width, int height)
    {
        this.SetWindowSize(width, height);
    }
    
    /// <summary>
    /// Set always on top
    /// </summary>
    public void SetAlwaysOnTop(bool alwaysOnTop)
    {
        this.IsAlwaysOnTop = alwaysOnTop;
    }

    /// <summary>
    /// Force the window to the front so canvas content is visible immediately.
    /// </summary>
    public void BringToFront(bool keepTopMost)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            ShowWindow(hwnd, SW_SHOWNORMAL);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetForegroundWindow(hwnd);

            if (!keepTopMost)
            {
                SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            }
        }
        catch
        {
            // Best-effort focus behavior only.
        }
    }
    
    public async Task EnsureA2UIHostAsync(string url)
    {
        await EnsureWebViewReadyAsync();
        if (!_isWebViewInitialized)
            throw new InvalidOperationException("WebView2 not initialized");
        
        if (!IsTrustedA2UIUrl(url))
            throw new ArgumentException("A2UI host URL is not allowed");
        
        var current = CanvasWebView.CoreWebView2?.Source;
        if (!string.IsNullOrEmpty(current) && current.StartsWith(url, StringComparison.OrdinalIgnoreCase))
            return;
        
        await NavigateAndWaitAsync(url);
    }
    
    public async Task<string> SendA2UIMessageAsync(string json)
    {
        await EnsureWebViewReadyAsync();
        if (!_isWebViewInitialized)
            throw new InvalidOperationException("WebView2 not initialized");
        
        var script = BuildA2UIMessageScript(json);
        return await CanvasWebView.CoreWebView2.ExecuteScriptAsync(script);
    }
    
    public async Task<string> ResetA2UIAsync()
    {
        await EnsureWebViewReadyAsync();
        if (!_isWebViewInitialized)
            throw new InvalidOperationException("WebView2 not initialized");
        
        var script = BuildA2UIResetScript();
        return await CanvasWebView.CoreWebView2.ExecuteScriptAsync(script);
    }
    
    private Task NavigateAndWaitAsync(string url)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _navigationTcs = tcs;
        CanvasWebView.CoreWebView2.Navigate(url);
        return tcs.Task;
    }
    
    private static bool IsTrustedA2UIUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;
        
        return uri.AbsolutePath.StartsWith("/__openclaw__/a2ui/", StringComparison.OrdinalIgnoreCase);
    }
    
    private static string BuildA2UIMessageScript(string json)
    {
        var escaped = json.Replace("\\", "\\\\").Replace("`", "\\`").Replace("${", "\\${");
        return $$"""
        (() => {
          const msg = JSON.parse(`{{escaped}}`);
          const trySend = (target, method) => {
            if (target && typeof target[method] === 'function') {
              target[method](msg);
              return true;
            }
            return false;
          };
          if (trySend(window.__a2ui, 'receive')) return 'ok';
          if (trySend(window.__a2ui, 'push')) return 'ok';
          if (trySend(window.__a2ui, 'ingest')) return 'ok';
          if (trySend(window.a2ui, 'receive')) return 'ok';
          if (trySend(window.a2ui, 'push')) return 'ok';
          if (trySend(window.a2ui, 'ingest')) return 'ok';
          if (trySend(window.A2UI, 'receive')) return 'ok';
          if (trySend(window.A2UI, 'push')) return 'ok';
          if (trySend(window.A2UI, 'ingest')) return 'ok';
          try { window.dispatchEvent(new MessageEvent('message', { data: msg })); return 'event'; } catch {}
          try { window.postMessage(msg, '*'); return 'postMessage'; } catch {}
          return 'no-handler';
        })()
        """;
    }
    
    private static string BuildA2UIResetScript()
    {
        return """
        (() => {
          const tryCall = (target, method) => {
            if (target && typeof target[method] === 'function') {
              target[method]();
              return true;
            }
            return false;
          };
          if (tryCall(window.__a2ui, 'reset')) return 'ok';
          if (tryCall(window.__a2ui, 'clear')) return 'ok';
          if (tryCall(window.a2ui, 'reset')) return 'ok';
          if (tryCall(window.a2ui, 'clear')) return 'ok';
          if (tryCall(window.A2UI, 'reset')) return 'ok';
          if (tryCall(window.A2UI, 'clear')) return 'ok';
          return 'no-handler';
        })()
        """;
    }
    
    private Task EnsureWebViewReadyAsync()
    {
        return _isWebViewInitialized ? Task.CompletedTask : _webViewReadyTcs.Task;
    }
}
