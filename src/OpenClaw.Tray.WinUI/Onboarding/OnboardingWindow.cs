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
    private bool _useV2;
    private bool _v2BridgeBackPending;
    private OpenClawTray.Onboarding.V2.OnboardingV2State? _v2State;
    private OpenClawTray.Onboarding.V2.OnboardingV2Bridge? _v2Bridge;

    public event EventHandler? OnboardingCompleted;
    public bool Completed { get; private set; }

    private readonly SettingsManager _settings;
    private readonly FunctionalHostControl _host;
    private readonly string? _visualTestDir;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly string? _identityDataPath;
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
    // Single-fire guard so the X button (Closed) and the Finish button (state.Complete ->
    // OnOnboardingFinished -> Close -> Closed) don't both dispatch completion. Both paths
    // route through TryCompleteOnboarding which no-ops after the first call.
    private bool _completionDispatched;
    private bool _incompleteSetupDialogOpen;

    public OnboardingWindow(SettingsManager settings, string? identityDataPath = null)
    {
        _settings = settings;
        _identityDataPath = identityDataPath;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _visualTestDir = Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST") == "1"
            ? ValidateTestDir(Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST_DIR")
              ?? Path.Combine(Path.GetTempPath(), "openclaw-visual-test"))
            : null;

        // Optional override for visual tests: render the onboarding UI in a specific locale
        // (e.g. "fr-FR", "zh-CN") regardless of system language. Must be set BEFORE the first
        // LocalizationHelper.GetString call so the resource context picks it up.
        var testLocale = Environment.GetEnvironmentVariable("OPENCLAW_TEST_LOCALE");
        if (!string.IsNullOrWhiteSpace(testLocale))
        {
            LocalizationHelper.SetLanguageOverride(testLocale);
        }

        // V2 onboarding redesign: mount OnboardingV2App + a bridge to existing
        // services. Set OPENCLAW_USE_V2_SETUP=0 to force the legacy flow (kept
        // as the Advanced-setup fallback and as a kill-switch for one cycle).
        // The legacy fields below are still initialised so the legacy fallback
        // (open-on-Connection from Welcome's Advanced link) works without
        // re-running this constructor.
        _useV2 = Environment.GetEnvironmentVariable("OPENCLAW_USE_V2_SETUP") != "0"
              && Environment.GetEnvironmentVariable("OPENCLAW_ONBOARDING_START_ROUTE") != "Connection";

        Title = LocalizationHelper.GetString("Onboarding_Title");
        ExtendsContentIntoTitleBar = true;
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
        _state.Dismissed += OnOnboardingDismissed;

        // Construct the existing-config guard for returning users. The
        // <see cref="SetupWarningPage"/> uses this to render the warn-and-confirm
        // ("Replace my setup" / "Keep my setup") section when prior credentials,
        // a non-default gateway URL, or a completed setup-state file is present.
        //
        // We intentionally do NOT preselect SetupPath here. Leaving it null keeps
        // the global nav-bar Next button disabled on SetupWarning so the user MUST
        // pick one of the three explicit choices ("Replace my setup", "Keep my
        // setup", or "Advanced setup") — eliminating the accidental Next-into-setup
        // path Scott Hanselman hit when clicking "Keep my setup".
        if (identityDataPath != null)
        {
            _state.ExistingConfigGuard = new OnboardingExistingConfigGuard(settings, identityDataPath);
        }

        // Optional override for visual tests / engineering: jump straight to a route.
        // Accepts the OnboardingRoute enum name (e.g., "Connection").
        var startRoute = Environment.GetEnvironmentVariable("OPENCLAW_ONBOARDING_START_ROUTE");
        if (!string.IsNullOrWhiteSpace(startRoute) &&
            Enum.TryParse<OnboardingRoute>(startRoute, ignoreCase: true, out var parsed))
        {
            // Ensure SetupPath is consistent with the requested route so GetPageOrder
            // produces the expected step indicator. Defaults can be overridden below.
            if (parsed == OnboardingRoute.LocalSetupProgress) _state.SetupPath = SetupPath.Local;
            else if (parsed == OnboardingRoute.Connection) _state.SetupPath = SetupPath.Advanced;
            _state.CurrentRoute = parsed;
        }
        var startSetupPath = Environment.GetEnvironmentVariable("OPENCLAW_ONBOARDING_START_SETUP_PATH");
        if (!string.IsNullOrWhiteSpace(startSetupPath) &&
            Enum.TryParse<SetupPath>(startSetupPath, ignoreCase: true, out var parsedPath))
        {
            _state.SetupPath = parsedPath;
        }
        // Optional override for visual tests: pre-select a connection mode (Local/Wsl/Remote/Ssh/Later).
        var startMode = Environment.GetEnvironmentVariable("OPENCLAW_ONBOARDING_START_MODE");
        if (!string.IsNullOrWhiteSpace(startMode) &&
            Enum.TryParse<ConnectionMode>(startMode, ignoreCase: true, out var parsedMode))
        {
            _state.Mode = parsedMode;
        }

        _host = new FunctionalHostControl();
        if (_useV2)
        {
            // Mount the V2 onboarding component tree. The bridge below wires
            // engine + permission-checker + settings into the V2 state object
            // so the new UI renders against real data without touching any
            // service code.
            _v2State = new OpenClawTray.Onboarding.V2.OnboardingV2State();
            // Hand the legacy OnboardingState to V2 so the Gateway page can
            // embed the legacy WizardPage component (provider/model RPC
            // picker) inside the V2 chrome until that step is itself
            // redesigned. V2 sees this as opaque object?, only the host
            // (here) and the GatewayWelcome page know the concrete type.
            _v2State.LegacyState = _state;
            _v2State.GatewayWizardChildFactory = () =>
                Factories.Component<OpenClawTray.Onboarding.Pages.WizardPage, OnboardingState>(_state);

            // Mirror the legacy existing-config probe into V2 state so the V2
            // Welcome page can render the "replace existing setup?" warn-and-
            // confirm UI. The probe is synchronous and lightweight (reads
            // saved tokens / settings flags from disk + memory).
            if (_state.ExistingConfigGuard is { } guard)
            {
                var summary = guard.GetSummary();
                _v2State.ExistingConfig = new OpenClawTray.Onboarding.V2.OnboardingV2State.ExistingConfigSnapshot(
                    HasAny: summary.HasAny,
                    HasToken: summary.HasToken,
                    HasBootstrapToken: summary.HasBootstrapToken,
                    HasOperatorDeviceToken: summary.HasOperatorDeviceToken,
                    HasNodeDeviceToken: summary.HasNodeDeviceToken,
                    HasNonDefaultGatewayUrl: summary.HasNonDefaultGatewayUrl);
            }

            // Route V2Strings through the existing LocalizationHelper so V2
            // text comes from the same .resw resources as legacy strings.
            // Falls back to V2Strings.DefaultEnUs when a key is missing or
            // the resource resolver returns the key itself (treated as miss).
            OpenClawTray.Onboarding.V2.V2Strings.Resolver = LocalizationHelper.GetString;

            _host.Mount(ctx =>
            {
                var (s, _) = ctx.UseState(_v2State);
                return Factories.Component<
                    OpenClawTray.Onboarding.V2.OnboardingV2App,
                    OpenClawTray.Onboarding.V2.OnboardingV2State>(s);
            });

            CreateAndStartV2Bridge(settings);
        }
        else
        {
            _host.Mount(ctx =>
            {
                var (s, _) = ctx.UseState(_state);
                return Factories.Component<OnboardingApp, OnboardingState>(s);
            });
        }

        // Build the chat overlay (hidden by default)
        // Leave bottom 60px uncovered so the functional UI nav bar (Back/Next/dots) is visible and clickable
        _chatOverlay = BuildChatOverlay();
        _chatOverlay.Visibility = Visibility.Collapsed;
        _chatOverlay.VerticalAlignment = VerticalAlignment.Top;

        // Root grid: titlebar row + content area
        _rootGrid = new Grid
        {
            Background = GetThemeBrush("SolidBackgroundFillColorBaseBrush")
        };
        _rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        _rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Custom title bar — matches HubWindow treatment
        var titleBar = new Grid { Padding = new Thickness(16, 0, 140, 0) };
        var titleIcon = new TextBlock
        {
            Text = "🦞",
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        var titleText = new TextBlock
        {
            Text = LocalizationHelper.GetString("Onboarding_Title"),
            FontSize = 13,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center
        };
        var titleStack = new StackPanel { Orientation = Orientation.Horizontal };
        titleStack.Children.Add(titleIcon);
        titleStack.Children.Add(titleText);
        titleBar.Children.Add(titleStack);
        Grid.SetRow(titleBar, 0);
        _rootGrid.Children.Add(titleBar);
        SetTitleBar(titleBar);

        // Content area
        var contentGrid = new Grid();
        contentGrid.Children.Add(_host);
        contentGrid.Children.Add(_chatOverlay);
        Grid.SetRow(contentGrid, 1);
        _rootGrid.Children.Add(contentGrid);
        Content = _rootGrid;
        Closed += OnClosed;

        // Size the overlay after layout — leave space for the nav bar (~84px)
        // contentGrid is already in row 1 (below titlebar), so no need to subtract titlebar height
        contentGrid.SizeChanged += (_, args) =>
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
        var grid = new Grid
        {
            Background = GetThemeBrush("SolidBackgroundFillColorBaseBrush")
        };

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

    private static Brush GetThemeBrush(string resourceKey)
    {
        if (Application.Current?.Resources.TryGetValue(resourceKey, out var resource) == true &&
            resource is Brush brush)
        {
            return brush;
        }

        throw new InvalidOperationException($"Brush resource '{resourceKey}' was not found.");
    }

    /// <summary>
    /// Build a fresh <see cref="OpenClawTray.Onboarding.V2.OnboardingV2Bridge"/>
    /// against the current <see cref="_v2State"/>, wire its host-facing
    /// events, and start it. Idempotent: disposes a prior bridge first.
    /// Called from the initial V2 mount and from the
    /// <c>OnRouteChanged</c> bridge-back path so V2 always has a live
    /// bridge when it's the visible flow — without this, the
    /// Advanced -> V2 round-trip would leave V2 with no Finished /
    /// AutoStart-persist / Refresh / engine wiring.
    /// </summary>
    private void CreateAndStartV2Bridge(SettingsManager settings)
    {
        if (_v2State is null) return;

        try { _v2Bridge?.Dispose(); } catch { /* ignore */ }

        _v2Bridge = new OpenClawTray.Onboarding.V2.OnboardingV2Bridge(
            state: _v2State,
            settings: settings,
            dispatcher: _dispatcherQueue,
            engineFactory: replaceConfirmed =>
                ((App)Application.Current).CreateLocalGatewaySetupEngine(replaceConfirmed));
        _v2Bridge.AdvancedSetupRequested += (_, _) => OpenLegacyAdvancedSetup();
        _v2Bridge.Finished += (_, _) =>
        {
            if (TryCompleteOnboarding())
            {
                Close();
            }
        };
        _v2Bridge.Start();
    }

    private void OnRouteChanged(object? sender, OnboardingRoute route)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            // Advanced -> V2 round-trip: when we kicked the user out to the
            // legacy Connection page (Welcome's "Advanced setup" link), we
            // arm _v2BridgeBackPending. As soon as legacy navigates past
            // Connection, pull them back into V2 — but **skip the legacy
            // Wizard step** because Advanced users are pointing at an
            // already-configured gateway. Advanced is for "I have a
            // gateway, just connect me", not "I need to walk through model
            // setup again". So all of Wizard/Permissions/Ready land on
            // V2 Permissions next.
            if (_v2BridgeBackPending && _v2State is { } v2 && route != OnboardingRoute.Connection)
            {
                var v2Next = route switch
                {
                    OnboardingRoute.Wizard => OpenClawTray.Onboarding.V2.V2Route.Permissions,
                    OnboardingRoute.Permissions => OpenClawTray.Onboarding.V2.V2Route.Permissions,
                    OnboardingRoute.Ready => OpenClawTray.Onboarding.V2.V2Route.AllSet,
                    _ => OpenClawTray.Onboarding.V2.V2Route.Permissions,
                };
                _v2BridgeBackPending = false;
                _useV2 = true;
                v2.CurrentRoute = v2Next;
                _host.Mount(ctx =>
                {
                    var (s, _) = ctx.UseState(v2);
                    return Factories.Component<
                        OpenClawTray.Onboarding.V2.OnboardingV2App,
                        OpenClawTray.Onboarding.V2.OnboardingV2State>(s);
                });
                // Spin up a fresh bridge so the V2 tail (Permissions ->
                // AllSet) has live engine / settings / Finished wiring.
                // Without this, Finish on AllSet would no-op (the prior
                // bridge was disposed in OpenLegacyAdvancedSetup) and the
                // launch-at-startup toggle would not persist.
                CreateAndStartV2Bridge(_settings);
                _chatOverlay.Visibility = Visibility.Collapsed;
                return;
            }

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
            // Get token from GatewayRegistry — the source of truth for credentials.
            var app0 = (App)Microsoft.UI.Xaml.Application.Current;
            var token = app0.Registry?.GetActive()?.SharedGatewayToken ?? "";

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
                Content = LocalizationHelper.GetString("Onboarding_Retry"),
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
        if (TryCompleteOnboarding())
        {
            Close();
            return;
        }

        _ = ShowIncompleteSetupDialogAsync();
    }

    /// <summary>
    /// Set when the user explicitly dismisses the wizard via
    /// <see cref="OnboardingState.Dismiss"/> (e.g., "Keep my setup" on the
    /// SetupWarning page). <see cref="OnClosed"/> consults this to skip the
    /// completion pipeline so existing settings and gateway connection are
    /// preserved untouched.
    /// </summary>
    private bool _dismissedWithoutCompletion;

    private void OnOnboardingDismissed(object? sender, EventArgs e)
    {
        Logger.Info("[OnboardingWindow] Dismissed by user (keep-existing-setup) — closing without completing");
        _dismissedWithoutCompletion = true;
        try
        {
            Close();
        }
        catch (Exception ex)
        {
            // If Close() fails the window is still alive. Reset the guard so a
            // subsequent X-button or normal Finish path is NOT permanently
            // suppressed (otherwise TryCompleteOnboarding becomes unreachable
            // for the lifetime of this window).
            _dismissedWithoutCompletion = false;
            Logger.Warn($"[OnboardingWindow] Close after dismiss threw: {ex.Message}");
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // X button path: also runs TryCompleteOnboarding (idempotent via _completionDispatched)
        // so a user who clicks the title-bar X on the Ready page still gets the chat-window
        // launch when a model has been configured, matching the Finish-button behavior.
        //
        // Skipped entirely when the user explicitly dismissed via "Keep my setup" — that
        // path must NOT mark onboarding complete, must NOT fire OnboardingCompleted, and
        // must NOT touch settings/AutoStart so the prior gateway connection is preserved.
        if (!_dismissedWithoutCompletion)
        {
            _ = TryCompleteOnboarding();
        }

        try { _v2Bridge?.Dispose(); } catch { /* ignore */ }
        _v2Bridge = null;

        if (_stateDisposed) return;
        _stateDisposed = true;
        _state.Finished -= OnOnboardingFinished;
        _state.RouteChanged -= OnRouteChanged;
        _state.Dismissed -= OnOnboardingDismissed;
        if (Completed)
        {
            _state.GatewayClient = null;
        }
        _state.Dispose();
    }

    /// <summary>
    /// Called when V2 Welcome page's "Advanced setup" link fires. Tears down
    /// the V2 mount, swaps the host to the legacy <see cref="OnboardingApp"/>,
    /// jumps straight to the Connection page (Phase 6 of the legacy flow),
    /// and from there the user follows the existing Advanced flow to
    /// completion. The legacy <see cref="OnboardingState"/> remains valid (it
    /// was constructed up-front).
    /// </summary>
    private void OpenLegacyAdvancedSetup()
    {
        try { _v2Bridge?.Dispose(); } catch { /* ignore */ }
        _v2Bridge = null;
        // Keep _v2State alive so we can bridge back to V2 once the legacy
        // Connection page completes (RouteChanged fires past Connection).
        _useV2 = false;
        _v2BridgeBackPending = true;

        _state.SetupPath = SetupPath.Advanced;
        _state.CurrentRoute = OnboardingRoute.Connection;

        _host.Mount(ctx =>
        {
            var (s, _) = ctx.UseState(_state);
            return Factories.Component<OnboardingApp, OnboardingState>(s);
        });
    }

    /// <summary>
    /// Unified completion handler invoked from both the Finish button (via
    /// <see cref="OnOnboardingFinished"/>) and the title-bar X button (via
    /// <see cref="OnClosed"/>). Idempotent — guarded by <see cref="_completionDispatched"/>.
    ///
    /// If the user is closing from the Ready page and setup no longer requires
    /// credentials, launches the main tray hub window on the chat tab.
    /// This intentionally does not depend on WizardLifecycleState == "complete": the
    /// gateway wizard can stop on a later channel step even after credentials/model
    /// setup succeeded, but Finish on Ready still runs this handler.
    /// </summary>
    private bool TryCompleteOnboarding()
    {
        if (_completionDispatched) return true;
        // V2 path: AllSet replaces legacy Ready as the "finish was clicked from
        // the terminal page" gate.
        var finishedFromReady = _state.CurrentRoute == OnboardingRoute.Ready
            || (_useV2 && _v2State?.CurrentRoute == OpenClawTray.Onboarding.V2.V2Route.AllSet);
        var dataPath = _identityDataPath ?? SettingsManager.SettingsDirectoryPath;
        var setupStillRequired = StartupSetupState.RequiresSetup(_settings, dataPath);
        if (OnboardingCompletionPolicy.Decide(_state.CurrentRoute, setupStillRequired) == OnboardingCompletionOutcome.BlockIncompleteReady)
        {
            Logger.Warn("[OnboardingWindow] Finish blocked because setup is still required; keeping onboarding open");
            return false;
        }

        _completionDispatched = true;

        _settings.Save();
        Completed = true;
        _state.GatewayClient = null;

        // Materialize the persisted AutoStart preference into the OS-level Run-key.
        // ReadyPage applies the toggle on each change, but a user who never touches
        // it should still get the default (true) registered. Idempotent.
        try
        {
            AutoStartManager.SetAutoStart(_settings.AutoStart);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Onboarding] Failed to apply AutoStart={_settings.AutoStart}: {ex.Message}");
        }

        OnboardingCompleted?.Invoke(this, EventArgs.Empty);

        if (finishedFromReady && !setupStillRequired)
        {
            Logger.Info("[OnboardingWindow] TryCompleteOnboarding launching HubWindow on chat tab");
            ShowHubChatAfterWizardClose();
        }
        else
        {
            Logger.Info($"[OnboardingWindow] TryCompleteOnboarding skipping chat launch; route={_state.CurrentRoute}, setupStillRequired={setupStillRequired}");
        }

        return true;
    }

    private async Task ShowIncompleteSetupDialogAsync()
    {
        if (_incompleteSetupDialogOpen) return;
        _incompleteSetupDialogOpen = true;
        try
        {
            if (_rootGrid.XamlRoot is null)
            {
                Logger.Warn("[OnboardingWindow] Cannot show incomplete setup dialog; XamlRoot is unavailable");
                return;
            }

            var dialog = new ContentDialog
            {
                Title = LocalizationHelper.GetString("Onboarding_IncompleteSetup_Title"),
                Content = new TextBlock
                {
                    Text = LocalizationHelper.GetString("Onboarding_IncompleteSetup_Body"),
                    TextWrapping = TextWrapping.Wrap
                },
                CloseButtonText = LocalizationHelper.GetString("Onboarding_IncompleteSetup_Close"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = _rootGrid.XamlRoot
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[OnboardingWindow] Failed to show incomplete setup dialog: {ex.Message}");
        }
        finally
        {
            _incompleteSetupDialogOpen = false;
        }
    }

    private void ShowHubChatAfterWizardClose()
    {
        void ShowHubChat()
        {
            try
            {
                var app = Microsoft.UI.Xaml.Application.Current as App;
                if (app == null)
                {
                    Logger.Warn("[OnboardingWindow] ShowHub chat after Finish failed: App unavailable");
                    return;
                }

                app.ShowHub("chat");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[OnboardingWindow] ShowHub chat after Finish failed: {ex.Message}");
            }
        }

        if (!_dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ShowHubChat))
        {
            ShowHubChat();
        }
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
