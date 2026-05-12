using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Services.Connection;
using OpenClawTray.Windows;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

public sealed partial class ChatPage : Page
{
    private HubWindow? _hub;
    private IDisposable? _chatHost;
    private IChatDataProvider? _mountedProvider;
    private string? _chatUrl;
    private bool _webViewInitialized;
    private bool _webViewMode;
    private global::Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs>? _navCompletedHandler;
    private global::Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2NavigationStartingEventArgs>? _navStartingHandler;

    public ChatPage()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Tear down native chat (if mounted) and detach WebView2 nav handlers.
        DisposeChatHost();

        if (WebView.CoreWebView2 != null)
        {
            if (_navCompletedHandler != null)
                WebView.CoreWebView2.NavigationCompleted -= _navCompletedHandler;
            if (_navStartingHandler != null)
                WebView.CoreWebView2.NavigationStarting -= _navStartingHandler;
        }

        if (_hub is not null)
            _hub.SettingsSaved -= OnSettingsSaved;

        if (App.Current is App app)
            app.ChatProviderChanged -= OnAppChatProviderChanged;

        // MEDIUM 6: detach the static debug-override subscription so that
        // an unloaded ChatPage doesn't keep responding to overrides changes
        // (the page keeps the static handler alive otherwise).
        OpenClawTray.Chat.DebugChatSurfaceOverrides.Changed -= OnDebugOverrideChanged;
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;

        // Compute a "open in browser" URL once so the toolbar button works
        // even when the gateway isn't fully reachable yet.
        if (hub.Settings is not null)
        {
            var url = TryComputeChatUrl(hub.Settings);
            if (!string.IsNullOrEmpty(url))
            {
                _chatUrl = url;
            }
        }

        // Re-mount on settings change so toggling "Use standard Gateway Chat
        // interface" swaps the surface live.
        hub.SettingsSaved -= OnSettingsSaved;
        hub.SettingsSaved += OnSettingsSaved;

        if (App.Current is App app)
        {
            app.ChatProviderChanged -= OnAppChatProviderChanged;
            app.ChatProviderChanged += OnAppChatProviderChanged;
        }

        // Also react to the per-surface debug override picked from DebugPage.
        OpenClawTray.Chat.DebugChatSurfaceOverrides.Changed -= OnDebugOverrideChanged;
        OpenClawTray.Chat.DebugChatSurfaceOverrides.Changed += OnDebugOverrideChanged;

        ApplyChatSurface();
    }

    private void OnSettingsSaved(object? sender, EventArgs e) => ApplyChatSurface();

    private void OnDebugOverrideChanged(object? sender, EventArgs e) => ApplyChatSurface();

    private void OnAppChatProviderChanged(object? sender, EventArgs e)
    {
        var dispatcher = DispatcherQueue;
        if (dispatcher is null || dispatcher.HasThreadAccess)
        {
            ApplyChatSurface();
            return;
        }

        _ = dispatcher.TryEnqueue(ApplyChatSurface);
    }

    private void ApplyChatSurface()
    {
        if (_hub?.Settings is null) return;

        // HIGH 3: re-resolve the chat URL from the current settings on every
        // surface application — _chatUrl was previously computed once in
        // Initialize() and never refreshed, so SettingsSaved (e.g. token /
        // gateway URL change) would leave the WebView pointing at a stale URL.
        var freshUrl = TryComputeChatUrl(_hub.Settings);
        var urlChanged = !string.Equals(freshUrl, _chatUrl, StringComparison.Ordinal);
        if (freshUrl is not null)
            _chatUrl = freshUrl;

        var useLegacy = OpenClawTray.Chat.DebugChatSurfaceOverrides.ResolveUseLegacy(
            OpenClawTray.Chat.DebugChatSurfaceOverrides.HubChat,
            _hub.Settings.UseLegacyWebChat);
        if (useLegacy)
            ShowWebViewSurface(forceNavigate: urlChanged);
        else
            ShowFunctionalSurface();
    }

    private static string? TryComputeChatUrl(SettingsManager settings)
    {
        return InteractiveGatewayCredentialResolver.TryResolve(
            settings,
            (App.Current as App)?.Registry,
            SettingsManager.SettingsDirectoryPath,
            DeviceIdentityFileReader.Instance,
            out var credential) &&
            credential is { IsBootstrapToken: false } &&
            GatewayChatUrlBuilder.TryBuildChatUrl(credential.GatewayUrl, credential.Token, out var url, out _)
            ? url
            : null;
    }

    private void ShowFunctionalSurface()
    {
        // Hide WebView2-specific UI; mount FunctionalUI chat host (idempotent).
        _webViewMode = false;
        WebView.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ToolbarBorder.Visibility = Visibility.Collapsed;
        HomeButton.Visibility = Visibility.Collapsed;
        RefreshButton.Visibility = Visibility.Collapsed;
        DevToolsButton.Visibility = Visibility.Collapsed;

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
        _chatHost = ((Window)_hub!).MountFunctionalChat(
            ChatHost,
            provider,
            onReadAloud: readAloud);
        _mountedProvider = provider;
    }

    private void ShowWebViewSurface(bool forceNavigate = false)
    {
        // Tear down native chat (so the WebView2 owns the row) and (re)init WebView2.
        _webViewMode = true;
        DisposeChatHost();

        ChatHost.Visibility = Visibility.Collapsed;
        PlaceholderPanel.Visibility = Visibility.Collapsed;
        ToolbarBorder.Visibility = Visibility.Visible;
        HomeButton.Visibility = Visibility.Visible;
        RefreshButton.Visibility = Visibility.Visible;
        DevToolsButton.Visibility = Visibility.Visible;

        if (_webViewInitialized)
        {
            // Already initialized — show it. The caller's `forceNavigate`
            // flag is informational; we always re-navigate so a settings
            // change (token / gateway URL) reaches the WebView.
            ErrorPanel.Visibility = Visibility.Collapsed;
            WebView.Visibility = Visibility.Visible;
            if (!string.IsNullOrEmpty(_chatUrl))
                WebView.CoreWebView2?.Navigate(_chatUrl);
            _ = forceNavigate; // explicit: parameter is currently advisory
            return;
        }

        if (_hub?.Settings is null) return;
        _ = InitializeWebViewAsync(_hub.Settings);
    }

    private void DisposeChatHost()
    {
        var host = _chatHost;
        _chatHost = null;
        _mountedProvider = null;
        try { host?.Dispose(); } catch { /* tear-down race — non-fatal */ }
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

            if (!GatewayChatHelper.TryBuildChatUrl(credential.GatewayUrl, credential.Token, out var chatUrl, out var errorMessage))
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

            await GatewayChatHelper.InitializeWebView2Async(WebView);
            _webViewInitialized = true;

            _navCompletedHandler = (s, e) =>
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;

                if (e.IsSuccess)
                {
                    // Hide the web Control UI sidebar — Hub NavigationView handles top-level nav.
                    _ = WebView.CoreWebView2.ExecuteScriptAsync(@"
                        (function() {
                            var style = document.createElement('style');
                            style.textContent = 'nav, [data-sidebar], .sidebar, aside { display: none !important; } main, [data-main], .main-content { margin-left: 0 !important; width: 100% !important; max-width: 100% !important; }';
                            document.head.appendChild(style);
                        })();
                    ");
                    ErrorPanel.Visibility = Visibility.Collapsed;
                    WebView.Visibility = Visibility.Visible;
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

    private void OnHome(object sender, RoutedEventArgs e)
    {
        if (_webViewMode && _webViewInitialized && !string.IsNullOrEmpty(_chatUrl))
            WebView.CoreWebView2?.Navigate(_chatUrl);
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_webViewMode && _webViewInitialized)
            WebView.CoreWebView2?.Reload();
    }

    private void OnDevTools(object sender, RoutedEventArgs e)
    {
        if (_webViewMode && _webViewInitialized)
            WebView.CoreWebView2?.OpenDevToolsWindow();
    }

    private void OnPopout(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_chatUrl)) return;
        try { Process.Start(new ProcessStartInfo(_chatUrl) { UseShellExecute = true }); }
        catch { /* shell launch failed — silently ignore */ }
    }
}
