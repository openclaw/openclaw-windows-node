using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace OpenClaw.SetupEngine.UI.Controls;

/// <summary>
/// One installation phase row on the progress page. The localized status text remains the
/// accessible meaning; the leading glyph and colors are decoration.
/// </summary>
/// <example>
/// <code>
/// &lt;controls:SetupPhaseStatus x:Name="PrepareStatus" x:Uid="Onboarding_V4_PhasePrepare" /&gt;
/// PrepareStatus.Apply(SetupInstallationStatus.Running);
/// PrepareStatus.Activity = "Clean up stale WSL distro";
/// </code>
/// </example>
public sealed partial class SetupPhaseStatus : UserControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(SetupPhaseStatus),
        new PropertyMetadata(string.Empty, (sender, args) => ((SetupPhaseStatus)sender).ApplyHeader()));

    public SetupPhaseStatus() => InitializeComponent();

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>The phase's current step, or empty when the phase is idle.</summary>
    public string? Activity
    {
        get => ActivityText.Text;
        set
        {
            ActivityText.Text = value ?? "";
            ActivityText.Visibility = string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public void Apply(SetupInstallationStatus status)
    {
        StatusText.Text = SetupLocalization.GetString("Onboarding_V4_Status" + status);
        RunningIcon.IsActive = status == SetupInstallationStatus.Running;
        VisualStateManager.GoToState(this, status switch
        {
            SetupInstallationStatus.Running => "Running",
            SetupInstallationStatus.Complete => "Complete",
            SetupInstallationStatus.Failed => "Failed",
            SetupInstallationStatus.Cancelled => "Cancelled",
            _ => "Pending",
        }, false);
        ApplyHeader();
    }

    private void ApplyHeader()
    {
        TitleText.Text = Header;
        AutomationProperties.SetName(this, string.IsNullOrEmpty(StatusText.Text) ? Header : $"{Header}, {StatusText.Text}");
    }
}
