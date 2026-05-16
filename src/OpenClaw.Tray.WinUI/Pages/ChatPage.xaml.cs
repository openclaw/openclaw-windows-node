using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClawTray.Chat;
using OpenClawTray.Helpers;
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
    private MountedFunctionalChat? _functionalHost;
    private IChatDataProvider? _mountedProvider;
    private string? _chatUrl;
    private bool _webViewInitialized;
    private bool _webViewMode;
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
        // Tear down native chat (if mounted) and detach WebView2 nav handlers.
        DisposeFunctionalHost();

        _navigationCts?.Cancel();
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
        // Hide WebView2-specific UI; mount FunctionalUI host (idempotent).
        _webViewMode = false;
        StopWebViewNavigation();
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

        if (_functionalHost is not null && ReferenceEquals(_mountedProvider, provider))
        {
            PlaceholderPanel.Visibility = Visibility.Collapsed;
            ChatHost.Visibility = Visibility.Visible;
            return;
        }

        DisposeFunctionalHost();

        if (provider is null)
        {
            PlaceholderPanel.Visibility = Visibility.Visible;
            ChatHost.Visibility = Visibility.Collapsed;
            return;
        }

        PlaceholderPanel.Visibility = Visibility.Collapsed;
        ChatHost.Visibility = Visibility.Visible;
        _functionalHost = ((Window)_hub!).MountFunctionalChat(
            ChatHost,
            provider,
            onReadAloud: readAloud,
            onVoiceRequest: VoiceTranscribeAsync,
            onAttachClick: OnAttachClicked,
            onSettingsClick: () => _hub?.NavigateTo("voice"),
            onSpeakerMuteChanged: muted => (App.Current as App)?.SetChatSpeakerMuted(muted));
        _mountedProvider = provider;
    }

    private void ShowWebViewSurface(bool forceNavigate = false)
    {
        // Tear down native chat (so the WebView2 owns the row) and (re)init WebView2.
        _webViewMode = true;
        DisposeFunctionalHost();

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
            if (!NavigateWebViewToCurrentChatUrl())
                ShowMissingChatCredentialError();
            _ = forceNavigate; // explicit: parameter is currently advisory
            return;
        }

        if (string.IsNullOrEmpty(_chatUrl))
        {
            ShowMissingChatCredentialError();
            return;
        }

        if (_hub?.Settings is null) return;
        _ = InitializeWebViewAsync(_hub.Settings);
    }

    private bool NavigateWebViewToCurrentChatUrl()
    {
        if (string.IsNullOrEmpty(_chatUrl) || WebView.CoreWebView2 is null)
            return false;

        ErrorPanel.Visibility = Visibility.Collapsed;
        WebView.Visibility = Visibility.Visible;
        WebView.CoreWebView2.Navigate(_chatUrl);
        return true;
    }

    private void ShowMissingChatCredentialError()
    {
        StopWebViewNavigation();
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        WebView.Visibility = Visibility.Collapsed;
        PlaceholderPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorText.Text = "Open Connection settings to finish pairing with a gateway.";
    }

    private void StopWebViewNavigation()
    {
        try
        {
            WebView.CoreWebView2?.Stop();
            WebView.CoreWebView2?.Navigate("about:blank");
        }
        catch (Exception ex)
        {
            Logger.Warn($"ChatPage WebView stop failed: {ex.Message}");
        }
    }

    private void DisposeFunctionalHost()
    {
        var host = _functionalHost;
        _functionalHost = null;
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
            ErrorPanel.Visibility = Visibility.Collapsed;
            WebView.Visibility = Visibility.Collapsed;
            WaitingPanel.Visibility = Visibility.Visible;
            WaitingStatusText.Text = "The gateway is connected; the chat surface is still coming online.";
            RetryChatButton.Visibility = Visibility.Collapsed;
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

    private async Task<string?> VoiceTranscribeAsync(CancellationToken cancellationToken)
    {
        var voiceService = _hub?.VoiceServiceInstance;
        var host = _functionalHost;
        if (voiceService is null) return null;

        // Subscribe to streaming events during recording
        void OnTranscription(string text) => host?.SetVoiceTranscript(text);
        void OnAudioLevel(float level) => host?.SetVoiceAudioLevel(level);

        voiceService.TranscriptionReceived += OnTranscription;
        voiceService.AudioLevelChanged += OnAudioLevel;
        try
        {
            var args = new SttListenArgs
            {
                TimeoutMs = 10_000,
                Language = ""
            };
            var result = await voiceService.ListenOnceAsync(args, cancellationToken);
            return result?.Text;
        }
        finally
        {
            voiceService.TranscriptionReceived -= OnTranscription;
            voiceService.AudioLevelChanged -= OnAudioLevel;
            host?.SetVoiceTranscript(null);
            host?.SetVoiceAudioLevel(0f);
        }
    }

    private void OnAttachClicked()
    {
        Logger.Info("[ChatPage] OnAttachClicked invoked");
        _ = PickAndAttachFileAsync();
    }

    private async Task PickAndAttachFileAsync()
    {
        try
        {
            if (_hub is null)
            {
                Logger.Warn("[ChatPage] PickAndAttachFileAsync: _hub is null, cannot open picker");
                return;
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle((Window)_hub!);
            var path = await Win32FilePickerHelper.PickSingleFileAsync(hwnd, "Attach file");

            if (path is null)
            {
                Logger.Info("[ChatPage] File picker cancelled by user");
                return;
            }

            Logger.Info($"[ChatPage] File selected: {path}");
            var attachment = ChatAttachment.FromFile(path);
            _functionalHost?.AttachFile(attachment);
        }
        catch (Exception ex)
        {
            Logger.Error($"[ChatPage] File picker error: {ex}");
        }
    }
}
