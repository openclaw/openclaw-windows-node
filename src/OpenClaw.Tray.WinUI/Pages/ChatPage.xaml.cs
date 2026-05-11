using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using OpenClaw.Shared;
using OpenClawTray.Services;
using OpenClawTray.Services.Connection;
using OpenClawTray.Windows;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace OpenClawTray.Pages;

public sealed partial class ChatPage : Page
{
    private HubWindow? _hub;
    private string _chatUrl = "";
    private bool _webViewInitialized;
    private bool _navigationStarted;
    private CancellationTokenSource? _navigationCts;
    private global::Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs>? _navCompletedHandler;
    private global::Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2NavigationStartingEventArgs>? _navStartingHandler;
    private IGatewayConnectionManager? _connectionManager;
    private static readonly HttpClient s_httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    public ChatPage()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _navigationCts?.Cancel();
        if (WebView.CoreWebView2 != null)
        {
            if (_navCompletedHandler != null)
                WebView.CoreWebView2.NavigationCompleted -= _navCompletedHandler;
            if (_navStartingHandler != null)
                WebView.CoreWebView2.NavigationStarting -= _navStartingHandler;
        }
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        if (!_webViewInitialized && hub.Settings != null)
        {
            _ = InitializeWebViewAsync(hub.Settings);
        }
    }

    private async Task InitializeWebViewAsync(SettingsManager settings)
    {
        try
        {
            if (!InteractiveGatewayCredentialResolver.TryResolve(
                settings,
                _hub?.GatewayRegistry,
                SettingsManager.SettingsDirectoryPath,
                DeviceIdentityFileReader.Instance,
                out var credential) ||
                credential == null)
            {
                PlaceholderPanel.Visibility = Visibility.Collapsed;
                ErrorPanel.Visibility = Visibility.Visible;
                ErrorText.Text = "Open Connection settings to finish pairing with a gateway.";
                return;
            }

            if (credential.IsBootstrapToken)
            {
                PlaceholderPanel.Visibility = Visibility.Collapsed;
                ErrorPanel.Visibility = Visibility.Visible;
                ErrorText.Text = "Gateway pairing is not complete. Open Connection settings to finish pairing.";
                return;
            }

            if (!TryBuildChatUrl(credential.GatewayUrl, credential.Token, out var chatUrl, out var errorMessage))
            {
                PlaceholderPanel.Visibility = Visibility.Collapsed;
                ErrorPanel.Visibility = Visibility.Visible;
                ErrorText.Text = errorMessage;
                return;
            }

            _chatUrl = chatUrl;

            PlaceholderPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;
            WebView.Visibility = Visibility.Collapsed;
            WaitingPanel.Visibility = Visibility.Visible;
            WaitingStatusText.Text = "The gateway is connected; the chat surface is still coming online.";
            RetryChatButton.Visibility = Visibility.Collapsed;
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
            WebView.CoreWebView2.Settings.IsZoomControlEnabled = true;

            _navCompletedHandler = (s, e) =>
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;

                if (e.IsSuccess)
                {
                    // Hide the web Control UI sidebar since Hub NavigationView handles nav
                    _ = WebView.CoreWebView2.ExecuteScriptAsync(@"
                        (function() {
                            var style = document.createElement('style');
                            style.textContent = 'nav, [data-sidebar], .sidebar, aside { display: none !important; } main, [data-main], .main-content { margin-left: 0 !important; width: 100% !important; max-width: 100% !important; }';
                            document.head.appendChild(style);
                        })();
                    ");
                    _ = CaptureVisualTestChatAsync();
                }
                else if (e.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionAborted ||
                                      e.WebErrorStatus == CoreWebView2WebErrorStatus.CannotConnect ||
                                      e.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionReset ||
                                      e.WebErrorStatus == CoreWebView2WebErrorStatus.ServerUnreachable)
                {
                    WebView.Visibility = Visibility.Collapsed;
                    ErrorPanel.Visibility = Visibility.Visible;
                    ErrorText.Text = $"Cannot connect to gateway at {credential.GatewayUrl}\n\nMake sure the gateway is running.";
                }
            };
            WebView.CoreWebView2.NavigationCompleted += _navCompletedHandler;

            _navStartingHandler = (s, e) =>
            {
                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;
            };
            WebView.CoreWebView2.NavigationStarting += _navStartingHandler;

            _connectionManager = _hub?.ConnectionManager;
            _navigationCts?.Cancel();
            _navigationCts = new CancellationTokenSource();
            _ = NavigateWhenChatReadyAsync(_connectionManager, credential.GatewayUrl, _navigationCts.Token);
        }
        catch (Exception ex)
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            PlaceholderPanel.Visibility = Visibility.Collapsed;
            WebView.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorText.Text = $"WebView2 failed to initialize:\n{ex.Message}";
        }
    }

    private async Task NavigateWhenChatReadyAsync(
        IGatewayConnectionManager? connectionManager,
        string gatewayUrl,
        CancellationToken cancellationToken)
    {
        if (_navigationStarted) return;

        try
        {
            Logger.Info("[ChatPage] Waiting for operator handshake before chat navigation");
            var ready = await ChatNavigationReadiness.WaitForOperatorHandshakeAsync(connectionManager, TimeSpan.FromSeconds(30), cancellationToken);
            if (!ready)
            {
                ShowChatReadinessFailure("Timed out waiting for the gateway operator handshake. Retry once the gateway is ready.");
                Logger.Warn("[ChatPage] Timed out waiting for operator handshake before chat navigation");
                return;
            }

            Logger.Info("[ChatPage] Operator handshake ready; probing chat HTTP surface");
            ready = await ProbeChatSurfaceAsync(_chatUrl, TimeSpan.FromSeconds(30), cancellationToken);
            if (!ready)
            {
                ShowChatReadinessFailure($"Timed out waiting for chat at {gatewayUrl}. Retry once the gateway is ready.");
                Logger.Warn("[ChatPage] Timed out waiting for chat HTTP surface before navigation");
                return;
            }

            WaitingStatusText.Text = "Chat is ready; starting your first hatching conversation…";
            var bootstrapped = await OnboardingChatBootstrapper.BootstrapAsync(
                connectionManager?.OperatorClient,
                ((App)Application.Current).Settings,
                TimeSpan.FromSeconds(90),
                cancellationToken).ConfigureAwait(true);
            if (!bootstrapped && !((App)Application.Current).Settings.HasInjectedFirstRunBootstrap)
            {
                Logger.Warn("[ChatPage] Gateway hatching bootstrap did not complete; navigating to empty chat");
            }

            if (cancellationToken.IsCancellationRequested || _navigationStarted) return;

            _navigationStarted = true;
            WaitingPanel.Visibility = Visibility.Collapsed;
            RetryChatButton.Visibility = Visibility.Collapsed;
            WebView.Visibility = Visibility.Visible;
            Logger.Info("[ChatPage] Chat HTTP surface is serving; navigating WebView");
            WebView.CoreWebView2.Navigate(_chatUrl);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowChatReadinessFailure($"Chat failed to start:\n{ex.Message}");
            Logger.Warn($"[ChatPage] Chat readiness wait failed: {ex.Message}");
        }
    }

    private static async Task<bool> ProbeChatSurfaceAsync(string chatUrl, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var attempts = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, chatUrl);
                using var response = await s_httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(true);

                if ((int)response.StatusCode is >= 200 and < 400)
                    return true;

                Logger.Warn($"[ChatPage] Chat readiness probe attempt {attempts} returned {(int)response.StatusCode}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    throw;
                Logger.Warn($"[ChatPage] Chat readiness probe attempt {attempts} failed: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(true);
        }

        return false;
    }

    private void ShowChatReadinessFailure(string message)
    {
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        WebView.Visibility = Visibility.Collapsed;
        PlaceholderPanel.Visibility = Visibility.Collapsed;
        WaitingPanel.Visibility = Visibility.Visible;
        WaitingStatusText.Text = message;
        RetryChatButton.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
    }

    private async Task CaptureVisualTestChatAsync()
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST") != "1") return;
        if (WebView.CoreWebView2 == null) return;

        try
        {
            await Task.Delay(5000);
            var outputDir = Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST_DIR");
            if (string.IsNullOrWhiteSpace(outputDir)) return;

            Directory.CreateDirectory(outputDir);
            var path = Path.Combine(outputDir, $"chat-{DateTime.Now:yyyyMMddHHmmss}.png");
            using var stream = new InMemoryRandomAccessStream();
            await WebView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            stream.Seek(0);
            var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);
            await File.WriteAllBytesAsync(path, bytes);
            Logger.Info($"[VisualTest] Captured chat WebView {path}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[VisualTest] Chat WebView capture failed: {ex.Message}");
        }
    }

    private static bool TryBuildChatUrl(string gatewayUrl, string token, out string url, out string errorMessage)
    {
        url = string.Empty;
        errorMessage = string.Empty;

        if (!GatewayUrlHelper.TryNormalizeWebSocketUrl(gatewayUrl, out var normalizedUrl) ||
            !Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var gatewayUri))
        {
            errorMessage = $"Invalid gateway URL: {gatewayUrl}";
            return false;
        }

        var scheme = gatewayUri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase) ? "https" : "http";
        var builder = new UriBuilder(gatewayUri) { Scheme = scheme, Port = gatewayUri.Port };
        var baseUrl = builder.Uri.GetLeftPart(UriPartial.Authority);
        url = $"{baseUrl}?token={Uri.EscapeDataString(token)}";
        return true;
    }

    private void OnHome(object sender, RoutedEventArgs e)
    {
        if (_webViewInitialized && !string.IsNullOrEmpty(_chatUrl))
            WebView.CoreWebView2?.Navigate(_chatUrl);
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_webViewInitialized)
            WebView.CoreWebView2?.Reload();
    }

    private void OnPopout(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_chatUrl))
        {
            try { Process.Start(new ProcessStartInfo(_chatUrl) { UseShellExecute = true }); }
            catch { }
        }
    }

    private void OnDevTools(object sender, RoutedEventArgs e)
    {
        if (_webViewInitialized)
            WebView.CoreWebView2?.OpenDevToolsWindow();
    }

    private void OnRetryChat(object sender, RoutedEventArgs e)
    {
        if (!_webViewInitialized || string.IsNullOrEmpty(_chatUrl))
            return;

        _navigationStarted = false;
        _navigationCts?.Cancel();
        _navigationCts = new CancellationTokenSource();
        ErrorPanel.Visibility = Visibility.Collapsed;
        WaitingPanel.Visibility = Visibility.Visible;
        RetryChatButton.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        _ = NavigateWhenChatReadyAsync(_connectionManager, _hub?.GatewayRegistry?.GetById(_hub.GatewayRegistry.ActiveGatewayId ?? "")?.Url ?? "gateway", _navigationCts.Token);
    }
}
