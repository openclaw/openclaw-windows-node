using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine.UI.Pages;

internal enum GatewaySetupDetail { LocalAi, Tailscale, Networking }
internal sealed record GatewaySetupDetailArgs(
    SetupAccessDraft Draft, GatewaySetupDetail Detail, bool Recovery, bool PinModel, bool ReturnToReview = false);

public sealed partial class GatewaySetupDetailPage : Page
{
    private GatewaySetupDetailArgs? _args;
    private bool _applying;
    private bool _installing;
    public GatewaySetupDetailPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _args = e.Parameter as GatewaySetupDetailArgs;
        if (_args is null) return;
        _applying = true;
        DetailTitle.Text = SetupLocalization.GetString($"Onboarding_V2_Detail{_args.Detail}");
        switch (_args.Detail)
        {
            case GatewaySetupDetail.LocalAi:
                LocalAiOptions.Visibility = Visibility.Visible;
                LocalAiOptions.Initialize(_args.Draft, _args.Recovery, _args.PinModel);
                LocalAiOptions.StateChanged += LocalAi_StateChanged;
                break;
            case GatewaySetupDetail.Tailscale:
                TailscaleOptions.Visibility = Visibility.Visible;
                TailscaleOptions.Initialize(_args.Draft);
                break;
            case GatewaySetupDetail.Networking:
                NetworkingOptions.Visibility = Visibility.Visible;
                LocalAiNetworkingConsentCheckBox.IsChecked = _args.Draft.Config.LocalAi.WslMirroredNetworkingConsent;
                break;
        }
        _applying = false;
        UpdatePrimaryAction();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        LocalAiOptions.Deactivate();
        TailscaleOptions.Deactivate();
        LocalAiOptions.StateChanged -= LocalAi_StateChanged;
        base.OnNavigatedFrom(e);
    }

    private void NetworkConsent_Changed(object sender, RoutedEventArgs e)
    {
        if (!_applying && _args is not null)
            _args.Draft.Config.LocalAi.WslMirroredNetworkingConsent = LocalAiNetworkingConsentCheckBox.IsChecked == true;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_installing) return;
        if (_args is { Detail: GatewaySetupDetail.Networking, ReturnToReview: false })
            SetupWindow.Active?.NavigateToLocalAiSetup(back: true);
        else if (SetupWindow.Active?.IsAiLocalReview == true)
            SetupWindow.Active.CancelLocalAiReview();
        else
            SetupWindow.Active?.NavigateToGatewaySetup(back: true);
    }

    private void LocalAi_StateChanged(object? sender, EventArgs e) => UpdatePrimaryAction();

    private void UpdatePrimaryAction()
    {
        if (_args?.Detail != GatewaySetupDetail.LocalAi || SetupWindow.Active?.IsAiLocalReview != true)
            return;
        PrimaryButton.Content = SetupLocalization.GetString("Onboarding_AiSetup_LocalInstall");
        PrimaryButton.IsEnabled = !_installing && _args.Draft.CanInstall(localAiRecovery: true);
    }

    private void Primary_Click(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(() => PrimaryAsync(sender, e), NullLogger.Instance, nameof(Primary_Click));

    private async Task PrimaryAsync(object sender, RoutedEventArgs e)
    {
        if (_args?.Detail != GatewaySetupDetail.LocalAi || SetupWindow.Active?.IsAiLocalReview != true)
        {
            Back_Click(sender, e);
            return;
        }
        if (_installing) return;
        _installing = true;
        UpdatePrimaryAction();
        try { await SetupWindow.Active.InstallReviewedLocalAiAsync(); }
        catch (Exception)
        {
            LocalAiReviewError.Message = SetupLocalization.GetString("Onboarding_AiSetup_LocalChanged");
            LocalAiReviewError.IsOpen = true;
        }
        finally { _installing = false; UpdatePrimaryAction(); }
    }
}
