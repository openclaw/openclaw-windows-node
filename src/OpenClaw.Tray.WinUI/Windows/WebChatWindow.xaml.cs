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
using System.Threading.Tasks;
using WinUIEx;
using Windows.Foundation;

namespace OpenClawTray.Windows;

public sealed partial class WebChatWindow : WindowEx
    , IVoiceChatWindow
{
    private readonly string _gatewayUrl;
    private readonly string _token;
    private readonly WebChatVoiceDomState _voiceDomState;
    private bool _voiceDomReady;

    private TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs>? _navigationCompletedHandler;
    private TypedEventHandler<CoreWebView2, CoreWebView2NavigationStartingEventArgs>? _navigationStartingHandler;

    public bool IsClosed { get; private set; }

    public WebChatWindow(string gatewayUrl, string token)
    {
        Logger.Info($"WebChatWindow: Constructor called, gateway={gatewayUrl}");
        _gatewayUrl = gatewayUrl;
        _token = token;
        _voiceDomState = new WebChatVoiceDomState();

        InitializeComponent();

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
        _voiceDomReady = false;

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

            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenClawTray", "WebView2");

            Directory.CreateDirectory(userDataFolder);
            Logger.Info($"WebChatWindow: User data folder: {userDataFolder}");

            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);

            Logger.Info("WebChatWindow: Calling EnsureCoreWebView2Async...");
            await WebView.EnsureCoreWebView2Async();
            Logger.Info("WebChatWindow: CoreWebView2 initialized successfully");

            WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            WebView.CoreWebView2.Settings.IsZoomControlEnabled = true;
            await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(WebChatVoiceDomBridge.DocumentCreatedScript);

            _voiceDomReady = false;

            _navigationCompletedHandler = (s, e) =>
            {
                Logger.Info($"WebChatWindow: Navigation completed, success={e.IsSuccess}, status={e.WebErrorStatus}");
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
                _voiceDomReady = e.IsSuccess;

                if (e.IsSuccess)
                {
                    _ = RefreshTrayVoiceDomStateAsync();
                }

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
                var safeUri = e.Uri?.Split('?')[0] ?? "unknown";
                Logger.Info($"WebChatWindow: Navigation starting to {safeUri}");
                _voiceDomReady = false;
                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;
            };
            WebView.CoreWebView2.NavigationStarting += _navigationStartingHandler;

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
        _voiceDomState.SetDraft(text, clear);
        await RefreshTrayVoiceDomStateAsync();
    }

    public async Task AppendVoiceConversationTurnAsync(VoiceConversationTurnEventArgs args)
    {
        await Task.CompletedTask;
    }

    private async Task RefreshTrayVoiceDomStateAsync()
    {
        if (WebView.CoreWebView2 == null || !_voiceDomReady || IsClosed)
        {
            return;
        }

        try
        {
            await WebView.CoreWebView2.ExecuteScriptAsync(
                WebChatVoiceDomBridge.BuildSetDraftScript(_voiceDomState.PendingDraft));
            await WebView.CoreWebView2.ExecuteScriptAsync(WebChatVoiceDomBridge.ClearLegacyTurnsScript);
        }
        catch (Exception ex)
        {
            Logger.Warn($"WebChatWindow: Failed to apply voice DOM state: {ex.Message}");
        }
    }
}
