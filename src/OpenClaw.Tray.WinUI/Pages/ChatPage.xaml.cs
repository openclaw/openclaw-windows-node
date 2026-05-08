using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using OpenClaw.Shared;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

public sealed partial class ChatPage : Page
{
    private HubWindow? _hub;
    private string _chatUrl = "";
    private bool _webViewInitialized;
    private global::Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs>? _navCompletedHandler;
    private global::Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2NavigationStartingEventArgs>? _navStartingHandler;

    public ChatPage()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
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
            var gatewayUrl = settings.GetEffectiveGatewayUrl();
            if (string.IsNullOrEmpty(gatewayUrl))
            {
                return;
            }

            if (!TryBuildChatUrl(gatewayUrl, settings.Token, out var chatUrl, out var errorMessage))
            {
                PlaceholderPanel.Visibility = Visibility.Collapsed;
                ErrorPanel.Visibility = Visibility.Visible;
                ErrorText.Text = errorMessage;
                return;
            }

            _chatUrl = chatUrl;

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
                    BootstrapMessageInjector.ScriptExecutor exec = script => WebView.CoreWebView2.ExecuteScriptAsync(script).AsTask();
                    _ = BootstrapMessageInjector.InjectAsync(exec, ((App)Application.Current).Settings, initialDelayMs: 500);
                }
                else if (e.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionAborted ||
                                      e.WebErrorStatus == CoreWebView2WebErrorStatus.CannotConnect ||
                                      e.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionReset ||
                                      e.WebErrorStatus == CoreWebView2WebErrorStatus.ServerUnreachable)
                {
                    WebView.Visibility = Visibility.Collapsed;
                    ErrorPanel.Visibility = Visibility.Visible;
                    ErrorText.Text = $"Cannot connect to gateway at {gatewayUrl}\n\nMake sure the gateway is running.";
                }
            };
            WebView.CoreWebView2.NavigationCompleted += _navCompletedHandler;

            _navStartingHandler = (s, e) =>
            {
                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;
            };
            WebView.CoreWebView2.NavigationStarting += _navStartingHandler;

            WebView.Visibility = Visibility.Visible;
            WebView.CoreWebView2.Navigate(_chatUrl);
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
}
