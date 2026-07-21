using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    private bool _installSelected = true; // default selection

    public WelcomePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        UpdateCardSelection();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _config = e.Parameter as SetupConfig ?? new SetupConfig();
        _installSelected = SetupWindow.Active?.IsWelcomeInstallSelected ?? true;
        UpdateCardSelection();
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

    private void InstallCard_Pressed(object sender, PointerRoutedEventArgs e)
    {
        SetInstallSelected(true);
    }

    private void ConnectCard_Pressed(object sender, PointerRoutedEventArgs e)
    {
        SetInstallSelected(false);
    }

    private void SetInstallSelected(bool installSelected)
    {
        _installSelected = installSelected;
        SetupWindow.Active?.SetWelcomeInstallSelected(installSelected);
        UpdateCardSelection();
    }

    private void UpdateCardSelection()
    {
        InstallCard.BorderBrush = _installSelected
            ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
            : (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        InstallCard.BorderThickness = new Thickness(_installSelected ? 2 : 1);

        ConnectCard.BorderBrush = !_installSelected
            ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
            : (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        ConnectCard.BorderThickness = new Thickness(!_installSelected ? 2 : 1);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        SetupWindow.Active?.NavigateToSecurityNotice(back: true);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_installSelected)
        {
            AsyncEventHandlerGuard.Run(
                StartInstallAsync,
                NullLogger.Instance,
                nameof(Next_Click));
        }
        else
        {
            SetupWindow.Active?.NavigateToAdvancedSetup();
        }
    }

    private async Task StartInstallAsync()
    {
        var config = _config ?? throw new InvalidOperationException("Setup configuration has not been loaded.");
        var setupWindow = SetupWindow.Active;
        var dataDir = setupWindow?.DataDir ?? SetupContext.ResolveDataDir();

        NextButton.IsEnabled = false;
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
                NextButton.IsEnabled = true;
            }
        }
    }
}
