using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.SetupEngine;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class AdvancedSetupPage : Page
{
    private SetupConfig? _config;

    public AdvancedSetupPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _config = e.Parameter as SetupConfig ?? new SetupConfig();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        SetupWindow.Active?.NavigateToWelcome(back: true);
    }

    private void OpenConnection_Click(object sender, RoutedEventArgs e)
    {
        // Hands off to the companion app's Connection page (and closes setup).
        SetupWindow.Active?.RequestAdvancedSetup();
    }
}
