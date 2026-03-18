using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
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
    private bool _isWebViewInitialized;
    private string? _pendingUrl;
    private string? _pendingHtml;
    private readonly TaskCompletionSource<bool> _webViewReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool>? _navigationTcs;
    
    // URL validation - block dangerous schemes and private networks (IPv4 + IPv6)
    private static readonly Regex DangerousUrlPattern = new(
        @"^(file|javascript|data|vbscript):|" +                           // Dangerous schemes
        @"^https?://(localhost|127\.|10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[01])\.|169\.254\.)|" + // Private IPv4
        @"^https?://\[(::1|0:0:0:0:0:0:0:1|::)\]",                        // IPv6 localhost
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    
    /// <summary>
    /// Validates a URL for security - returns true if URL is safe
    /// </summary>
    private static bool IsUrlSafe(string url)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return IsSafeDataUrl(url);
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
    
    public CanvasWindow()
    {
        this.InitializeComponent();
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
            
            // Configure WebView2
            CanvasWebView.CoreWebView2.Settings.IsScriptEnabled = true;
            CanvasWebView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
            CanvasWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            CanvasWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            
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
    
    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        IsClosed = true;
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
        html = System.Text.RegularExpressions.Regex.Replace(
            html, @"<\s*(iframe|object|embed|applet)\b[^>]*>.*?<\s*/\s*\1\s*>",
            "<!-- blocked -->", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        // Remove self-closing variants
        html = System.Text.RegularExpressions.Regex.Replace(
            html, @"<\s*(iframe|object|embed|applet)\b[^>]*/?\s*>",
            "<!-- blocked -->", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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
