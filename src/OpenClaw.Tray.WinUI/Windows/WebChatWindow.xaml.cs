using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Services.Voice;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using WinUIEx;
using Windows.Foundation;

namespace OpenClawTray.Windows;

public sealed partial class WebChatWindow : WindowEx
    , IVoiceChatWindow
{
    private readonly string _gatewayUrl;
    private readonly string _token;
    private bool _stripInjectedMemories;
    private string _pendingVoiceDraft = string.Empty;
    
    // Store event handlers for cleanup
    private TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs>? _navigationCompletedHandler;
    private TypedEventHandler<CoreWebView2, CoreWebView2NavigationStartingEventArgs>? _navigationStartingHandler;
    
    public bool IsClosed { get; private set; }

private const string TrayVoiceIntegrationScript = """
(() => {
  const isVisible = (el) => !!el && !(el.disabled === true) && el.getClientRects().length > 0;
  const memoryPattern = /<relevant-memories>[\s\S]*?<\/relevant-memories>\s*/gi;
  let desiredDraft = '';
  let stripInjectedMemories = true;
  const findComposer = () => {
    const candidates = Array.from(document.querySelectorAll('textarea, input[type="text"], [contenteditable="true"], [contenteditable="plaintext-only"]'));
    return candidates.find(isVisible) || null;
  };
  const setElementValue = (el, value) => {
    if ('value' in el) {
      const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
      const descriptor = Object.getOwnPropertyDescriptor(proto, 'value');
      if (descriptor && descriptor.set) {
        descriptor.set.call(el, value);
      } else {
        el.value = value;
      }
      el.dispatchEvent(new InputEvent('input', { bubbles: true, data: value, inputType: 'insertText' }));
      el.dispatchEvent(new Event('change', { bubbles: true }));
      return;
    }
    if (el.isContentEditable) {
      el.textContent = value;
      el.dispatchEvent(new InputEvent('input', { bubbles: true, data: value, inputType: 'insertText' }));
      el.dispatchEvent(new Event('change', { bubbles: true }));
    }
  };
  const applyDraftIfPossible = () => {
    const composer = findComposer();
    if (!composer) return false;
    setElementValue(composer, desiredDraft);
    return true;
  };
  const cleanTextNodes = () => {
    if (!stripInjectedMemories || !document.body) return false;
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    const nodes = [];
    let current;
    while ((current = walker.nextNode())) {
      nodes.push(current);
    }
    let changed = false;
    for (const node of nodes) {
      if (!node || !node.parentElement) continue;
      const tag = node.parentElement.tagName;
      if (tag === 'SCRIPT' || tag === 'STYLE' || tag === 'TEXTAREA') continue;
      const original = node.textContent || '';
      const withoutMemories = original.replace(memoryPattern, '');
      if (withoutMemories !== original) {
        const cleaned = withoutMemories.trimStart();
        node.textContent = cleaned;
        changed = true;
      }
    }
    return changed;
  };
  let refreshScheduled = false;
  const refreshView = () => {
    if (refreshScheduled) return;
    refreshScheduled = true;
    queueMicrotask(() => {
      refreshScheduled = false;
      cleanTextNodes();
      applyDraftIfPossible();
    });
  };
  const observer = new MutationObserver(() => refreshView());
  const start = () => {
    if (!document.body) return;
    observer.observe(document.body, { childList: true, subtree: true });
    refreshView();
  };
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', start, { once: true });
  } else {
    start();
  }
  window.__openClawTrayVoice = {
    setDraft(text) {
      desiredDraft = text || '';
      return applyDraftIfPossible();
    },
    setStripInjectedMemories(enabled) {
      stripInjectedMemories = !!enabled;
      refreshView();
      return true;
    },
    clearDraft() {
      desiredDraft = '';
      return applyDraftIfPossible();
    }
  };
})();
""";

    public WebChatWindow(string gatewayUrl, string token, bool stripInjectedMemories)
    {
        Logger.Info($"WebChatWindow: Constructor called, gateway={gatewayUrl}");
        _gatewayUrl = gatewayUrl;
        _token = token;
        _stripInjectedMemories = stripInjectedMemories;
        
        InitializeComponent();
        
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
        
        // Cleanup WebView2 event handlers
        if (WebView.CoreWebView2 != null)
        {
            if (_navigationCompletedHandler != null)
                WebView.CoreWebView2.NavigationCompleted -= _navigationCompletedHandler;
            if (_navigationStartingHandler != null)
                WebView.CoreWebView2.NavigationStarting -= _navigationStartingHandler;
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
            await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(TrayVoiceIntegrationScript);

            // Handle navigation events (store for cleanup)
            _navigationCompletedHandler = (s, e) =>
            {
                Logger.Info($"WebChatWindow: Navigation completed, success={e.IsSuccess}, status={e.WebErrorStatus}");
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
                _ = RefreshTrayVoiceDomStateAsync();
                
                // Show friendly error if connection failed
                if (!e.IsSuccess && (e.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionAborted ||
                                      e.WebErrorStatus == CoreWebView2WebErrorStatus.CannotConnect ||
                                      e.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionReset ||
                                      e.WebErrorStatus == CoreWebView2WebErrorStatus.ServerUnreachable))
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
                }
            };
            WebView.CoreWebView2.NavigationCompleted += _navigationCompletedHandler;

            _navigationStartingHandler = (s, e) =>
            {
                // Strip query params to avoid logging tokens
                var safeUri = e.Uri?.Split('?')[0] ?? "unknown";
                Logger.Info($"WebChatWindow: Navigation starting to {safeUri}");
                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;
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
        }
    }

    // Set to a test URL to bypass gateway (e.g., "https://www.bing.com"), or null for normal operation
    private const string? DEBUG_TEST_URL = null;

    private static bool IsLocalHost(Uri uri)
    {
        return uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
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
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        WebView.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorText.Text = message;
    }
    
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

    public async Task UpdateVoiceTranscriptDraftAsync(string text, bool clear)
    {
        _pendingVoiceDraft = clear ? string.Empty : (text ?? string.Empty);
        await RefreshTrayVoiceDomStateAsync();
    }

    public async Task SetStripInjectedMemoriesEnabledAsync(bool enabled)
    {
        _stripInjectedMemories = enabled;
        await RefreshTrayVoiceDomStateAsync();
    }

    private async Task RefreshTrayVoiceDomStateAsync()
    {
        if (WebView.CoreWebView2 == null)
        {
            return;
        }

        try
        {
            var stripJson = _stripInjectedMemories ? "true" : "false";
            await WebView.CoreWebView2.ExecuteScriptAsync(
                $"window.__openClawTrayVoice?.setStripInjectedMemories?.({stripJson});");

            var draftJson = JsonSerializer.Serialize(_pendingVoiceDraft ?? string.Empty);
            var script = string.IsNullOrWhiteSpace(_pendingVoiceDraft)
                ? "window.__openClawTrayVoice?.clearDraft?.();"
                : $"window.__openClawTrayVoice?.setDraft?.({draftJson});";

            await WebView.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            Logger.Warn($"WebChatWindow: Failed to apply voice DOM state: {ex.Message}");
        }
    }
}
