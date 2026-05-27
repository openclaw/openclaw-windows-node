using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.SetupEngine.UI.Pages;
using System.Runtime.InteropServices;

namespace OpenClaw.SetupEngine.UI;

public sealed partial class SetupWindow : Window
{
    private SetupConfig _config = null!;
    private SetupRunLock? _setupLock;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public SetupWindow()
    {
        Program.WriteStartupBreadcrumb("SetupWindow.ctor.begin");
        BuildWindowShell();
        Program.WriteStartupBreadcrumb("SetupWindow.ctor.afterBuildWindowShell");

        // Size window accounting for DPI
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Program.WriteStartupBreadcrumb("SetupWindow.ctor.afterWindowHandle");
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(720 * scale), (int)(820 * scale)));
        Program.WriteStartupBreadcrumb("SetupWindow.ctor.afterResize");

        // Extend into title bar for modern look
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDrag);

        // Mica backdrop
        SystemBackdrop = new MicaBackdrop();
        Program.WriteStartupBreadcrumb("SetupWindow.ctor.afterBackdrop");

        // Load config: explicit --config arg, or bundled default-config.json (required)
        var args = Environment.GetCommandLineArgs();
        var configPath = GetArg(args, "--config");
        if (configPath == null)
        {
            var defaultPath = Path.Combine(AppContext.BaseDirectory, "default-config.json");
            if (File.Exists(defaultPath))
                configPath = defaultPath;
        }

        if (configPath == null || !File.Exists(configPath))
        {
            // Cannot run without config
            Console.Error.WriteLine("ERROR: No config file found. Place default-config.json next to the exe or pass --config <path>.");
            Environment.Exit(1);
            return;
        }

        _config = SetupConfig.LoadFromFile(configPath);
        Program.WriteStartupBreadcrumb("SetupWindow.ctor.afterLoadConfig");
        _config = SetupConfig.FromEnvironment(_config);
        _config.ApplyUiDefaults(rollbackOnFailure: !HasFlag(args, "--no-rollback-on-failure"));
        Program.WriteStartupBreadcrumb("SetupWindow.ctor.afterApplyConfig");

        Closed += (_, _) =>
        {
            _setupLock?.Dispose();
            _setupLock = null;
        };

        if (!SetupRunLock.TryAcquire(SetupContext.ResolveDataDir(), out _setupLock, out var lockMessage))
        {
            RootFrame.Navigate(typeof(CompletePage), new CompletePageArgs(false, TimeSpan.Zero, null, lockMessage ?? "Another setup run is active."));
            return;
        }

        RootFrame.Navigate(typeof(WelcomePage), _config);
        Program.WriteStartupBreadcrumb("SetupWindow.ctor.afterWelcomeNavigate");
    }

    private void BuildWindowShell()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        TitleBarDrag = new Grid
        {
            Padding = new Thickness(12, 0, 0, 0)
        };
        Grid.SetRow(TitleBarDrag, 0);

        var titleContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleContent.Children.Add(new TextBlock
        {
            Text = "OpenClaw Setup",
            FontSize = 13,
            Opacity = 0.85,
            VerticalAlignment = VerticalAlignment.Center
        });
        TitleBarDrag.Children.Add(titleContent);

        RootFrame = new Frame();
        Grid.SetRow(RootFrame, 1);

        root.Children.Add(TitleBarDrag);
        root.Children.Add(RootFrame);
        Content = root;
    }

    public void NavigateToCapabilities() => RootFrame.Navigate(typeof(CapabilitiesPage), _config);
    public void NavigateToProgress() => RootFrame.Navigate(typeof(ProgressPage), _config);
    public void NavigateToWizard() => RootFrame.Navigate(typeof(WizardPage), _config);
    public void NavigateToPermissions() => RootFrame.Navigate(typeof(PermissionsPage), _config);
    public void NavigateToComplete(bool success, TimeSpan elapsed, string? logPath, string? errorMessage = null)
        => RootFrame.Navigate(typeof(CompletePage), new CompletePageArgs(success, elapsed, logPath, errorMessage));

    public void BringToFrontForSetupLaunch()
    {
        Activate();

        if (AppWindow.Presenter is not OverlappedPresenter presenter)
            return;

        if (presenter.State == OverlappedPresenterState.Minimized)
            presenter.Restore();

        var wasAlwaysOnTop = presenter.IsAlwaysOnTop;
        presenter.IsAlwaysOnTop = true;
        Activate();

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(750);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!wasAlwaysOnTop && AppWindow.Presenter is OverlappedPresenter p)
                p.IsAlwaysOnTop = false;
        };
        timer.Start();
    }

    private static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static bool HasFlag(string[] args, string name)
        => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
}

public sealed record CompletePageArgs(bool Success, TimeSpan Elapsed, string? LogPath, string? ErrorMessage = null);
