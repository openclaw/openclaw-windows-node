using OpenClawTray.Infrastructure.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace OpenClawTray.Infrastructure.Hosting;

/// <summary>
/// Helper methods for hosting Reactor components inside XAML Pages that participate
/// in Frame-based navigation.
///
/// IMPORTANT: WinUI's Frame.Navigate requires pages to have XAML metadata
/// (IXamlType registration). Code-only Page subclasses — including generic
/// base classes like ReactorPage&lt;T&gt; — crash with a null access violation
/// in ActivationAPI::ActivateInstance because GetXamlTypeNoRef() returns null.
///
/// The correct pattern for Reactor pages in Frame navigation:
///
///   1. Create a minimal .xaml file for the page:
///      &lt;Page x:Class="MyApp.Pages.ButtonPage" ... /&gt;
///
///   2. In the code-behind, use PageHelper to mount the component:
///
///      public sealed partial class ButtonPage : Page
///      {
///          private ReactorHostControl? _host;
///
///          public ButtonPage() { InitializeComponent(); }
///
///          protected override void OnNavigatedTo(NavigationEventArgs e)
///          {
///              base.OnNavigatedTo(e);
///              _host = PageHelper.Mount&lt;MyComponent&gt;(this, e);
///          }
///
///          protected override void OnNavigatedFrom(NavigationEventArgs e)
///          {
///              base.OnNavigatedFrom(e);
///              PageHelper.Unmount(ref _host);
///          }
///      }
/// </summary>
public static class PageHelper
{
    /// <summary>
    /// Creates a ReactorHostControl, mounts the component, and sets it as the page's content.
    /// Passes navigation parameters as props if the component accepts them.
    /// </summary>
    public static ReactorHostControl Mount<TComponent>(Page page, NavigationEventArgs e)
        where TComponent : Component, new()
    {
        var host = new ReactorHostControl();
        var component = new TComponent();

        if (e.Parameter is not null && component is IPropsReceiver receiver)
            receiver.SetProps(e.Parameter);

        host.Mount(component);
        page.Content = host;
        return host;
    }

    /// <summary>
    /// Creates a ReactorHostControl, mounts the component with typed props, and sets it as the page's content.
    /// </summary>
    public static ReactorHostControl Mount<TComponent, TProps>(Page page, NavigationEventArgs e)
        where TComponent : Component<TProps>, new()
    {
        var host = new ReactorHostControl();
        var component = new TComponent();

        if (e.Parameter is TProps props)
            component.Props = props;

        host.Mount(component);
        page.Content = host;
        return host;
    }

    /// <summary>
    /// Disposes the ReactorHostControl and clears the reference.
    /// </summary>
    public static void Unmount(ref ReactorHostControl? host)
    {
        host?.Dispose();
        host = null;
    }
}
