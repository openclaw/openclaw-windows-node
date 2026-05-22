using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class WelcomePage : Page
{
    private SetupConfig? _config;

    public WelcomePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _config = e.Parameter as SetupConfig ?? new SetupConfig();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var isDark = ActualTheme == ElementTheme.Dark;
        InfoCard.Background = new SolidColorBrush(isDark
            ? Color.FromArgb(255, 0x2C, 0x2C, 0x2C)
            : Color.FromArgb(255, 0xF0, 0xF0, 0xF0));

        InfoText.Text = "This local setup installs a small WSL Linux instance dedicated to OpenClaw. "
                      + $"Distro: {_config!.DistroName} • Port: {_config.GatewayPort}";
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        App.MainWindow?.NavigateToCapabilities();
    }
}
