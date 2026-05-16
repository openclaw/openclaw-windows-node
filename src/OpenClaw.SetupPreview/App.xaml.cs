using Microsoft.UI.Xaml;

namespace OpenClaw.SetupPreview;

/// <summary>
/// Minimal WinUI 3 Application bootstrap for the standalone V2 setup preview.
/// Constructs the single PreviewWindow and activates it. All capture-mode
/// behaviour (env-var gated headless rendering + PNG export + exit) lives
/// inside the window itself so this class stays trivially small.
/// </summary>
public partial class App : Application
{
    private PreviewWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new PreviewWindow();
        _window.Activate();
    }
}
