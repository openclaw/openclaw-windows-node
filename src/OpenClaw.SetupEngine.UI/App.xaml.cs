using Microsoft.UI.Xaml;

namespace OpenClaw.SetupEngine.UI;

public partial class App : Application
{
    public static SetupWindow? MainWindow { get; private set; }

    public App()
    {
        Program.WriteStartupBreadcrumb("App.ctor.begin");
        InitializeComponent();
        Program.WriteStartupBreadcrumb("App.ctor.afterInitializeComponent");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Program.WriteStartupBreadcrumb("App.OnLaunched.begin");
        MainWindow = new SetupWindow();
        Program.WriteStartupBreadcrumb("App.OnLaunched.afterSetupWindow");
        MainWindow.BringToFrontForSetupLaunch();
        Program.WriteStartupBreadcrumb("App.OnLaunched.afterBringToFront");
    }
}
