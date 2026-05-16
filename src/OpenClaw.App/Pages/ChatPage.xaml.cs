using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using OpenClaw.App.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace OpenClaw.App.Pages;

public sealed partial class ChatPage : Page
{
    private string _gatewayUrl = "";
    private string _token = "";
    private bool _initialized;

    public ChatPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var s = App.Current.Settings;
        _gatewayUrl = s?.GatewayUrl ?? "";
        _token = s?.Token ?? "";

        if (!_initialized)
        {
            _initialized = true;
            _ = InitializeWebViewAsync();
        }
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenClawApp", "WebView2");
            Directory.CreateDirectory(userDataFolder);
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);

            await WebView.EnsureCoreWebView2Async();

            WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            WebView.CoreWebView2.Settings.IsZoomControlEnabled = true;

            WebView.CoreWebView2.NavigationCompleted += (s, e) =>
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;

                if (!e.IsSuccess && (e.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionAborted ||
                                      e.WebErrorStatus == CoreWebView2WebErrorStatus.CannotConnect ||
                                      e.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionReset ||
                                      e.WebErrorStatus == CoreWebView2WebErrorStatus.ServerUnreachable))
                {
                    ShowError($"Cannot connect to gateway at {_gatewayUrl}.\nMake sure the gateway is running.");
                }
            };

            WebView.CoreWebView2.NavigationStarting += (s, e) =>
            {
                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;
            };

            NavigateToChat();
        }
        catch (Exception ex)
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            WebView.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorText.Text = $"Failed to initialize: {ex.Message}";
        }
    }

    private void NavigateToChat()
    {
        if (WebView.CoreWebView2 == null) return;

        if (!TryBuildChatUrl(out var url, out var error))
        {
            ShowError(error);
            return;
        }

        WebView.CoreWebView2.Navigate(url);
    }

    private bool TryBuildChatUrl(out string url, out string errorMessage)
    {
        url = "";
        errorMessage = "";

        if (!OpenClaw.Shared.GatewayUrlHelper.TryNormalizeWebSocketUrl(_gatewayUrl, out var normalized) ||
            !Uri.TryCreate(normalized, UriKind.Absolute, out var gatewayUri))
        {
            errorMessage = $"Invalid gateway URL: {_gatewayUrl}";
            return false;
        }

        var webScheme = gatewayUri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase) ? "https" : "http";

        if (webScheme == "http" && !gatewayUri.IsLoopback &&
            !string.Equals(gatewayUri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = "Web chat requires HTTPS for non-localhost connections.";
            return false;
        }

        var builder = new UriBuilder(gatewayUri) { Scheme = webScheme, Port = gatewayUri.Port };
        var baseUrl = builder.Uri.GetLeftPart(UriPartial.Authority);
        url = $"{baseUrl}?token={Uri.EscapeDataString(_token)}";
        return true;
    }

    private void ShowError(string message)
    {
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        WebView.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorText.Text = message;
    }

    private void OnHome(object sender, RoutedEventArgs e) => NavigateToChat();
    private void OnRefresh(object sender, RoutedEventArgs e) => WebView.CoreWebView2?.Reload();

    private void OnPopout(object sender, RoutedEventArgs e)
    {
        if (!TryBuildChatUrl(out var url, out _)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Error($"Failed to open browser: {ex.Message}"); }
    }
}
