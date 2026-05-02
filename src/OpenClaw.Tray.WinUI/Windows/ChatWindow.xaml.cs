using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Diagnostics;
using System.IO;
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

    public ChatWindow(string gatewayUrl, string token)
    {
        _gatewayUrl = gatewayUrl;
        _token = token;
        InitializeComponent();

        // Extend into title bar for native look
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        this.SetWindowSize(520, 750);
        this.MinWidth = 380;
        this.MinHeight = 450;
        this.CenterOnScreen();
        this.SetIcon(IconHelper.GetStatusIconPath(ConnectionStatus.Connected));

        // Hide instead of close — preserves WebView2 session for instant reopen
        Closed += OnWindowClosing;
        _ = InitializeWebViewAsync();
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
                if (!e.IsSuccess && (e.WebErrorStatus == CoreWebView2WebErrorStatus.CannotConnect ||
                                      e.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionReset ||
                                      e.WebErrorStatus == CoreWebView2WebErrorStatus.ServerUnreachable))
                {
                    WebView.Visibility = Visibility.Collapsed;
                    ErrorPanel.Visibility = Visibility.Visible;
                    ErrorText.Text = $"Cannot connect to gateway at {_gatewayUrl}";
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

    private void OnPopout(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_chatUrl))
            try { Process.Start(new ProcessStartInfo(_chatUrl) { UseShellExecute = true }); } catch { }
    }
}
