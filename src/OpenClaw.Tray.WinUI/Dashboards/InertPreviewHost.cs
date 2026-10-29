using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Dashboards;

/// <summary>
/// Hosts a gallery preview as an inert, presentation-only subtree: it takes
/// no input and exposes no UI Automation children, matching the upstream
/// preview's <c>aria-hidden</c> and <c>inert</c>. The card button names the
/// dashboard for assistive technology instead.
/// </summary>
internal sealed class InertPreviewHost : Control
{
    private ContentPresenter? _contentRoot;
    private ReactorHostControl? _host;
    private DashboardPreviewProps? _props;
    private bool _isLoaded;

    public InertPreviewHost()
    {
        Template = (ControlTemplate)Application.Current.Resources["BoardWidgetWebHostTemplate"];
        IsTabStop = false;
        IsHitTestVisible = false;
        // Disabling propagates to every descendant control (links, scrollers, widget
        // WebViews), so no preview content can take keyboard focus or input.
        IsEnabled = false;
        Loaded += (_, _) =>
        {
            _isLoaded = true;
            Mount();
        };
        Unloaded += (_, _) =>
        {
            _isLoaded = false;
            Release();
        };
    }

    public void Update(DashboardPreviewProps props)
    {
        if (props == _props)
            return;
        _props = props;
        Release();
        Mount();
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _contentRoot = GetTemplateChild("ContentRoot") as ContentPresenter;
        Mount();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new InertPeer(this);

    private void Mount()
    {
        if (!_isLoaded || _contentRoot is null || _props is not { } props || _host is not null)
            return;
        _host = new ReactorHostControl();
        _host.Mount(_ => Component<ReactorDashboardPreview, DashboardPreviewProps>(props));
        _contentRoot.Content = _host;
    }

    private void Release()
    {
        if (_host is null)
            return;
        if (_contentRoot is not null && ReferenceEquals(_contentRoot.Content, _host))
            _contentRoot.Content = null;
        _host.Dispose();
        _host = null;
    }

    private sealed class InertPeer(FrameworkElement owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override IList<AutomationPeer>? GetChildrenCore() => null;
        protected override bool IsControlElementCore() => false;
        protected override bool IsContentElementCore() => false;
    }
}
