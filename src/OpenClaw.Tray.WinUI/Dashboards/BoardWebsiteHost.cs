using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using OpenClaw.Shared.Dashboards;
using OpenClawTray.Services;

namespace OpenClawTray.Dashboards;

/// <summary>
/// Hosts a <c>session:website</c> widget: a public HTTPS site in its own
/// in-private WebView2 profile, without Gateway credentials, host objects, or
/// device permissions. New windows open in the default browser.
/// </summary>
internal sealed class BoardWebsiteHost : Control
{
    private ContentPresenter? _contentRoot;
    private WebView2? _webView;
    private Uri? _url;
    private bool _isLoaded;
    private int _generation;
    private int _attempt;
    private bool _hasContent;

    public BoardWebsiteHost()
    {
        Template = (ControlTemplate)Application.Current.Resources["BoardWidgetWebHostTemplate"];
        IsTabStop = false;
        Loaded += (_, _) =>
        {
            _isLoaded = true;
            _ = StartAsync();
        };
        Unloaded += (_, _) =>
        {
            _isLoaded = false;
            Close();
        };
    }

    /// <summary>Reports load failures (true) and recoveries (false) for the native error overlay.</summary>
    public Action<bool>? FailedChanged { get; set; }

    /// <summary>Loads <paramref name="url"/>; a new <paramref name="attempt"/> restarts the WebView.</summary>
    public void Update(Uri url, int attempt = 0)
    {
        if (attempt != _attempt)
        {
            _attempt = attempt;
            _url = url;
            Close();
            _ = StartAsync();
            return;
        }
        if (_url == url)
            return;
        _url = url;
        _hasContent = false;
        if (_webView?.CoreWebView2 is { } core)
            Navigate(core, url);
        else
            _ = StartAsync();
    }

    private void Navigate(CoreWebView2 core, Uri url)
    {
        try
        {
            core.Navigate(url.AbsoluteUri);
        }
        catch (Exception ex)
        {
            // The browser process can exit between the null check and this call.
            Logger.Warn($"[Dashboards] website navigation failed: {ex.Message}");
            FailedChanged?.Invoke(true);
        }
    }

    private void OnProcessFailed(int generation, CoreWebView2ProcessFailedKind kind)
    {
        if (generation != _generation)
            return;
        switch (kind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                // A CoreWebView2 can't be reused after its browser process exits; Retry starts a fresh one.
                Close();
                FailedChanged?.Invoke(true);
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
            case CoreWebView2ProcessFailedKind.FrameRenderProcessExited:
                FailedChanged?.Invoke(true);
                break;
            // GPU, utility, and other helper process failures recover inside WebView2.
        }
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _contentRoot = GetTemplateChild("ContentRoot") as ContentPresenter;
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        if (!_isLoaded || _contentRoot is null || _url is null || _webView is not null)
            return;
        var generation = ++_generation;
        var webView = _webView = new WebView2 { DefaultBackgroundColor = Colors.Transparent };
        _contentRoot.Content = webView;
        CoreWebView2Environment environment;
        try
        {
            environment = await BoardWebViewEnvironment.GetAsync();
            // A profile per WebView: same-origin sites in two website widgets must not
            // share cookies, sign-in state, or storage. InPrivate keeps it off disk.
            await webView.EnsureCoreWebView2Async(
                environment,
                BoardWebViewEnvironment.CreateControllerOptions(environment, BoardWebViewEnvironment.NewWebsiteProfileName()));
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Dashboards] website WebView2 initialization failed: {ex.Message}");
            if (generation == _generation)
                FailedChanged?.Invoke(true);
            return;
        }
        if (generation != _generation || !ReferenceEquals(webView, _webView) || _url is null)
            return;
        var core = webView.CoreWebView2;
        BoardWebViewEnvironment.ApplyLockedDownSettings(core.Settings);
        BoardWebViewEnvironment.DenyAmbientCapabilities(core);
        // Every request, not just navigations: a public page must not reach
        // loopback or private-network services from the user's machine. The
        // source-kinds overload also covers cross-origin frames and workers.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, args) =>
        {
            if (Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var target) && BoardWidgetUrlPolicy.IsBlockedWebsiteRequest(target))
                args.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "Cache-Control: no-store");
        };
        core.NavigationStarting += (_, args) =>
        {
            // Fires for the first load, link clicks and every redirect hop.
            if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) || !BoardWidgetUrlPolicy.IsPublicHttpsUrl(target))
                args.Cancel = true;
        };
        core.FrameNavigationStarting += (_, args) =>
        {
            if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) ||
                (!BoardWidgetUrlPolicy.IsPublicHttpsUrl(target) && target.Scheme != "about"))
                args.Cancel = true;
        };
        core.NewWindowRequested += (sender, args) =>
        {
            args.Handled = true;
            if (args.IsUserInitiated && BoardWidgetUrlPolicy.IsExternalHttpLink(args.Uri, out var link))
                _ = global::Windows.System.Launcher.LaunchUriAsync(link);
        };
        core.NavigationCompleted += (_, args) =>
        {
            if (generation != _generation)
                return;
            if (args.IsSuccess)
            {
                _hasContent = true;
                FailedChanged?.Invoke(false);
            }
            // A link the policy cancels leaves the loaded page in place; only report it before the first load.
            else if (!_hasContent || args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
            {
                FailedChanged?.Invoke(true);
            }
        };
        core.ProcessFailed += (_, args) =>
        {
            var kind = args.ProcessFailedKind;
            DispatcherQueue.TryEnqueue(() => OnProcessFailed(generation, kind));
        };
        Navigate(core, _url);
    }

    private void Close()
    {
        _generation++;
        _hasContent = false;
        if (_webView is null)
            return;
        var webView = _webView;
        _webView = null;
        if (_contentRoot is not null && ReferenceEquals(_contentRoot.Content, webView))
            _contentRoot.Content = null;
        try
        {
            // The profile belongs to this WebView alone; deleting it also removes any leftover folder.
            webView.CoreWebView2?.Profile.Delete();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Dashboards] website profile delete failed: {ex.Message}");
        }
        try
        {
            webView.Close();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Dashboards] website WebView2 close failed: {ex.Message}");
        }
    }
}
