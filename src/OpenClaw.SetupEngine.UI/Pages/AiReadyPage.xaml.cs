using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.Shared;
using OpenClawTray.Helpers;

namespace OpenClaw.SetupEngine.UI.Pages;

internal sealed record AiReadyPageArgs(SetupNativeCompletionCoordinator Coordinator, SetupWindow Owner);

public sealed partial class AiReadyPage : Page, IAsyncDisposable
{
    private AiReadyPageArgs? _args;
    private bool _closed;

    public AiReadyPage()
    {
        InitializeComponent();
        ChatChoice.HeaderIcon = FluentIconCatalog.Build(FluentIconCatalog.Chat, 20);
        ChannelsChoice.HeaderIcon = FluentIconCatalog.Build(FluentIconCatalog.Sessions, 20);
        SkillsChoice.HeaderIcon = FluentIconCatalog.Build(FluentIconCatalog.Develop, 20);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not AiReadyPageArgs args || !args.Owner.OwnsReadyChoice(args.Coordinator))
            throw new InvalidOperationException("This page requires the current setup's verified completion.");
        _args = args;
        args.Coordinator.StateChanged += UpdateStatus;
        var model = args.Coordinator.Proof.ModelRef;
        ModelSummary.Text = model.Length <= 160 && !model.Any(char.IsControl)
            ? SetupLocalization.Format("Onboarding_Ready_Model", model) : "";
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_args is not { } args) return;
        args.Coordinator.StateChanged -= UpdateStatus;
        args.Coordinator.Dispose();
    }

    private void UpdateStatus()
    {
        if (!_closed && _args is { } args)
            StatusText.Text = SetupLocalization.GetString("Onboarding_Ready_" + args.Coordinator.Stage);
    }

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _args is not { } args || !args.Owner.OwnsReadyChoice(args.Coordinator) ||
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
            if (_closed || args.Owner.IsClosed) return;
            ErrorBar.Message = SetupLocalization.GetString(error switch
            {
                SetupNativeOwnershipException => "Onboarding_Ready_OwnershipChanged",
                OperationCanceledException or TimeoutException => "Onboarding_Ready_Timeout",
                _ => "Onboarding_Ready_" + args.Coordinator.Stage + "Failed",
            });
            ErrorBar.IsOpen = true;
            System.Diagnostics.Trace.TraceWarning("Native setup completion needs retry ({0}).", error.GetType().Name);
        }
        finally { if (!_closed) SetBusy(args.Coordinator.IsCompleted); }
    }

    private void SetBusy(bool busy)
    {
        foreach (var choice in Choices.Children.OfType<Control>())
            choice.IsEnabled = !busy;
        RecoveryButton.IsEnabled = !busy;
        BusyProgress.Visibility = StatusText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = SetupLocalization.GetString("Onboarding_Ready_Verifying");
    }

    private void Return_Click(object sender, RoutedEventArgs e)
    {
        if (!_closed && _args is { } args && !args.Coordinator.IsBusy)
            args.Owner.ReturnFromReadyChoice(args.Coordinator);
    }

    public async ValueTask DisposeAsync()
    {
        _closed = true;
        if (_args is not { } args) return;
        args.Coordinator.StateChanged -= UpdateStatus;
        args.Coordinator.Dispose();
        try { await args.Coordinator.ActiveTask; }
        catch (OperationCanceledException) { }
    }
}
