using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenClaw.SetupEngine.UI.Controls;

/// <summary>Native status decoration. The localized text remains the accessible meaning.</summary>
public sealed partial class SetupPhaseStatus : UserControl
{
    public SetupPhaseStatus() => InitializeComponent();

    public void Apply(SetupInstallationStatus status)
    {
        StatusText.Text = SetupLocalization.GetString("Onboarding_V4_Status" + status);
        RunningIcon.IsActive = status == SetupInstallationStatus.Running;
        RunningIcon.Visibility = RunningIcon.IsActive ? Visibility.Visible : Visibility.Collapsed;
        CompleteIcon.Visibility = status == SetupInstallationStatus.Complete ? Visibility.Visible : Visibility.Collapsed;
        FailedIcon.Visibility = status == SetupInstallationStatus.Failed ? Visibility.Visible : Visibility.Collapsed;
        CancelledIcon.Visibility = status == SetupInstallationStatus.Cancelled ? Visibility.Visible : Visibility.Collapsed;
    }
}
