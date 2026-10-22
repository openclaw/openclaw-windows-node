using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class AdvancedSetupPage : Page
{
    public AdvancedSetupPage() => InitializeComponent();
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        HostUnavailable.IsOpen = e.Parameter is true;
        ClassicSettingsLink.Visibility = SetupWindow.Active?.HasClassicSettingsHost == true
            ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Back_Click(object sender, RoutedEventArgs e) => SetupWindow.Active?.NavigateToWelcome(back: true);
    private void Existing_Click(object sender, RoutedEventArgs e) => RequestConnection(SetupGatewayRoute.Existing);
    private void Remote_Click(object sender, RoutedEventArgs e) => RequestConnection(SetupGatewayRoute.Remote);
    private void RequestConnection(SetupGatewayRoute route)
    {
        var window = SetupWindow.Active;
        if (window is null) return;
        window.SelectGatewayRoute(route);
        if (window.HasNativeConnectionHost)
            window.NavigateToNativeConnection(route);
        else
            HostUnavailable.IsOpen = true;
    }
    private void ClassicSettings_Click(object sender, RoutedEventArgs e)
    {
        if (SetupWindow.Active?.RequestAdvancedSetup() != true)
            HostUnavailable.IsOpen = true;
    }
    private void Mcp_Click(object sender, RoutedEventArgs e)
    {
        var window = SetupWindow.Active;
        if (window is null) return;
        window.SelectGatewayRoute(SetupGatewayRoute.McpOnly);
        window.AccessDraft.SetNodeMode(false);
        window.AccessDraft.SetMcpServer(true);
        window.NavigateToCapabilities();
    }
    private void Deferred_Click(object sender, RoutedEventArgs e)
    {
        SetupWindow.Active?.SelectGatewayRoute(SetupGatewayRoute.Deferred);
        SetupWindow.Active?.NavigateToCapabilities();
    }
}
