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
    private const string InstallButtonText = "Install a local gateway (WSL)";
    private const string CheckingButtonText = "Checking existing setup...";
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
            StartButtonClickAsync,
            NullLogger.Instance,
            nameof(StartButton_Click));

    private async Task StartButtonClickAsync()
    {
        var config = _config ?? throw new InvalidOperationException("Setup configuration has not been loaded.");
        var dataDir = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenClawTray");
        var setupWindow = SetupWindow.Active;

        InstallButton.IsEnabled = false;
        InstallTitle.Text = CheckingButtonText;
        var navigating = false;
        try
        {
            var existing = await Task.Run(() => ExistingConfigDetector.Detect(dataDir, config.DistroName));
            var xamlRoot = XamlRoot;
            if (setupWindow is null or { IsClosed: true } || xamlRoot is null)
                return;

            var summary = ExistingConfigDetector.BuildReplacementSummary(existing);

            var dialog = new ContentDialog
            {
                Title = existing.HasLocalGateway || existing.HasDistro
                    ? "Replace existing WSL gateway?"
                    : "Install a new WSL gateway?",
                Content = summary,
                PrimaryButtonText = "Continue",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot,
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return;

            navigating = true;
            setupWindow.NavigateToCapabilities();
        }
        finally
        {
            if (!navigating && setupWindow is { IsClosed: false })
            {
                InstallTitle.Text = InstallButtonText;
                InstallButton.IsEnabled = true;
            }
        }
    }

    private void AdvancedSetup_Click(object sender, RoutedEventArgs e)
    {
        // Show quick connect instructions before handing off to the companion app.
        SetupWindow.Active?.NavigateToAdvancedSetup();
    }
}
