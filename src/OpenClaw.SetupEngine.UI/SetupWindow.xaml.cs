using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using OpenClaw.SetupEngine.UI.Pages;
using System.Runtime.InteropServices;

namespace OpenClaw.SetupEngine.UI;

public sealed partial class SetupWindow : Window
{
    private SetupConfig _config = null!;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public SetupWindow()
    {
        InitializeComponent();

        // Size window accounting for DPI
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(720 * scale), (int)(820 * scale)));

        // Extend into title bar for modern look
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDrag);

        // Mica backdrop
        SystemBackdrop = new MicaBackdrop();

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
        _config = SetupConfig.FromEnvironment(_config);

        RootFrame.Navigate(typeof(WelcomePage), _config);
    }

    public void NavigateToCapabilities() => RootFrame.Navigate(typeof(CapabilitiesPage), _config);
    public void NavigateToProgress() => RootFrame.Navigate(typeof(ProgressPage), _config);
    public void NavigateToPermissions() => RootFrame.Navigate(typeof(PermissionsPage), _config);
    public void NavigateToComplete(bool success, TimeSpan elapsed, string? logPath, string? errorMessage = null)
        => RootFrame.Navigate(typeof(CompletePage), new CompletePageArgs(success, elapsed, logPath, errorMessage));

    private static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}

public sealed record CompletePageArgs(bool Success, TimeSpan Elapsed, string? LogPath, string? ErrorMessage = null);
