using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClaw.SetupEngine.UI.Controls;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed record AiReadyPageArgs(SetupReadyCoordinator Coordinator, Action Recover, Func<bool> IsCurrent,
    bool IsCommitted = false, SetupReadyGateway? Gateway = null);

public sealed partial class AiReadyPage : Page, IAsyncDisposable
{
    private AiReadyPageArgs? _args;
    private bool _closed;
    private bool _admitted;

    public AiReadyPage()
    {
        InitializeComponent();
        ChatAction.Content = SetupLocalization.GetString("Onboarding_Ready_OpenChat");
        PairedTitle.Text = SetupLocalization.GetString("Onboarding_Ready_PairedTitle");
        PairedSummary.Text = SetupLocalization.GetString("Onboarding_Ready_PairedDescription");
        CapabilitiesTitle.Text = SetupLocalization.GetString("Onboarding_Ready_CapabilitiesTitle");
        CapabilitiesSummary.Text = SetupLocalization.GetString("Onboarding_Ready_CapabilitiesDescription");
        ModelTitle.Text = SetupLocalization.GetString("Onboarding_Ready_ModelTitle");
        Prompt.Text = SetupLocalization.GetString("Onboarding_Ready_Prompt");
        Loaded += (_, _) => { if (_admitted) Celebrate(true); };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not AiReadyPageArgs args || !args.IsCurrent())
            throw new InvalidOperationException("This page requires the current setup's verified completion.");
        _args = args;
        ShowGateway(args.Gateway ?? SetupReadyGateway.Unknown);
        Heading.Text = SetupLocalization.GetString("Onboarding_Finishing_Heading.Text");
        _admitted = args.IsCommitted;
        SetChatActionEnabled(_admitted);
        if (_admitted) AdmitChatAction();
        var model = args.Coordinator.Proof.ModelRef;
        ModelSummary.Text = model.Length <= 160 && !model.Any(char.IsControl) ? model : "";
        ModelSummary.Visibility = ModelSummary.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Describe the Gateway by how setup reached it; only the native and WSL routes host it here.
    private void ShowGateway(SetupReadyGateway gateway)
    {
        var (title, description) = gateway.Kind switch
        {
            SetupReadyGatewayKind.Native => ("Onboarding_Ready_GatewayNativeTitle", "Onboarding_Ready_GatewayDescription"),
            SetupReadyGatewayKind.Wsl => ("Onboarding_Ready_GatewayWslTitle", "Onboarding_Ready_GatewayWslDescription"),
            SetupReadyGatewayKind.LocalExisting => ("Onboarding_Ready_GatewayConnectedTitle", "Onboarding_Ready_GatewayLocalDescription"),
            SetupReadyGatewayKind.Remote => ("Onboarding_Ready_GatewayRemoteTitle", "Onboarding_Ready_GatewayVerifiedDescription"),
            _ => ("Onboarding_Ready_GatewayConnectedTitle", "Onboarding_Ready_GatewayVerifiedDescription"),
        };
        GatewayTitle.Text = SetupLocalization.GetString(title);
        GatewaySummary.Text = gateway is { Kind: SetupReadyGatewayKind.Remote, Host: { Length: > 0 } host }
            ? host : SetupLocalization.GetString(description);
    }

    // Verified readiness mirrors CompletePage's success companion; anything else drops the cap.
    private void Celebrate(bool celebrating)
    {
        if (SetupCompanion.Find(this) is not { } companion) return;
        if (celebrating)
            companion.Present(SetupLocalization.GetString("Onboarding_Bubble_Done"), OnboardingMascotMood.Celebrating);
        companion.Mascot.Accessory = celebrating ? OnboardingMascotAccessory.GradCap : OnboardingMascotAccessory.None;
    }

    private void ShowSummary(bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Prompt.Visibility = SummaryPanel.Visibility = ChatActionPanel.Visibility = visibility;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Celebrate(false);
        if (_args is not { } args) return;
        args.Coordinator.Dispose();
    }

    private void OpenChat_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || !_admitted || _args is not { } args || !args.IsCurrent() ||
            args.Coordinator.IsBusy || args.Coordinator.IsCompleted ||
            !ReferenceEquals(sender, ChatAction))
            return;
        AsyncEventHandlerGuard.Run(() => OpenChatAsync(args), onError: error =>
            System.Diagnostics.Trace.TraceWarning("Native setup completion failed ({0}).", error.GetType().Name));
    }

    private async Task OpenChatAsync(AiReadyPageArgs args)
    {
        SetBusy(true);
        ErrorBar.IsOpen = false;
        try { await args.Coordinator.SelectAsync(SetupNativeDestination.Chat); }
        catch (Exception error)
        {
            if (_closed || !args.IsCurrent()) return;
            try
            {
                args.Coordinator.RequireCurrent();
                RecoveryButton.Visibility = Visibility.Collapsed;
            }
            catch (Exception)
            {
                Heading.Text = SetupLocalization.GetString("Onboarding_Finishing_Heading.Text");
                ShowSummary(false);
                Celebrate(false);
                RecoveryButton.Visibility = Visibility.Visible;
            }
            ErrorBar.Message = SetupLocalization.GetString(AiSetupReadinessPresentation.CompletionFailureKey(
                error, "Onboarding_Ready_OpeningFailed"));
            ErrorBar.IsOpen = true;
            System.Diagnostics.Trace.TraceWarning("Native setup completion needs retry ({0}; phase: {1}).",
                error.GetType().Name, (error as SetupNativeCompletionTimeoutException)?.Phase.ToString() ?? "none");
        }
        finally { if (!_closed) SetBusy(args.Coordinator.IsCompleted); }
    }

    private void SetBusy(bool busy)
    {
        SetChatActionEnabled(!busy && _admitted);
        BusyProgress.Visibility = StatusText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = SetupLocalization.GetString("Onboarding_Ready_Opening");
    }

    private void Return_Click(object sender, RoutedEventArgs e)
    {
        if (!_closed && _admitted && _args is { } args && !args.Coordinator.IsBusy)
            args.Recover();
    }

    public void ShowInvalidated()
    {
        Heading.Text = SetupLocalization.GetString("Onboarding_Finishing_Heading.Text");
        ShowSummary(false);
        Celebrate(false);
        ErrorBar.Message = SetupLocalization.GetString("Onboarding_Ready_OwnershipChanged");
        ErrorBar.IsOpen = true;
        RecoveryButton.IsEnabled = _admitted;
        RecoveryButton.Visibility = Visibility.Visible;
    }

    public void AdmitChatAction()
    {
        _admitted = true;
        Heading.Text = SetupLocalization.GetString("Onboarding_Ready_Heading.Text");
        Celebrate(true);
        ShowSummary(true);
        SetChatActionEnabled(true);
    }

    private void SetChatActionEnabled(bool enabled)
    {
        ChatAction.IsEnabled = enabled;
        RecoveryButton.IsEnabled = enabled;
    }

    public async ValueTask DisposeAsync()
    {
        _closed = true;
        if (_args is not { } args) return;
        args.Coordinator.Dispose();
        await args.Coordinator.CleanupCompleted;
    }
}
