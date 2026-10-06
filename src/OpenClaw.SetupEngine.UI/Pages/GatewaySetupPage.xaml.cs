using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using OpenClawTray.Helpers;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class GatewaySetupPage : Page
{
    private SetupAccessDraft? _draft;
    private SetupWindow? _window;
    private SetupInstallRequirement? _primaryRequirement;
    private bool _applying;
    public GatewaySetupPage()
    {
        InitializeComponent();
        Unloaded += (_, _) => DetachTailscale();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        DetachTailscale();
        _draft = e.Parameter as SetupAccessDraft;
        _window = SetupWindow.Active;
        if (_draft is null || _window is null) return;
        var summary = SetupReviewSummaryBuilder.Build(_draft.Config, _window.DataDir, _window.LocalDataDir);
        DistroCard.Header = summary.DistroTitle;
        DistroCard.Description = summary.DistroDescription;
        DistroCard.HeaderIcon = FluentIconCatalog.Build(FluentIconCatalog.Terminal, 20);
        CliCard.Description = summary.InstallerDescription;
        var cliIcon = FluentIconCatalog.Build(FluentIconCatalog.Terminal, 20);
        cliIcon.IsTextScaleFactorEnabled = false;
        AutomationProperties.SetAccessibilityView(cliIcon, AccessibilityView.Raw);
        CliCard.HeaderIcon = cliIcon;
        GatewayCard.Description = $"{summary.GatewayDescription}\n{summary.GatewayEndpoint}";
        GatewayCard.HeaderIcon = FluentIconCatalog.Build(FluentIconCatalog.ServerEnvironment, 20);
        ExactCommandsText.Text = summary.ExactCommands;
        LocalAiCard.Description = SetupLocalization.GetString(_draft.Config.LocalAi.Enabled ? "Onboarding_V2_SelectedReview" : "Onboarding_V2_OptionalOff");
        LocalAiCard.Visibility = _draft.Config.LocalAi.Enabled || _window.IsLocalAiRecovery
            ? Visibility.Visible : Visibility.Collapsed;
        _applying = true;
        ReplacementOptions.Visibility = _draft.ReplacementReviewRequired && !_window.IsLocalAiRecovery
            ? Visibility.Visible : Visibility.Collapsed;
        ReplacementWarning.Message = _draft.ReplacementSummary;
        ReplacementConsent.Content = SetupLocalization.Format("Onboarding_V2_ReplaceDistro", _draft.Config.DistroName);
        ReplacementConsent.IsChecked = _draft.ReplacementReviewRequired && _draft.ReplacementConfirmed;
        _applying = false;
        StartupPreferenceRow.Visibility = _window.ShowStartupPreference ? Visibility.Visible : Visibility.Collapsed;
        StartupPreferenceToggle.IsOn = _window.AutoStartAfterSetup;
        TailscaleOptions.StateChanged += Tailscale_StateChanged;
        TailscaleOptions.Initialize(_draft);
        ApplyInstallGuidance();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        DetachTailscale();
        base.OnNavigatedFrom(e);
    }

    private void DetachTailscale()
    {
        TailscaleOptions.StateChanged -= Tailscale_StateChanged;
        TailscaleOptions.Deactivate();
    }

    private void Tailscale_StateChanged(object? sender, EventArgs e)
    {
        if (_draft is null || _window is null) return;
        ExactCommandsText.Text = SetupReviewSummaryBuilder.Build(
            _draft.Config, _window.DataDir, _window.LocalDataDir).ExactCommands;
        ApplyInstallGuidance();
    }

    private void ApplyInstallGuidance()
    {
        if (_draft is null || _window is null) return;
        var requirements = _draft.GetInstallRequirements(_window.IsLocalAiRecovery);
        _primaryRequirement = requirements.Count == 0 ? null : requirements[0];
        // Replacement has its own exact-target warning beside the consent checkbox.
        var otherRequirements = requirements.Where(requirement => requirement != SetupInstallRequirement.Replacement).ToArray();
        ReviewRequired.Visibility = otherRequirements.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        PrimaryButton.IsEnabled = _primaryRequirement != SetupInstallRequirement.Replacement;
        PrimaryButton.Content = requirements.Count == 0 || _primaryRequirement == SetupInstallRequirement.Replacement
            ? SetupLocalization.GetString("Onboarding_V2_Install.Content")
            : SetupLocalization.GetString($"Onboarding_V2_Action{requirements[0]}");
        if (otherRequirements.Length == 0) return;
        ReviewRequired.Title = SetupLocalization.GetString($"Onboarding_V2_Blocker{otherRequirements[0]}");
        ReviewRequired.Message = string.Join("\n", otherRequirements.Select(requirement =>
            SetupLocalization.Format($"Onboarding_V2_Requirement{requirement}", _draft.Config.DistroName)));
    }

    private void Startup_Toggled(object sender, RoutedEventArgs e)
    {
        if (_window is not null) _window.AutoStartAfterSetup = StartupPreferenceToggle.IsOn;
    }
    private void LocalAi_Click(object sender, RoutedEventArgs e) => _window?.NavigateToLocalAiSetup();
    private void ReplacementConsent_Changed(object sender, RoutedEventArgs e)
    {
        if (_applying || _draft is null || _window?.IsLocalAiRecovery != false) return;
        _draft.ConfirmReplacement(ReplacementConsent.IsChecked == true);
        ApplyInstallGuidance();
    }
    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_window?.IsLocalAiRecovery == true) _window.NavigateToWelcome(back: true);
        else _window?.NavigateToCapabilities(back: true);
    }
    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_window is null || _draft is null) return;
        var presentedRequirement = _primaryRequirement;
        ApplyInstallGuidance();
        // A changed draft must not turn a click on a review action into installation.
        if (presentedRequirement != _primaryRequirement) return;
        if (_primaryRequirement is null)
        {
            if (_draft.CanInstall(_window.IsLocalAiRecovery))
                _window.NavigateToProgress();
            return;
        }
        switch (_primaryRequirement)
        {
            case SetupInstallRequirement.ManagedWslRoute:
            case SetupInstallRequirement.WslInspection:
                _window.NavigateToWelcome(back: true);
                break;
            case SetupInstallRequirement.Replacement:
                break;
            case SetupInstallRequirement.LocalAi:
                _window.NavigateToLocalAiSetup();
                break;
            case SetupInstallRequirement.NetworkingConsent:
                _window.NavigateToWslNetworking(returnToReview: true);
                break;
            case SetupInstallRequirement.Tailscale:
                TailscaleOptions.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                break;
        }
    }
}
