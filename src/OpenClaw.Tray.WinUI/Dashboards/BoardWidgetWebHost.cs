using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Web.WebView2.Core;
using OpenClaw.Shared.Dashboards;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using Windows.UI;

namespace OpenClawTray.Dashboards;

/// <summary>
/// Hosts one untrusted widget document in an isolated WebView2. The WebView
/// loads only the in-memory relay page (<see cref="BoardWidgetHostPage"/>),
/// which embeds the Gateway sandbox proxy; this owner fetches the ticketed
/// document natively and delivers it through the proxy handshake. Navigation,
/// popups, downloads, and device permissions are denied.
/// </summary>
internal sealed class BoardWidgetWebHost : Control
{
    private static readonly BoardWidgetDocumentLoader s_loader = new();
    private static readonly TimeSpan s_slowNotice = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_initialRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_maxRetryDelay = TimeSpan.FromSeconds(30);

    private ContentPresenter? _contentRoot;
    private Panel? _themeTokens;
    private WebView2? _webView;
    private Task? _webViewInit;
    private CoreWebView2Environment? _environment;
    private string? _relayUrl;
    private BoardWidgetWebHostRequest? _request;
    private string _documentKey = "";
    private int _generation;
    private bool _isLoaded;
    private Uri? _frameUrl;
    private Uri? _sandboxUrl;
    private bool _proxyReady;
    private string? _pendingHtml;
    private string? _renderId;
    private string _nonce = "";
    private CancellationTokenSource? _loadCancellation;
    private DispatcherQueueTimer? _slowTimer;
    private DispatcherQueueTimer? _deadlineTimer;
    // Frame and document retries back off independently so neither cancels the other.
    private DispatcherQueueTimer? _frameRetryTimer;
    private DispatcherQueueTimer? _documentRetryTimer;
    private TimeSpan _frameRetryDelay = s_initialRetryDelay;
    private TimeSpan _documentRetryDelay = s_initialRetryDelay;
    private int _frameAttempts;
    private int _crashRestarts;
    private BoardWidgetWebHostState? _state;

    public BoardWidgetWebHost()
    {
        Template = (ControlTemplate)Application.Current.Resources["BoardWidgetWebHostTemplate"];
        IsTabStop = false;
        Loaded += (_, _) =>
        {
            _isLoaded = true;
            _frameAttempts = 0;
            Restart();
        };
        Unloaded += (_, _) =>
        {
            _isLoaded = false;
            Teardown(closeWebView: true);
        };
        ActualThemeChanged += (_, _) => PostTheme();
    }

    public Action<BoardWidgetWebHostState>? StateChanged { get; set; }

    public Action<double>? ContentHeightChanged { get; set; }

    /// <summary>Idempotent: re-rendering with the same document keeps the loaded content.</summary>
    public void Update(BoardWidgetWebHostRequest request)
    {
        var previousFrameUrl = _request?.Widget.FrameUrl;
        var previousAttempt = _request?.Attempt ?? 0;
        var previousScale = _request?.Scale;
        _request = request;
        var key = request.DocumentKey;
        if (key == _documentKey)
        {
            // A preview resize only rescales the loaded frame; init picks up the latest scale otherwise.
            if (previousScale != request.Scale)
                PostJson(BoardWidgetHostPage.ScaleMessage(request.Scale));
            if (request.Attempt != previousAttempt)
            {
                Retry();
                return;
            }
            // A renewed ticket resumes a document read that its predecessor's ticket could not finish.
            if (_state?.Status is BoardWidgetWebHostStatus.Unauthorized &&
                !string.Equals(previousFrameUrl, request.Widget.FrameUrl, StringComparison.Ordinal))
                Restart();
            return;
        }
        _documentKey = key;
        _crashRestarts = 0;
        ResetBackoff();
        Restart();
    }

    public void Retry()
    {
        _crashRestarts = 0;
        ResetBackoff();
        Restart();
    }

    private void ResetBackoff()
    {
        _frameRetryDelay = s_initialRetryDelay;
        _documentRetryDelay = s_initialRetryDelay;
        _frameAttempts = 0;
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _contentRoot = GetTemplateChild("ContentRoot") as ContentPresenter;
        _themeTokens = GetTemplateChild("ThemeTokens") as Panel;
        if (_webView is not null && _contentRoot is not null)
            _contentRoot.Content = _webView;
        Restart();
    }

    private void Restart()
    {
        Teardown(closeWebView: false);
        if (!_isLoaded || _contentRoot is null || _request is null)
            return;
        _ = StartAsync(++_generation);
    }

    private async Task StartAsync(int generation)
    {
        var request = _request!;
        if (!BoardWidgetUrlPolicy.TryResolveFrameUrl(request.Widget.FrameUrl, request.GatewayOrigin, out var frameUrl))
        {
            Report(BoardWidgetWebHostStatus.Failed, LocalizationHelper.GetString("Dashboards_Widget_ContentUnavailable"));
            return;
        }
        if (!BoardWidgetUrlPolicy.TryResolveSandboxUrl(
                request.Widget.SandboxUrl,
                request.Widget.SandboxPort,
                request.Widget.SandboxOrigin,
                request.GatewayOrigin,
                BoardWidgetHostPage.PageUri,
                out var sandboxUrl))
        {
            Report(BoardWidgetWebHostStatus.Failed, LocalizationHelper.GetString("Dashboards_Widget_SandboxUnavailable"));
            return;
        }

        _frameUrl = frameUrl;
        // The proxy reports its own location, including this theme seed, as readiness proof.
        _sandboxUrl = new UriBuilder(sandboxUrl) { Fragment = "theme=" + (IsDark ? "dark" : "light") }.Uri;
        Report(BoardWidgetWebHostStatus.Loading);
        StartTimers(generation);
        LoadDocument(generation);

        try
        {
            await EnsureWebViewAsync();
        }
        catch (Exception ex)
        {
            if (generation == _generation)
            {
                Logger.Warn($"[Dashboards] widget WebView2 initialization failed: {ex.Message}");
                // Stop the deadline, retries, and document fetch so the error stays until Retry.
                Teardown(closeWebView: true);
                Report(BoardWidgetWebHostStatus.Failed, LocalizationHelper.GetString("Dashboards_Widget_WebViewUnavailable"));
            }
            return;
        }
        if (generation != _generation || _webView?.CoreWebView2 is not { } core)
            return;
        _nonce = BoardWidgetHostPage.NewNonce();
        // Only this page instance may talk to native code; a previous page's queued messages are ignored.
        _relayUrl = $"{BoardWidgetHostPage.PageUrl}?r={generation}";
        _frameAttempts++;
        PostTheme();
        try
        {
            core.Navigate(_relayUrl);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Dashboards] widget relay navigation failed: {ex.Message}");
            RetryFrame();
        }
    }

    private Task EnsureWebViewAsync()
    {
        if (_webViewInit is { IsFaulted: true } or { IsCanceled: true })
            CloseWebView();
        if (_webView is null)
        {
            _webView = new WebView2
            {
                // Transparent so widget content sits on the native card. WinUI's WebView2 currently
                // paints an opaque theme backplate behind web content regardless of this value, so
                // widgets show the window base color until the WinUI fix
                // (bkudiess/webview2-transparent-background) ships.
                DefaultBackgroundColor = Colors.Transparent,
                IsHitTestVisible = _request?.IsPreview != true,
            };
            if (_request?.IsPreview == true)
            {
                // Previews are inert: no keyboard focus and no input reaches widget script.
                _webView.IsTabStop = false;
                _webView.AllowFocusOnInteraction = false;
            }
            if (_contentRoot is not null)
                _contentRoot.Content = _webView;
        }
        // One initialization per control, so concurrent restarts never attach handlers twice.
        return _webViewInit ??= InitializeWebViewAsync(_webView);
    }

    private async Task InitializeWebViewAsync(WebView2 webView)
    {
        _environment = await BoardWebViewEnvironment.GetAsync();
        await webView.EnsureCoreWebView2Async(
            _environment,
            BoardWebViewEnvironment.CreateControllerOptions(_environment, BoardWebViewEnvironment.WidgetProfile));
        if (!ReferenceEquals(webView, _webView))
            return;
        var core = webView.CoreWebView2;
        BoardWebViewEnvironment.ApplyLockedDownSettings(core.Settings);
        BoardWebViewEnvironment.DenyAmbientCapabilities(core);
        core.NavigationStarting += (_, args) =>
        {
            if (!BoardWidgetHostPage.IsPageNavigation(args.Uri))
                args.Cancel = true;
        };
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.AddWebResourceRequestedFilter(BoardWidgetHostPage.Origin + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.WebMessageReceived += OnWebMessageReceived;
        core.ProcessFailed += (_, args) =>
        {
            var kind = args.ProcessFailedKind;
            DispatcherQueue.TryEnqueue(() => OnProcessFailed(webView, kind));
        };
    }

    private void OnProcessFailed(WebView2 webView, CoreWebView2ProcessFailedKind kind)
    {
        if (!ReferenceEquals(webView, _webView))
            return;
        switch (kind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                // The CoreWebView2 cannot be reused after its browser process exits; start a fresh control.
                CloseWebView();
                RetryFrame(crashed: true);
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
            case CoreWebView2ProcessFailedKind.FrameRenderProcessExited:
                RetryFrame(crashed: true);
                break;
            // GPU, utility, and other helper process failures recover inside WebView2.
        }
    }

    private void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (_environment is null)
            return;
        if (_relayUrl is null || !string.Equals(args.Request.Uri, _relayUrl, StringComparison.Ordinal) || _sandboxUrl is null)
        {
            args.Response = _environment.CreateWebResourceResponse(null, 404, "Not Found", "Cache-Control: no-store");
            return;
        }
        var html = Encoding.UTF8.GetBytes(BoardWidgetHostPage.BuildHtml(_nonce));
        var headers =
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Security-Policy: {BoardWidgetHostPage.ContentSecurityPolicy(_nonce, BoardWidgetUrlPolicy.OriginString(_sandboxUrl))}\r\n" +
            "Cache-Control: no-store\r\n" +
            "X-Content-Type-Options: nosniff";
        args.Response = _environment.CreateWebResourceResponse(
            new MemoryStream(html).AsRandomAccessStream(), 200, "OK", headers);
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        // Only the current relay page may talk to native code; frames cannot reach this channel,
        // and a replaced page's queued messages must not complete the new page's handshake.
        if (_relayUrl is null || !string.Equals(args.Source, _relayUrl, StringComparison.Ordinal))
            return;
        var message = BoardWidgetHostPage.Parse(args.WebMessageAsJson);
        switch (message)
        {
            case BoardWidgetHostMessage.PageReady when _sandboxUrl is not null && _request is not null:
                PostJson(BoardWidgetHostPage.InitMessage(_sandboxUrl, _request.Widget.Label, _request.Scale));
                break;
            case BoardWidgetHostMessage.ProxyReady ready when _sandboxUrl is not null &&
                string.Equals(ready.SandboxUrl, _sandboxUrl.AbsoluteUri, StringComparison.Ordinal):
                _proxyReady = true;
                // The sandbox answered, so earlier pages (ticket renewals, slow starts) don't count against it.
                _frameAttempts = 0;
                PostTheme();
                DeliverDocument();
                break;
            case BoardWidgetHostMessage.ResourceLoaded loaded when loaded.RenderId == _renderId:
                _renderId = null;
                StopTimers();
                // A recovered frame must not be replaced by a retry scheduled before it rendered.
                _frameRetryTimer?.Stop();
                _frameRetryTimer = null;
                ResetBackoff();
                // The proxy replaces its inner frame per document, so the theme posted
                // at proxy-ready never reached this one (upstream posts on render too).
                PostTheme();
                Report(BoardWidgetWebHostStatus.Ready);
                break;
            case BoardWidgetHostMessage.ContentSize size:
                ContentHeightChanged?.Invoke(size.Height);
                break;
            case BoardWidgetHostMessage.FrameError:
                RetryFrame();
                break;
        }
    }

    private void LoadDocument(int generation)
    {
        var frameUrl = _frameUrl!;
        _loadCancellation?.Cancel();
        var cancellation = _loadCancellation = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                var html = await s_loader.LoadAsync(frameUrl, cancellation.Token).ConfigureAwait(false);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (generation != _generation || cancellation.IsCancellationRequested)
                        return;
                    _pendingHtml = html;
                    DeliverDocument();
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (BoardWidgetDocumentException ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (generation != _generation || cancellation.IsCancellationRequested)
                        return;
                    switch (ex.Failure)
                    {
                        case BoardWidgetDocumentFailure.Unauthorized:
                            StopTimers();
                            Report(BoardWidgetWebHostStatus.Unauthorized, ex.Message);
                            break;
                        case BoardWidgetDocumentFailure.Unavailable:
                            Report(BoardWidgetWebHostStatus.Slow, ex.Message);
                            ScheduleDocumentRetry(() => LoadDocument(generation));
                            break;
                        default:
                            StopTimers();
                            Report(BoardWidgetWebHostStatus.Failed, ex.Message);
                            break;
                    }
                });
            }
        });
    }

    private void DeliverDocument()
    {
        if (!_proxyReady || _pendingHtml is null)
            return;
        _renderId = Guid.NewGuid().ToString("N");
        var html = _pendingHtml;
        _pendingHtml = null;
        PostJson(BoardWidgetHostPage.ResourceReadyMessage(html, _renderId));
    }

    private void StartTimers(int generation)
    {
        _slowTimer = DispatcherQueue.CreateTimer();
        _slowTimer.Interval = s_slowNotice;
        _slowTimer.IsRepeating = false;
        _slowTimer.Tick += (_, _) =>
        {
            if (generation == _generation && _state?.Status == BoardWidgetWebHostStatus.Loading)
                Report(BoardWidgetWebHostStatus.Slow);
        };
        _slowTimer.Start();

        _deadlineTimer = DispatcherQueue.CreateTimer();
        _deadlineTimer.Interval = BoardWidgetDocumentLoader.DefaultTimeout;
        _deadlineTimer.IsRepeating = false;
        _deadlineTimer.Tick += (_, _) =>
        {
            if (generation != _generation)
                return;
            if (!_proxyReady)
                RetryFrame();
            else if (_renderId is not null)
                Report(BoardWidgetWebHostStatus.Failed, LocalizationHelper.GetString("Dashboards_Widget_RenderTimeout"));
        };
        _deadlineTimer.Start();
    }

    /// <summary>
    /// A sandbox frame that never became ready gets a fresh relay page after
    /// backoff. After <see cref="BoardWidgetRetryPolicy.MaxFrameAttempts"/> pages the sandbox is
    /// treated as unreachable (for example a derived sandbox port that a
    /// proxy or tunnel doesn't forward) and the widget shows a terminal error.
    /// </summary>
    private void RetryFrame(bool crashed = false)
    {
        // One failing page can report several failures (process exit, frame error, deadline); retry it once.
        if (!_isLoaded || _request is null || _frameRetryTimer is not null)
            return;
        if (crashed)
        {
            _crashRestarts++;
            // A process failure is not evidence that the remote sandbox is unreachable.
            _frameAttempts = 0;
        }
        if (BoardWidgetRetryPolicy.TerminalStatus(crashed, _frameAttempts, _crashRestarts) is { } terminal)
        {
            Teardown(closeWebView: true);
            Report(terminal, terminal == BoardWidgetWebHostStatus.Failed
                ? LocalizationHelper.GetString("Dashboards_Widget_StoppedResponding")
                : null);
            return;
        }
        _frameRetryTimer = Schedule(_frameRetryTimer, _frameRetryDelay, Restart);
        _frameRetryDelay = NextDelay(_frameRetryDelay);
    }

    private void ScheduleDocumentRetry(Action retry)
    {
        _documentRetryTimer = Schedule(_documentRetryTimer, _documentRetryDelay, retry);
        _documentRetryDelay = NextDelay(_documentRetryDelay);
    }

    private DispatcherQueueTimer Schedule(DispatcherQueueTimer? previous, TimeSpan delay, Action retry)
    {
        previous?.Stop();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = delay;
        timer.IsRepeating = false;
        var generation = _generation;
        timer.Tick += (_, _) =>
        {
            if (generation == _generation && _isLoaded)
                retry();
        };
        timer.Start();
        return timer;
    }

    private static TimeSpan NextDelay(TimeSpan delay) =>
        TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, s_maxRetryDelay.Ticks));

    private void PostTheme()
    {
        if (_webView?.CoreWebView2 is not { } core)
            return;
        // The relay page and the sandbox frame must share one used color scheme, or
        // Chromium paints an opaque canvas behind the transparent widget document.
        core.Profile.PreferredColorScheme = IsDark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
        if (!_proxyReady)
            return;
        PostJson(BoardWidgetHostPage.ThemeMessage(IsDark, ReadThemeTokens()));
    }

    private void PostJson(string json)
    {
        try
        {
            _webView?.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Dashboards] widget relay post failed: {ex.Message}");
        }
    }

    private bool IsDark => ActualTheme == ElementTheme.Dark;

    /// <summary>CSS values for the upstream widget theme tokens, read from ThemeResource-bound template fills.</summary>
    private Dictionary<string, string> ReadThemeTokens()
    {
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_themeTokens is not null)
        {
            foreach (var child in _themeTokens.Children)
            {
                if (child is Rectangle { Tag: string token, Fill: SolidColorBrush brush })
                    tokens[token] = Css(brush.Color, brush.Opacity);
            }
        }
        if (Application.Current.Resources.TryGetValue("OverlayCornerRadius", out var radius) && radius is CornerRadius corner)
            tokens["radius"] = $"{corner.TopLeft:0}px";
        tokens["radius-full"] = "9999px";
        if (Application.Current.Resources.TryGetValue("ContentControlThemeFontFamily", out var font) && font is FontFamily family)
            tokens["font-body"] = $"\"{family.Source}\", \"Segoe UI\", sans-serif";
        tokens["font-mono"] = "\"Cascadia Mono\", Consolas, monospace";
        return tokens;
    }

    private static string Css(Color color, double opacity)
    {
        var alpha = color.A / 255d * opacity;
        return $"rgba({color.R},{color.G},{color.B},{alpha.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)})";
    }

    private void Report(BoardWidgetWebHostStatus status, string? detail = null)
    {
        var state = new BoardWidgetWebHostState(status, detail);
        if (state == _state)
            return;
        _state = state;
        StateChanged?.Invoke(state);
    }

    private void StopTimers()
    {
        _slowTimer?.Stop();
        _deadlineTimer?.Stop();
        _slowTimer = null;
        _deadlineTimer = null;
    }

    private void Teardown(bool closeWebView)
    {
        _generation++;
        _relayUrl = null;
        _loadCancellation?.Cancel();
        _loadCancellation = null;
        StopTimers();
        _frameRetryTimer?.Stop();
        _frameRetryTimer = null;
        _documentRetryTimer?.Stop();
        _documentRetryTimer = null;
        _proxyReady = false;
        _pendingHtml = null;
        _renderId = null;
        _state = null;
        if (closeWebView)
            CloseWebView();
    }

    private void CloseWebView()
    {
        _webViewInit = null;
        if (_webView is null)
            return;
        var webView = _webView;
        _webView = null;
        if (_contentRoot is not null && ReferenceEquals(_contentRoot.Content, webView))
            _contentRoot.Content = null;
        try
        {
            webView.Close();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Dashboards] widget WebView2 close failed: {ex.Message}");
        }
    }
}
