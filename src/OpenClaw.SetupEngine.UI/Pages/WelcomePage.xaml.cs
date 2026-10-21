using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
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
    private readonly LocalAiSetupAvailabilityCoordinator _availability = new();
    private CancellationTokenSource? _availabilityCancellation;

    public WelcomePage()
    {
        InitializeComponent();
        AutomationProperties.SetName(NativeChoice,
            SetupLocalization.GetString("Onboarding_Native_Title.Text") +
            ", " + SetupLocalization.GetString("Onboarding_Native_Recommended.Text"));
        AutomationProperties.SetName(InstallChoice, SetupLocalization.GetString("Onboarding_Wsl_Title.Text"));
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            ++_probeGeneration;
            CancelAvailability();
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        CancelAvailability();
        _config = e.Parameter as SetupConfig ?? new SetupConfig();
        _selectedChoice = SetupWindow.Active?.WelcomeGatewayChoice;
        ApplySelection();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        CancelAvailability();
        base.OnNavigatedFrom(e);
    }

    private void ClearAvailabilityBadge()
    {
        LocalAiAvailabilityPanel.Visibility = Visibility.Collapsed;
        LocalAiAvailabilityText.Text = "";
        AutomationProperties.SetName(LocalAiAvailabilityPanel, "");
    }

    private void CancelAvailability()
    {
        _availability.CancelCurrent();
        var cancellation = _availabilityCancellation;
        _availabilityCancellation = null;
        cancellation?.Cancel();
        ClearAvailabilityBadge();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
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
        ApplyNativeChoicePresentation(null);
        VisualStateManager.GoToState(this, "WslRecommendedState", false);
        WslRecommendedBadge.Visibility = Visibility.Visible;
        NativeSupportCard.Visibility = Visibility.Visible;
        NativeSupportStatusPanel.Visibility = Visibility.Visible;
        WindowsUpdateButton.Visibility = Visibility.Collapsed;
        NativeCheckProgress.IsActive = true;
        NativeCheckProgress.Visibility = Visibility.Visible;
        NativeGatewayEligibilityText.ApplyPlain(
            NativeSupportStatus,
            SetupLocalization.GetString("Onboarding_Native_CheckingSupport"));
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
        ApplyNativeChoicePresentation(eligibility);
        WslRecommendedBadge.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        NativeSupportCard.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        VisualStateManager.GoToState(this, available ? "NativeRecommendedState" : "WslRecommendedState", false);
        NativeSupportStatusPanel.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        WindowsUpdateButton.Visibility = eligibility == NativeGatewayEligibility.CapabilityUnavailable
            ? Visibility.Visible : Visibility.Collapsed;
        if (available)
            NativeGatewayEligibilityText.ApplyPlain(NativeSupportStatus, "");
        else
            NativeGatewayEligibilityText.Apply(NativeSupportStatus, eligibility);
        NativeCheckProgress.IsActive = false;
        NativeCheckProgress.Visibility = Visibility.Collapsed;
        SetChoice(NativeGatewaySetupEligibility.ResolveSelection(_selectedChoice, eligibility));
        if (!available)
        {
            var peer = FrameworkElementAutomationPeer.FromElement(NativeSupportStatus)
                ?? FrameworkElementAutomationPeer.CreatePeerForElement(NativeSupportStatus);
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    private void ApplyNativeChoicePresentation(NativeGatewayEligibility? eligibility)
    {
        bool available = eligibility == NativeGatewayEligibility.Available;
        NativeChoice.IsEnabled = available;
        PlaceNativeChoice(available ? 0 : GatewayChoiceSelector.Items.Count - 1);
    }

    private void PlaceNativeChoice(int targetIndex)
    {
        int currentIndex = GatewayChoiceSelector.Items.IndexOf(NativeChoice);
        if (currentIndex == targetIndex)
            return;

        object? selectedItem = GatewayChoiceSelector.SelectedItem;
        bool wasSuppressingSelectionWrite = _suppressSelectionWrite;
        _suppressSelectionWrite = true;
        try
        {
            GatewayChoiceSelector.Items.RemoveAt(currentIndex);
            GatewayChoiceSelector.Items.Insert(targetIndex, NativeChoice);
            GatewayChoiceSelector.SelectedItem = selectedItem;
        }
        finally
        {
            _suppressSelectionWrite = wasSuppressingSelectionWrite;
        }
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
            NativeGatewayEligibilityText.ApplyPlain(
                NativeSupportStatus,
                SetupLocalization.GetString("Onboarding_Native_UpdateLaunchFailed"));
        }
    }

    private async Task DetectLocalAiAvailabilityAsync()
    {
        CancelAvailability();
        SetupWindow? setupWindow = SetupWindow.Active;
        SetupConfig? config = _config;
        if (setupWindow is null || config is null || setupWindow.IsClosed)
            return;

        var generation = _availability.StartProbe().Generation;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _availabilityCancellation = cancellation;
        bool CanApply() => !cancellation.IsCancellationRequested && _availability.IsCurrent(generation) &&
            IsLoaded && !setupWindow.IsClosed && ReferenceEquals(SetupWindow.Active, setupWindow) &&
            ReferenceEquals(_config, config);
        try
        {
            WslViabilityResult wslViability = await setupWindow.GetWslViabilityAsync().WaitAsync(cancellation.Token);
            if (!CanApply() || wslViability.BlocksSetup) return;
            var hardware = await setupWindow.GetLocalAiHardwareAsync().WaitAsync(cancellation.Token);
            if (!CanApply()) return;
            LocalInferenceEligibilityResult eligibility = LocalInferenceEligibility.Evaluate(hardware);
            if (!eligibility.CanInstall || eligibility.SelectedGpu is null) return;
            if (!_availability.TryApplyAvailable(generation, out _)) return;
            LocalAiAvailabilityText.Text = SetupLocalization.Format(
                "Onboarding_Welcome_LocalAiAvailabilityDetail",
                eligibility.SelectedGpu.Name);
            LocalAiAvailabilityPanel.Visibility = Visibility.Visible;
            AutomationProperties.SetName(
                LocalAiAvailabilityPanel,
                $"{SetupLocalization.GetString("Onboarding_Welcome_LocalAiAvailableBadge.Text")}. {LocalAiAvailabilityText.Text}");
            // The capability announcement belongs to the general card, not a Gateway choice.
            AutomationPeer automationPeer = FrameworkElementAutomationPeer.FromElement(LocalAiAvailabilityPanel)
                ?? FrameworkElementAutomationPeer.CreatePeerForElement(LocalAiAvailabilityPanel);
            automationPeer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (_availability.IsCurrent(generation))
            {
                _availability.TryApplyProbeFailure(generation, "timeout", out _);
                System.Diagnostics.Trace.TraceWarning("Welcome Local AI availability observation timed out.");
            }
        }
        catch (Exception error)
        {
            if (_availability.IsCurrent(generation))
            {
                _availability.TryApplyProbeFailure(generation, error.GetType().Name, out _);
                System.Diagnostics.Trace.TraceWarning("Welcome Local AI availability is unknown ({0}).", error.GetType().Name);
            }
        }
        finally
        {
            if (ReferenceEquals(_availabilityCancellation, cancellation))
                _availabilityCancellation = null;
        }
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
            SetupWindow.Active?.SelectGatewayRoute(SetupGatewayRoute.Native);
            SetupWindow.Active?.NavigateToCapabilities();
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
            SetupWindow.Active?.SelectGatewayRoute(SetupGatewayRoute.Existing);
            SetupWindow.Active?.NavigateToNativeConnection(SetupGatewayRoute.Existing);
        }
    }

    private async Task StartInstallAsync()
    {
        CancelAvailability();
        var config = _config ?? throw new InvalidOperationException("Setup configuration has not been loaded.");
        var setupWindow = SetupWindow.Active;
        if (setupWindow is null) return;
        NextButton.IsEnabled = false;
        _installInProgress = true;
        GatewayChoiceSelector.IsEnabled = false;
        ReadinessError.IsOpen = false;
        MascotHero.Mood = OnboardingMascotMood.Thinking;
        InstallCheckProgress.IsActive = true;
        InstallCheckProgress.Visibility = Visibility.Visible;
        try
        {
            var viability = await setupWindow.GetWslViabilityAsync(refresh: true);
            if (!IsLoaded || setupWindow.IsClosed) return;
            if (viability.BlocksSetup)
            {
                ReadinessError.Title = SetupLocalization.GetString("Onboarding_V2_WslNotReady");
                ReadinessError.Message = viability.Description;
                ReadinessError.IsOpen = true;
                return;
            }
            var existing = await Task.Run(() => ExistingConfigDetector.Detect(
                setupWindow.DataDir, config.DistroName, setupWindow.LocalDataDir));
            if (!IsLoaded || setupWindow.IsClosed) return;
            setupWindow.AccessDraft.RecordWslInspection(existing);
            setupWindow.SelectGatewayRoute(SetupGatewayRoute.ManagedWsl);
            setupWindow.NavigateToCapabilities();
        }
        catch (InvalidOperationException ex)
        {
            if (!IsLoaded || setupWindow.IsClosed) return;
            ReadinessError.Title = SetupLocalization.GetString("Onboarding_V2_WslInspectFailure");
            ReadinessError.Message = ex.Message;
            ReadinessError.IsOpen = true;
        }
        finally
        {
            _installInProgress = false;
            if (!setupWindow.IsClosed)
            {
                MascotHero.Mood = ReadinessError.IsOpen ? OnboardingMascotMood.Sad : OnboardingMascotMood.Happy;
                InstallCheckProgress.IsActive = false;
                InstallCheckProgress.Visibility = Visibility.Collapsed;
                GatewayChoiceSelector.IsEnabled = true;
                ApplySelection();
            }
        }
    }

    private void Alternatives_Click(object sender, RoutedEventArgs e) =>
        SetupWindow.Active?.NavigateToAdvancedSetup();
}
