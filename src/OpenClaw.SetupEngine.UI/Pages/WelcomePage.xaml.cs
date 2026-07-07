using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI;
using OpenClaw.Shared;
using System.Numerics;

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
        StartMascotBreatheAnimation();
    }

    private void StartMascotBreatheAnimation()
    {
        var visual = ElementCompositionPreview.GetElementVisual(MascotHero);
        var compositor = visual.Compositor;
        var centerX = MascotHero.ActualWidth > 0 ? MascotHero.ActualWidth / 2 : MascotHero.Width / 2;
        var centerY = MascotHero.ActualHeight > 0 ? MascotHero.ActualHeight / 2 : MascotHero.Height / 2;
        visual.CenterPoint = new Vector3((float)centerX, (float)centerY, 0f);

        var pulse = compositor.CreateVector3KeyFrameAnimation();
        pulse.InsertKeyFrame(0f, new Vector3(1f, 1f, 1f));
        pulse.InsertKeyFrame(0.5f, new Vector3(1.025f, 1.025f, 1f));
        pulse.InsertKeyFrame(1f, new Vector3(1f, 1f, 1f));
        pulse.Duration = TimeSpan.FromMilliseconds(4200);
        pulse.IterationBehavior = AnimationIterationBehavior.Forever;

        visual.StartAnimation("Scale", pulse);
    }

    private void StartButton_Click(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => StartButtonClickAsync(GatewayInstallMode.NativeWindows),
            NullLogger.Instance,
            nameof(StartButton_Click));

    private void WslButton_Click(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => StartButtonClickAsync(GatewayInstallMode.Wsl),
            NullLogger.Instance,
            nameof(WslButton_Click));

    private Task StartButtonClickAsync(GatewayInstallMode installMode)
    {
        var config = _config ?? throw new InvalidOperationException("Setup configuration has not been loaded.");

        config.InstallMode = installMode;
        GatewayLkgVersion.ApplyToConfig(config);
        SetupWindow.Active?.NavigateToCapabilities();
        return Task.CompletedTask;
    }

    private void AdvancedSetup_Click(object sender, RoutedEventArgs e)
    {
        // Show quick connect instructions before handing off to the companion app.
        SetupWindow.Active?.NavigateToAdvancedSetup();
    }
}
