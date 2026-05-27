using Microsoft.UI.Xaml;

namespace OpenClaw.SetupEngine.UI;

public partial class App : Application
{
    public static SetupWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new SetupWindow();
        MainWindow.BringToFrontForSetupLaunch();
    }
}
