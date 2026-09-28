using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI;
using OpenClaw.Shared;
using OpenClaw.Shared.Inference.Catalog;
using System.Numerics;
using System.Diagnostics;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class WelcomePage : Page
{
    private SetupConfig? _config;
    private GatewaySetupChoice? _selectedChoice;
    private NativeGatewayEligibility? _nativeEligibility;
    private int _probeGeneration;
    private bool _installInProgress;
    private bool _suppressSelectionWrite;
    private string? _installChoiceBaseAutomationName;

    public WelcomePage()
    {
        InitializeComponent();
        AutomationProperties.SetName(NativeChoice,
            SetupLocalization.GetString("Onboarding_Native_Title.Text") +
            ", " + SetupLocalization.GetString("Onboarding_Native_Recommended.Text"));
        AutomationProperties.SetName(InstallChoice, SetupLocalization.GetString("Onboarding_Wsl_Title.Text"));
        Loaded += OnLoaded;
        Unloaded += (_, _) => ++_probeGeneration;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _config = e.Parameter as SetupConfig ?? new SetupConfig();
        _selectedChoice = SetupWindow.Active?.WelcomeGatewayChoice;
        ApplySelection();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartMascotBreatheAnimation();
        AsyncEventHandlerGuard.Run(
            CheckNativeSupportAsync,
            NullLogger.Instance,
            nameof(CheckNativeSupportAsync));
        AsyncEventHandlerGuard.Run(
            DetectLocalAiAvailabilityAsync,
            NullLogger.Instance,
            nameof(DetectLocalAiAvailabilityAsync));
    }

    private async Task CheckNativeSupportAsync()
    {
        var window = SetupWindow.Active;
        if (window is null)
            return;
        var generation = ++_probeGeneration;
        _nativeEligibility = null;
        NativeChoice.IsEnabled = false;
        VisualStateManager.GoToState(this, "NativeDisabled", false);
        NativeSupportAvailablePanel.Visibility = Visibility.Collapsed;
        NativeSupportCard.Visibility = Visibility.Visible;
        NativeSupportStatusPanel.Visibility = Visibility.Visible;
        WindowsUpdateButton.Visibility = Visibility.Collapsed;
        NativeCheckProgress.IsActive = true;
        NativeCheckProgress.Visibility = Visibility.Visible;
        NativeSupportStatus.Text = SetupLocalization.GetString("Onboarding_Native_CheckingSupport");
        ApplySelection();

        NativeGatewayEligibility eligibility;
        try
        {
            eligibility = await window.GetNativeGatewayEligibilityAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                   or System.ComponentModel.Win32Exception)
        {
            Trace.TraceError($"Native Gateway capability check failed: {ex}");
            eligibility = NativeGatewayEligibility.CheckFailed;
        }
        if (generation != _probeGeneration || !IsLoaded || !ReferenceEquals(SetupWindow.Active, window))
            return;

        _nativeEligibility = eligibility;
        var available = eligibility == NativeGatewayEligibility.Available;
        NativeChoice.IsEnabled = available;
        VisualStateManager.GoToState(this, available ? "NativeEnabled" : "NativeDisabled", false);
        NativeSupportCard.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        NativeSupportAvailablePanel.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        NativeSupportStatusPanel.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        WindowsUpdateButton.Visibility = eligibility == NativeGatewayEligibility.CapabilityUnavailable
            ? Visibility.Visible : Visibility.Collapsed;
        var supportText = available ? NativeSupportAvailableText : NativeSupportStatus;
        supportText.Text = NativeGatewayEligibilityText.Get(eligibility);
        NativeCheckProgress.IsActive = false;
        NativeCheckProgress.Visibility = Visibility.Collapsed;
        SetChoice(NativeGatewaySetupEligibility.ResolveSelection(_selectedChoice, eligibility));
        var peer = FrameworkElementAutomationPeer.FromElement(supportText)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(supportText);
        peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void WindowsUpdate_Click(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(OpenWindowsUpdateAsync, NullLogger.Instance, nameof(WindowsUpdate_Click));

    private async Task OpenWindowsUpdateAsync()
    {
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:windowsupdate")))
                ShowWindowsUpdateError();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Trace.TraceError($"Opening Windows Update failed: {ex}");
            ShowWindowsUpdateError();
        }
    }

    private void ShowWindowsUpdateError()
    {
        Trace.TraceWarning("Windows Update could not be opened from native Gateway setup.");
        if (IsLoaded)
        {
            NativeSupportCard.Visibility = Visibility.Visible;
            NativeSupportStatusPanel.Visibility = Visibility.Visible;
            NativeSupportStatus.Text = SetupLocalization.GetString("Onboarding_Native_UpdateLaunchFailed");
        }
    }

    private async Task DetectLocalAiAvailabilityAsync()
    {
        SetupWindow? setupWindow = SetupWindow.Active;
        SetupConfig? config = _config;
        if (setupWindow is null || config is null)
            return;

        WslViabilityResult wslViability = await setupWindow.GetWslViabilityAsync();
        if (!IsLoaded || !ReferenceEquals(SetupWindow.Active, setupWindow))
            return;
        if (wslViability.BlocksSetup)
            return;

        var hardware = await setupWindow.GetLocalAiHardwareAsync();
        if (!IsLoaded || !ReferenceEquals(SetupWindow.Active, setupWindow))
            return;

        LocalInferenceEligibilityResult eligibility = LocalInferenceEligibility.Evaluate(hardware);
        if (!eligibility.CanInstall || eligibility.SelectedGpu is null)
            return;

        LocalAiAvailabilityText.Text = SetupLocalization.Format(
            "Onboarding_Welcome_LocalAiAvailabilityDetail",
            eligibility.SelectedGpu.Name);
        LocalAiAvailabilityPanel.Visibility = Visibility.Visible;
        // Capture the control's base accessible name once, so repeated detections (e.g. the
        // page is re-loaded after navigating back) rebuild the announcement from the same
        // starting point instead of appending the availability suffix again on every call.
        _installChoiceBaseAutomationName ??= AutomationProperties.GetName(InstallChoice);
        AutomationProperties.SetName(
            InstallChoice,
            $"{_installChoiceBaseAutomationName}, " +
            $"{SetupLocalization.GetString("Onboarding_Welcome_LocalAiAvailableBadge.Text")}");
        // FromElement returns null until a screen reader (or other AT client) has already
        // queried this element for a peer. This probe can complete before that happens, so the
        // live-region announcement would otherwise be silently skipped; force peer creation so
        // the event always has somewhere to go.
        AutomationPeer automationPeer = FrameworkElementAutomationPeer.FromElement(InstallChoice)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(InstallChoice);
        automationPeer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
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
        if (_suppressSelectionWrite)
            return;
        if (ReferenceEquals(GatewayChoiceSelector.SelectedItem, NativeChoice) &&
            _nativeEligibility == NativeGatewayEligibility.Available)
            SetChoice(GatewaySetupChoice.Native);
        else if (ReferenceEquals(GatewayChoiceSelector.SelectedItem, InstallChoice))
            SetChoice(GatewaySetupChoice.Wsl);
        else if (ReferenceEquals(GatewayChoiceSelector.SelectedItem, ConnectChoice))
            SetChoice(GatewaySetupChoice.Existing);
        else
            ApplySelection();
    }

    private void SetChoice(GatewaySetupChoice? choice)
    {
        _selectedChoice = choice;
        if (SetupWindow.Active is { } window)
            window.WelcomeGatewayChoice = choice;
        ApplySelection();
    }

    private void ApplySelection()
    {
        _suppressSelectionWrite = true;
        try
        {
            GatewayChoiceSelector.SelectedItem = _selectedChoice switch
            {
                GatewaySetupChoice.Native => NativeChoice,
                GatewaySetupChoice.Wsl => InstallChoice,
                GatewaySetupChoice.Existing => ConnectChoice,
                _ => null,
            };
            NextButton.IsEnabled = !_installInProgress &&
                (_selectedChoice is GatewaySetupChoice.Existing or GatewaySetupChoice.Wsl ||
                 (_selectedChoice == GatewaySetupChoice.Native && _nativeEligibility == NativeGatewayEligibility.Available));
        }
        finally { _suppressSelectionWrite = false; }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        SetupWindow.Active?.NavigateToSecurityNotice(back: true);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedChoice == GatewaySetupChoice.Native && _nativeEligibility == NativeGatewayEligibility.Available)
        {
            SetupWindow.Active?.NavigateToNativeCapabilities();
        }
        else if (_selectedChoice == GatewaySetupChoice.Wsl)
        {
            AsyncEventHandlerGuard.Run(
                StartInstallAsync,
                NullLogger.Instance,
                nameof(Next_Click));
        }
        else if (_selectedChoice == GatewaySetupChoice.Existing)
        {
            SetupWindow.Active?.NavigateToAdvancedSetup();
        }
    }

    private async Task StartInstallAsync()
    {
        var config = _config ?? throw new InvalidOperationException("Setup configuration has not been loaded.");
        var setupWindow = SetupWindow.Active;
        if (setupWindow is null)
            return;

        var dataDir = setupWindow.DataDir;

        // The progress ring carries the checking state (its automation name is
        // "Checking existing WSL setup"). Leave the option title alone: replacing it
        // hides which option is being acted on for as long as the check runs.
        NextButton.IsEnabled = false;
        _installInProgress = true;
        GatewayChoiceSelector.IsEnabled = false;
        InstallCheckProgress.IsActive = true;
        InstallCheckProgress.Visibility = Visibility.Visible;
        var navigating = false;
        try
        {
            while (true)
            {
                WslViabilityResult wslViability =
                    await setupWindow.GetWslViabilityAsync(refresh: true);
                if (wslViability.BlocksSetup)
                {
                    var readinessRoot = XamlRoot;
                    if (setupWindow.IsClosed || readinessRoot is null)
                        return;

                    var retry = await new ContentDialog
                    {
                        Title = "WSL2 is not ready",
                        Content = wslViability.Description,
                        PrimaryButtonText = "Try again",
                        CloseButtonText = "Cancel",
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = readinessRoot,
                    }.ShowAsync();

                    if (retry != ContentDialogResult.Primary)
                        return;
                    continue;
                }

                break;
            }

            ExistingConfigDetector.ExistingConfig existing;
            while (true)
            {
                try
                {
                    existing = await Task.Run(() => ExistingConfigDetector.Detect(
                        dataDir,
                        config.DistroName,
                        setupWindow.LocalDataDir));
                    break;
                }
                catch (InvalidOperationException ex)
                {
                    var errorRoot = XamlRoot;
                    if (setupWindow.IsClosed || errorRoot is null)
                        return;

                    // Inspection failure is usually transient, so offer a way forward
                    // instead of ending the flow on the recommended option.
                    var retry = await new ContentDialog
                    {
                        Title = "Could not inspect WSL",
                        Content = ex.Message,
                        PrimaryButtonText = "Try again",
                        CloseButtonText = "Cancel",
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = errorRoot,
                    }.ShowAsync();

                    if (retry != ContentDialogResult.Primary)
                        return;
                }
            }

            var xamlRoot = XamlRoot;
            if (setupWindow.IsClosed || xamlRoot is null)
                return;

            InstallCheckProgress.IsActive = false;
            InstallCheckProgress.Visibility = Visibility.Collapsed;
            var summary = ExistingConfigDetector.BuildReplacementSummary(existing);
            var requiresDestructiveConfirmation =
                ExistingConfigDetector.RequiresDestructiveConfirmation(existing);

            var dialog = new ContentDialog
            {
                Title = requiresDestructiveConfirmation
                    ? $"Permanently delete WSL distro '{config.DistroName}'?"
                    : existing.HasLocalGateway || existing.HasDistro || existing.HasDistroDataDirectory
                        ? "Replace existing WSL gateway?"
                        : "Install a new WSL gateway?",
                Content = summary,
                PrimaryButtonText = requiresDestructiveConfirmation
                    ? "Delete and replace"
                    : "Continue",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot,
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return;

            config.ConfirmedDestructiveDistroName = requiresDestructiveConfirmation
                ? config.DistroName
                : null;

            navigating = true;
            setupWindow.NavigateToCapabilities();
        }
        finally
        {
            _installInProgress = false;
            if (!navigating && !setupWindow.IsClosed)
            {
                GatewayChoiceSelector.IsEnabled = true;
                InstallCheckProgress.IsActive = false;
                InstallCheckProgress.Visibility = Visibility.Collapsed;
                NextButton.IsEnabled = true;
            }
        }
    }
}
