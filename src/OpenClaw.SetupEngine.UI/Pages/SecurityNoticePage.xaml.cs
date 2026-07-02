using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.SetupEngine;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class SecurityNoticePage : Page
{
    private SetupConfig? _config;

    public SecurityNoticePage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _config = e.Parameter as SetupConfig ?? new SetupConfig();
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        SetupWindow.Active?.NavigateToWelcome();
    }
}
