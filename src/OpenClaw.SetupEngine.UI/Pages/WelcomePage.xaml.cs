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
    private const string CheckingButtonText = "Checking existing setup...";
    private SetupConfig? _config;
    private bool _installSelected = true; // default selection
    private GatewayInstallMode _installMode = GatewayInstallMode.Wsl;
    private bool _suppressSelectionWrite;

    public WelcomePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _config = e.Parameter as SetupConfig ?? new SetupConfig();
        _installSelected = SetupWindow.Active?.IsWelcomeInstallSelected ?? true;
        _installMode = SetupWindow.Active?.WelcomeInstallMode ?? _config.InstallMode;
        _suppressSelectionWrite = true;
        try
        {
            GatewayChoiceSelector.SelectedIndex = !_installSelected
                ? 2
                : _installMode == GatewayInstallMode.NativeWindows ? 1 : 0;
        }
        finally
        {
            _suppressSelectionWrite = false;
        }
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

    private void GatewayChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // A single-select ListView can be cleared to no selection (Ctrl+click / automation).
        // The Welcome choice must always have exactly one option selected, so restore the last
        // known selection instead of leaving the persisted value stale behind an empty list.
        if (GatewayChoiceSelector.SelectedIndex is not (0 or 1 or 2))
        {
            _suppressSelectionWrite = true;
            try
            {
                GatewayChoiceSelector.SelectedIndex = !_installSelected
                    ? 2
                    : _installMode == GatewayInstallMode.NativeWindows ? 1 : 0;
            }
            finally
            {
                _suppressSelectionWrite = false;
            }

            return;
        }

        if (!_suppressSelectionWrite)
            SetSelection(GatewayChoiceSelector.SelectedIndex);
    }

    private void SetSelection(int selectedIndex)
    {
        _installSelected = selectedIndex is 0 or 1;
        _installMode = selectedIndex == 1
            ? GatewayInstallMode.NativeWindows
            : GatewayInstallMode.Wsl;
        SetupWindow.Active?.SetWelcomeInstallSelected(_installSelected);
        SetupWindow.Active?.SetWelcomeInstallMode(_installMode);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        SetupWindow.Active?.NavigateToSecurityNotice(back: true);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (!_installSelected)
        {
            SetupWindow.Active?.NavigateToAdvancedSetup();
            return;
        }

        AsyncEventHandlerGuard.Run(
            StartInstallWithConfirmationAsync,
            NullLogger.Instance,
            nameof(Next_Click));
    }

    private Task StartInstallAsync(GatewayInstallMode installMode)
    {
        var config = _config ?? throw new InvalidOperationException("Setup configuration has not been loaded.");

        config.ApplyInstallMode(installMode);
        GatewayLkgVersion.ApplyToConfig(config);
        SetupWindow.Active?.NavigateToCapabilities();
        return Task.CompletedTask;
    }

    private async Task StartInstallWithConfirmationAsync()
    {
        var config = _config ?? throw new InvalidOperationException("Setup configuration has not been loaded.");
        var setupWindow = SetupWindow.Active;
        var dataDir = setupWindow?.DataDir ?? SetupContext.ResolveDataDir();
        var localDataDir = setupWindow?.LocalDataDir ?? SetupContext.ResolveLocalDataDir();
        config.InstallMode = _installMode;

        NextButton.IsEnabled = false;
        NextButton.Content = CheckingButtonText;
        var navigating = false;
        try
        {
            var existing = await Task.Run(() => ExistingConfigDetector.Detect(dataDir, localDataDir, config));
            var xamlRoot = XamlRoot;
            if (setupWindow is null or { IsClosed: true } || xamlRoot is null)
                return;

            var summary = ExistingConfigDetector.BuildReplacementSummary(existing, _installMode);
            var dialog = new ContentDialog
            {
                Title = ExistingConfigDetector.BuildReplacementTitle(existing, _installMode),
                Content = summary,
                PrimaryButtonText = "Continue",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            navigating = true;
            await StartInstallAsync(_installMode);
        }
        finally
        {
            if (!navigating && setupWindow is { IsClosed: false })
            {
                NextButton.Content = "Next";
                NextButton.IsEnabled = true;
            }
        }
    }

    private void AdvancedSetup_Click(object sender, RoutedEventArgs e)
    {
        // Show quick connect instructions before handing off to the companion app.
        SetupWindow.Active?.NavigateToAdvancedSetup();
    }
}
