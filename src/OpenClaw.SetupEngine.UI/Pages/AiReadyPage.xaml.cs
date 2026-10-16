using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.Connection.NativeGateway;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClaw.SetupEngine.UI.Controls;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed record AiReadyPageArgs(SetupReadyCoordinator Coordinator, Action Recover, Func<bool> IsCurrent,
    bool IsCommitted = false);

public sealed partial class AiReadyPage : Page, IAsyncDisposable
{
    private AiReadyPageArgs? _args;
    private bool _closed;
    private bool _admitted;

    public AiReadyPage()
    {
        InitializeComponent();
        ChatChoice.HeaderIcon = FluentIconCatalog.Build(FluentIconCatalog.Chat, 20);
        ChannelsChoice.HeaderIcon = FluentIconCatalog.Build(FluentIconCatalog.Sessions, 20);
        SkillsChoice.HeaderIcon = FluentIconCatalog.Build(FluentIconCatalog.Develop, 20);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not AiReadyPageArgs args || !args.IsCurrent())
            throw new InvalidOperationException("This page requires the current setup's verified completion.");
        _args = args;
        Heading.Text = SetupLocalization.GetString("Onboarding_Finishing_Heading.Text");
        FlowProgress.Update([OnboardingStage.AiSetup, OnboardingStage.Ready], OnboardingStage.Ready);
        _admitted = args.IsCommitted;
        SetChoicesEnabled(_admitted);
        if (_admitted) AdmitChoices();
        var model = args.Coordinator.Proof.ModelRef;
        ModelSummary.Text = model.Length <= 160 && !model.Any(char.IsControl)
            ? SetupLocalization.Format("Onboarding_Ready_Model", model) : "";
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_args is not { } args) return;
        args.Coordinator.Dispose();
    }

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || !_admitted || _args is not { } args || !args.IsCurrent() ||
            args.Coordinator.IsBusy || args.Coordinator.IsCompleted ||
            sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<SetupNativeDestination>(tag, out var destination))
            return;
        AsyncEventHandlerGuard.Run(() => ChooseAsync(args, destination), onError: error =>
            System.Diagnostics.Trace.TraceWarning("Native setup completion failed ({0}).", error.GetType().Name));
    }

    private async Task ChooseAsync(AiReadyPageArgs args, SetupNativeDestination destination)
    {
        SetBusy(true);
        ErrorBar.IsOpen = false;
        try { await args.Coordinator.SelectAsync(destination); }
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
                Choices.Visibility = Visibility.Collapsed;
                RecoveryButton.Visibility = Visibility.Visible;
            }
            ErrorBar.Message = SetupLocalization.GetString(error switch
            {
                SetupNativeOwnershipException => "Onboarding_Ready_OwnershipChanged",
                SetupNativeCompletionTimeoutException { Phase: SetupNativeCompletionPhase.Verification } => "Onboarding_Ready_Timeout",
                SetupNativeCompletionTimeoutException timeout => "Onboarding_Ready_Timeout" + timeout.Phase,
                NativeGatewayStartupTimeoutException => "Onboarding_Ready_TimeoutGatewayStartup",
                OperationCanceledException or TimeoutException => "Onboarding_Ready_Timeout",
                _ => "Onboarding_Ready_OpeningFailed",
            });
            ErrorBar.IsOpen = true;
            System.Diagnostics.Trace.TraceWarning("Native setup completion needs retry ({0}; phase: {1}).",
                error.GetType().Name, (error as SetupNativeCompletionTimeoutException)?.Phase.ToString() ?? "none");
        }
        finally { if (!_closed) SetBusy(args.Coordinator.IsCompleted); }
    }

    private void SetBusy(bool busy)
    {
        SetChoicesEnabled(!busy && _admitted);
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
        Choices.Visibility = Visibility.Collapsed;
        ErrorBar.Message = SetupLocalization.GetString("Onboarding_Ready_OwnershipChanged");
        ErrorBar.IsOpen = true;
        RecoveryButton.IsEnabled = _admitted;
        RecoveryButton.Visibility = Visibility.Visible;
    }

    public void AdmitChoices()
    {
        _admitted = true;
        Heading.Text = SetupLocalization.GetString("Onboarding_Ready_Heading.Text");
        Prompt.Text = SetupLocalization.GetString("Onboarding_Ready_Prompt.Text");
        MascotHero.Mood = OnboardingMascotMood.Celebrating;
        Choices.Visibility = Visibility.Visible;
        SetChoicesEnabled(true);
    }

    private void SetChoicesEnabled(bool enabled)
    {
        foreach (var choice in Choices.Children.OfType<Control>())
            choice.IsEnabled = enabled;
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
