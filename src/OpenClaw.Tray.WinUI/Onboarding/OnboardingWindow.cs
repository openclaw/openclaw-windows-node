using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Onboarding.Services;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Hosting;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;
using WinUIEx;

namespace OpenClawTray.Onboarding;

/// <summary>
/// Host window for the functional UI onboarding wizard.
/// Manages a WebView2 overlay for the Chat page to provide a consistent
/// chat experience that matches the post-setup WebChatWindow.
/// Supports visual test capture via OPENCLAW_VISUAL_TEST env var.
/// </summary>
public sealed class OnboardingWindow : WindowEx
{
    public event EventHandler? OnboardingCompleted;
    public bool Completed { get; private set; }

    private readonly SettingsManager _settings;
    private readonly FunctionalHostControl _host;
    private readonly string? _visualTestDir;
    private readonly DispatcherQueue _dispatcherQueue;
    private int _captureIndex;

    // WebView2 overlay for Chat page
    private readonly Grid _rootGrid;
    private readonly Grid _chatOverlay;
    private WebView2? _chatWebView;
    private ProgressRing? _chatLoadingRing;
    private TextBlock? _chatErrorText;
    private Button? _chatRetryButton;
    private bool _chatWebViewInitialized;
    private readonly OnboardingState _state;
    private bool _stateDisposed;

    public OnboardingWindow(SettingsManager settings)
    {
        _settings = settings;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _visualTestDir = Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST") == "1"
            ? ValidateTestDir(Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST_DIR")
              ?? Path.Combine(Path.GetTempPath(), "openclaw-visual-test"))
            : null;

        Title = LocalizationHelper.GetString("Onboarding_Title");
        this.SetWindowSize(720, 900);
        this.CenterOnScreen();
        this.SetIcon("Assets\\openclaw.ico");
        SystemBackdrop = new MicaBackdrop();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }

        _state = new OnboardingState(settings);
        _state.Finished += OnOnboardingFinished;
        _state.RouteChanged += OnRouteChanged;

        // Optional override for visual tests / engineering: jump straight to a route.
        // Accepts the OnboardingRoute enum name (e.g., "Connection").
        var startRoute = Environment.GetEnvironmentVariable("OPENCLAW_ONBOARDING_START_ROUTE");
        if (!string.IsNullOrWhiteSpace(startRoute) &&
            Enum.TryParse<OnboardingRoute>(startRoute, ignoreCase: true, out var parsed))
        {
            _state.CurrentRoute = parsed;
        }
        // Optional override for visual tests: pre-select a connection mode (Local/Wsl/Remote/Ssh/Later).
        var startMode = Environment.GetEnvironmentVariable("OPENCLAW_ONBOARDING_START_MODE");
        if (!string.IsNullOrWhiteSpace(startMode) &&
            Enum.TryParse<ConnectionMode>(startMode, ignoreCase: true, out var parsedMode))
        {
            _state.Mode = parsedMode;
        }

        _host = new FunctionalHostControl();
        _host.Mount(ctx =>
        {
            var (s, _) = ctx.UseState(_state);
            return Factories.Component<OnboardingApp, OnboardingState>(s);
        });

        // Build the chat overlay (hidden by default)
        // Leave bottom 60px uncovered so the functional UI nav bar (Back/Next/dots) is visible and clickable
        _chatOverlay = BuildChatOverlay();
        _chatOverlay.Visibility = Visibility.Collapsed;
        _chatOverlay.VerticalAlignment = VerticalAlignment.Top;

        // Root grid: functional UI host fills everything, overlay sits on top (except nav bar)
        _rootGrid = new Grid
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White)
        };
        _rootGrid.Children.Add(_host);
        _rootGrid.Children.Add(_chatOverlay);
        Content = _rootGrid;
        Closed += OnClosed;

        // Size the overlay after layout — leave space for the nav bar
        // Nav bar is ~60px + VStack bottom padding 20px = 80px minimum
        _rootGrid.SizeChanged += (_, args) =>
        {
            _chatOverlay.Height = Math.Max(0, args.NewSize.Height - 84);
        };

        // Auto-capture in visual test mode
        if (_visualTestDir != null)
        {
            Directory.CreateDirectory(_visualTestDir);

            _host.Loaded += (_, _) =>
            {
                DispatcherQueue.GetForCurrentThread().TryEnqueue(
                    DispatcherQueuePriority.Low,
                    () => _ = CaptureCurrentPageAsync());
            };

            Task.Delay(1500).ContinueWith(_ =>
                _dispatcherQueue.TryEnqueue(() => _ = CaptureCurrentPageAsync()),
                TaskScheduler.Default);
            Task.Delay(5000).ContinueWith(_ =>
                _dispatcherQueue.TryEnqueue(() => _ = CaptureCurrentPageAsync()),
                TaskScheduler.Default);

            _state.PageChanged += (_, _) =>
            {
                Task.Delay(500).ContinueWith(_ =>
                    _dispatcherQueue.TryEnqueue(() => _ = CaptureCurrentPageAsync()),
                    TaskScheduler.Default);
            };
        }
    }

    private Grid BuildChatOverlay()
    {
        var grid = new Grid();

        // Try to use theme-aware brush, fall back to white
        try
        {
            grid.Background = (Microsoft.UI.Xaml.Media.Brush)
                Microsoft.UI.Xaml.Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        }
        catch
        {
            grid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.Colors.White);
        }

        // Match the functional UI layout: 20px padding, header + WebView2 content
        grid.Padding = new Thickness(20, 0, 20, 0);
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(70) });   // Header space
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // WebView2

        // Header: lobster icon + title (matches other pages)
        var headerStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var iconBlock = new TextBlock
        {
            Text = "🦞",
            FontSize = 48,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        headerStack.Children.Add(iconBlock);
        Grid.SetRow(headerStack, 0);
        grid.Children.Add(headerStack);

        // Chat content area
        var chatArea = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        chatArea.CornerRadius = new CornerRadius(8);

        _chatWebView = new WebView2
        {
            Visibility = Visibility.Collapsed
        };
        chatArea.Children.Add(_chatWebView);

        _chatLoadingRing = new ProgressRing
        {
            IsActive = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        chatArea.Children.Add(_chatLoadingRing);

        _chatErrorText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.7,
            FontSize = 14,
            MaxWidth = 400
        };
        chatArea.Children.Add(_chatErrorText);

        Grid.SetRow(chatArea, 1);
        grid.Children.Add(chatArea);

        return grid;
    }

    private void OnRouteChanged(object? sender, OnboardingRoute route)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (route == OnboardingRoute.Chat)
            {
                _chatOverlay.Visibility = Visibility.Visible;
                if (!_chatWebViewInitialized)
                    _ = InitializeChatWebViewAsync();
            }
            else
            {
                _chatOverlay.Visibility = Visibility.Collapsed;
            }
        });
    }

    private async Task InitializeChatWebViewAsync()
    {
        if (_chatWebView == null) return;
        _chatWebViewInitialized = true;

        try
        {
            Logger.Info("[OnboardingChat] Initializing WebView2 chat overlay");

            var gatewayUrl = _state.Settings.GetEffectiveGatewayUrl();
            // Use settings token for chat URL — this is the gateway shared secret
            // that the chat web UI's JavaScript uses for WebSocket authentication.
            // NOTE: Do NOT use the client's connect auth token here. After device pairing,
            // that becomes the Ed25519 device token, which the HTTP chat JS doesn't understand.
            var token = _state.Settings.Token;

            // Pre-flight: verify gateway is reachable before loading chat
            var app = (App)Microsoft.UI.Xaml.Application.Current;
            var gatewayClient = app.GatewayClient;
            if (gatewayClient == null || !gatewayClient.IsConnectedToGateway)
            {
                Logger.Warn("[OnboardingChat] Gateway not connected, waiting...");
                for (int i = 0; i < 15; i++)
                {
                    await Task.Delay(1000);
                    gatewayClient = app.GatewayClient;
                    if (gatewayClient?.IsConnectedToGateway == true) break;
                }
                if (gatewayClient == null || !gatewayClient.IsConnectedToGateway)
                {
                    Logger.Warn("[OnboardingChat] Gateway still not connected after 15s");
                    ShowChatError("Gateway is not connected.\nComplete the connection setup first, then come back to chat.");
                    return;
                }
                Logger.Info("[OnboardingChat] Gateway connected after waiting");
            }

            // Dev port override for testing with non-default gateway port
            if (gatewayUrl == "ws://localhost:18789")
            {
                var devPort = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_PORT");
                // SECURITY: Validate port is a valid number in range 1-65535
                if (!string.IsNullOrEmpty(devPort) && int.TryParse(devPort, out var port) && port >= 1 && port <= 65535)
                    gatewayUrl = $"ws://localhost:{port}";
                else if (!string.IsNullOrEmpty(devPort))
                    Logger.Warn($"[OnboardingChat] Invalid OPENCLAW_GATEWAY_PORT value: {devPort}");
            }

            await GatewayChatHelper.InitializeWebView2Async(_chatWebView);

            // Bridge JS console to app logs via WebView2 postMessage
            _chatWebView.CoreWebView2.WebMessageReceived += (s, e) =>
            {
                try
                {
                    var msg = e.TryGetWebMessageAsString();
                    if (!string.IsNullOrEmpty(msg))
                        Logger.Debug($"[OnboardingChat-JS] {msg}");
                }
                catch { }
            };

            // SECURITY: Restrict navigation to gateway origin only and strip tokens from logs
            // (matches existing WebChatWindow.xaml.cs pattern)
            string? _allowedOrigin = null;

            _chatWebView.CoreWebView2.NavigationStarting += (s, e) =>
            {
                // Strip query params to avoid logging tokens
                var safeUri = e.Uri?.Split('?')[0] ?? "unknown";
                Logger.Info($"[OnboardingChat] Navigation starting to {safeUri}");

                // Block navigation to unexpected origins (prevent open redirects)
                if (_allowedOrigin != null && e.Uri != null)
                {
                    if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var navUri))
                    {
                        var navOrigin = $"{navUri.Scheme}://{navUri.Authority}";
                        if (!string.Equals(navOrigin, _allowedOrigin, StringComparison.OrdinalIgnoreCase)
                            && !navUri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase))
                        {
                            Logger.Warn($"[OnboardingChat] Blocked navigation to external origin: {safeUri}");
                            e.Cancel = true;
                        }
                    }
                }
            };

            _chatWebView.CoreWebView2.NavigationCompleted += (s, e) =>
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (_chatLoadingRing != null)
                    {
                        _chatLoadingRing.IsActive = false;
                        _chatLoadingRing.Visibility = Visibility.Collapsed;
                    }

                    if (!e.IsSuccess)
                    {
                        Logger.Warn($"[OnboardingChat] Navigation failed: {e.WebErrorStatus}");
                        ShowChatError($"Could not load chat ({e.WebErrorStatus}).\nMake sure the gateway is running.");
                        return;
                    }

                    if (_chatWebView != null)
                    {
                        _chatWebView.Visibility = Visibility.Visible;

                        // Inject console bridge — forward JS errors/warnings to app log via postMessage
                        _ = _chatWebView.CoreWebView2.ExecuteScriptAsync(@"
                            (function() {
                                const origError = console.error;
                                const origWarn = console.warn;
                                const origLog = console.log;
                                console.error = function() {
                                    origError.apply(console, arguments);
                                    try { window.chrome.webview.postMessage('[ERROR] ' + Array.from(arguments).join(' ')); } catch(e) {}
                                };
                                console.warn = function() {
                                    origWarn.apply(console, arguments);
                                    try { window.chrome.webview.postMessage('[WARN] ' + Array.from(arguments).join(' ')); } catch(e) {}
                                };
                                // Also forward OpenClaw-specific logs
                                const origLogFn = console.log;
                                console.log = function() {
                                    origLogFn.apply(console, arguments);
                                    const msg = Array.from(arguments).join(' ');
                                    if (msg.includes('OpenClaw') || msg.includes('openclaw') || msg.includes('websocket') || msg.includes('WebSocket') || msg.includes('session') || msg.includes('error') || msg.includes('Error'))
                                        try { window.chrome.webview.postMessage('[LOG] ' + msg); } catch(e) {}
                                };
                                // Capture unhandled errors
                                window.addEventListener('error', function(e) {
                                    try { window.chrome.webview.postMessage('[UNCAUGHT] ' + e.message + ' at ' + e.filename + ':' + e.lineno); } catch(ex) {}
                                });
                                window.addEventListener('unhandledrejection', function(e) {
                                    try { window.chrome.webview.postMessage('[REJECTION] ' + (e.reason?.message || e.reason || 'unknown')); } catch(ex) {}
                                });
                                window.chrome.webview.postMessage('[BRIDGE] Console bridge installed');
                            })();
                        ");

                        _ = SendBootstrapMessageAsync();
                    }
                });
            };

            if (GatewayChatHelper.TryBuildChatUrl(gatewayUrl, token, out var url, out var error, sessionKey: "onboarding"))
            {
                // Record allowed origin for NavigationStarting restriction
                if (Uri.TryCreate(url, UriKind.Absolute, out var chatUri))
                    _allowedOrigin = $"{chatUri.Scheme}://{chatUri.Authority}";

                var safeUrl = url.Split('?')[0];
                Logger.Info($"[OnboardingChat] Navigating to {safeUrl} (session=onboarding)");
                _chatWebView.CoreWebView2.Navigate(url);
            }
            else
            {
                Logger.Warn($"[OnboardingChat] URL build failed: {error}");
                ShowChatError(error);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[OnboardingChat] WebView2 init failed: {ex.Message}");
            ShowChatError($"Chat unavailable: {ex.Message}\n\nYou can chat from the tray menu after setup.");
        }
    }

    private void ShowChatError(string message)
    {
        if (_chatLoadingRing != null)
        {
            _chatLoadingRing.IsActive = false;
            _chatLoadingRing.Visibility = Visibility.Collapsed;
        }
        if (_chatErrorText != null)
        {
            _chatErrorText.Text = message;
            _chatErrorText.Visibility = Visibility.Visible;
        }

        // Add retry button if not already present
        if (_chatRetryButton == null && _chatErrorText?.Parent is Panel parentPanel)
        {
            _chatRetryButton = new Button
            {
                Content = "Retry",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 40, 0, 0)
            };
            _chatRetryButton.Click += (s, e) =>
            {
                _chatWebViewInitialized = false;
                if (_chatErrorText != null) _chatErrorText.Visibility = Visibility.Collapsed;
                if (_chatRetryButton != null) _chatRetryButton.Visibility = Visibility.Collapsed;
                if (_chatLoadingRing != null)
                {
                    _chatLoadingRing.IsActive = true;
                    _chatLoadingRing.Visibility = Visibility.Visible;
                }
                _ = InitializeChatWebViewAsync();
            };
            parentPanel.Children.Add(_chatRetryButton);
        }
        else if (_chatRetryButton != null)
        {
            _chatRetryButton.Visibility = Visibility.Visible;
        }
    }

    private bool _bootstrapSent;

    /// <summary>
    /// Auto-sends the bootstrap kickoff message after the web chat loads.
    /// Waits for the WebSocket to connect, then injects the message via JS.
    /// Matches macOS's maybeKickoffOnboardingChat behavior.
    /// </summary>
    private async Task SendBootstrapMessageAsync()
    {
        if (_bootstrapSent || _chatWebView?.CoreWebView2 == null) return;
        _bootstrapSent = true;

        const string bootstrapMessage =
            "Hi! I just installed OpenClaw and you're my brand-new agent. " +
            "Please start the first-run ritual from BOOTSTRAP.md, ask one question at a time, " +
            "and before we talk about WhatsApp/Telegram, visit soul.md with me to craft SOUL.md: " +
            "ask what matters to me and how you should be. Then guide me through choosing " +
            "how we should talk (web-only, WhatsApp, or Telegram).";

        try
        {
            // Wait for the web UI to initialize its WebSocket connection
            await Task.Delay(3000);

            // Inject JS that finds the chat input and sends the bootstrap message.
            // The Lit-based UI uses shadow DOM, so we traverse through custom elements.
            // SECURITY: Use JsonSerializer to safely encode the message as a JS string literal,
            // preventing XSS via template expression injection (${...}), quotes, or backslashes.
            var safeMsg = System.Text.Json.JsonSerializer.Serialize(bootstrapMessage);
            var js = $$"""
            (function() {
                const msg = {{safeMsg}};

                // Strategy 1: Find textarea/input in the page (may be in shadow DOM)
                function findInput(root) {
                    const inputs = root.querySelectorAll('textarea, input[type="text"]');
                    for (const input of inputs) {
                        if (input.offsetParent !== null || input.offsetHeight > 0) return input;
                    }
                    // Search shadow DOMs
                    const elements = root.querySelectorAll('*');
                    for (const el of elements) {
                        if (el.shadowRoot) {
                            const found = findInput(el.shadowRoot);
                            if (found) return found;
                        }
                    }
                    return null;
                }

                function findButton(root) {
                    // Look for send buttons
                    const buttons = root.querySelectorAll('button');
                    for (const btn of buttons) {
                        const text = (btn.textContent || '').toLowerCase();
                        const label = (btn.getAttribute('aria-label') || '').toLowerCase();
                        if (text.includes('send') || label.includes('send') ||
                            btn.querySelector('svg') && btn.closest('form')) {
                            return btn;
                        }
                    }
                    const elements = root.querySelectorAll('*');
                    for (const el of elements) {
                        if (el.shadowRoot) {
                            const found = findButton(el.shadowRoot);
                            if (found) return found;
                        }
                    }
                    return null;
                }

                const input = findInput(document);
                if (input) {
                    // Set value and dispatch events to trigger Lit's data binding
                    input.value = msg;
                    input.dispatchEvent(new Event('input', { bubbles: true }));
                    input.dispatchEvent(new Event('change', { bubbles: true }));

                    // Try to find and click the send button
                    setTimeout(() => {
                        const btn = findButton(document);
                        if (btn) {
                            btn.click();
                            console.log('[OpenClaw] Bootstrap message sent via button click');
                        } else {
                            // Try Enter key as fallback
                            input.dispatchEvent(new KeyboardEvent('keydown', {
                                key: 'Enter', code: 'Enter', keyCode: 13, bubbles: true
                            }));
                            console.log('[OpenClaw] Bootstrap message sent via Enter key');
                        }
                    }, 200);
                } else {
                    console.warn('[OpenClaw] Could not find chat input for bootstrap');
                }
            })();
            """;

            await _chatWebView.CoreWebView2.ExecuteScriptAsync(js);
            Logger.Info("[OnboardingChat] Bootstrap message injection executed");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[OnboardingChat] Bootstrap injection failed: {ex.Message}");
            // Not fatal — user can type manually
        }
    }

    /// <summary>
    /// Captures the current window content to a PNG file.
    /// Called automatically on page navigation when OPENCLAW_VISUAL_TEST=1.
    /// </summary>
    public async Task CaptureCurrentPageAsync()
    {
        if (_visualTestDir == null) return;
        try
        {
            await Task.Delay(300);

            var fileName = $"page-{_captureIndex:D2}.png";
            var filePath = Path.Combine(_visualTestDir, fileName);

            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(_rootGrid);
            var pixels = await rtb.GetPixelsAsync();
            var pixelBytes = pixels.ToArray();

            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)rtb.PixelWidth, (uint)rtb.PixelHeight,
                96, 96, pixelBytes);
            await encoder.FlushAsync();

            stream.Seek(0);
            var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);
            await File.WriteAllBytesAsync(filePath, bytes);

            Logger.Info($"[VisualTest] Captured {fileName} ({rtb.PixelWidth}x{rtb.PixelHeight})");
            _captureIndex++;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[VisualTest] Capture failed: {ex.Message}");
        }
    }


    private void OnOnboardingFinished(object? sender, EventArgs e)
    {
        _settings.Save();
        Completed = true;
        _state.GatewayClient = null;
        OnboardingCompleted?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_stateDisposed) return;
        _stateDisposed = true;
        _state.Finished -= OnOnboardingFinished;
        _state.RouteChanged -= OnRouteChanged;
        if (Completed)
        {
            _state.GatewayClient = null;
        }
        _state.Dispose();
    }

    /// <summary>
    /// SECURITY: Validate visual test directory path to prevent directory traversal.
    /// Returns null if the path is suspicious.
    /// </summary>
    private static string? ValidateTestDir(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (path.Contains('\0')) return null;

        try
        {
            var fullPath = Path.GetFullPath(path);
            // Ensure it doesn't escape via .. traversal to unexpected locations
            if (fullPath.Contains("..")) return null;
            return fullPath;
        }
        catch
        {
            return null;
        }
    }
}
