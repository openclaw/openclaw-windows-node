using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Connection.Migration;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using WinUIEx;

namespace OpenClawTray.Windows;

public sealed partial class StoreMigrationWindow : Window
{
    private readonly StoreMigrationWorkflow _workflow;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<bool> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _allowClose;
    private StoreMigrationStage? _renderedStage;

    internal StoreMigrationWindow(StoreMigrationWorkflow workflow)
    {
        InitializeComponent();
        _workflow = workflow;
        Title = Heading.Text = TitleBarText.Text = LocalizationHelper.GetString("Migration2_Title");
        this.SetIcon("Assets\\openclaw.ico");
        InstalledApps.Content = LocalizationHelper.GetString("Migration2_InstalledApps");
        PrepareStepTitle.Text = LocalizationHelper.GetString("Migration2_PrepareStepTitle");
        PrepareStepBody.Text = LocalizationHelper.GetString("Migration2_PrepareStepBody");
        RemoveStepTitle.Text = LocalizationHelper.GetString("Migration2_RemoveStepTitle");
        RemoveStepBody.Text = LocalizationHelper.GetString("Migration2_RemoveStepBody");
        FinishStepTitle.Text = LocalizationHelper.GetString("Migration2_FinishStepTitle");
        FinishStepBody.Text = LocalizationHelper.GetString("Migration2_FinishStepBody");
        ConsentHint.Text = LocalizationHelper.GetString("Migration2_ConsentHint");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDrag);
        SystemBackdrop = new MicaBackdrop();
        this.SetWindowSize(720, 820);
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(
            Math.Min(AppWindow.Size.Width, workArea.Width), Math.Min(AppWindow.Size.Height, workArea.Height)));
        this.CenterOnScreen();
        AppWindow.Closing += OnClosing;
        Closed += OnClosed;
        _workflow.Changed += Render;
        Root.Loaded += OnLoaded;
        Render();
    }

    public Task<bool> ShowAsync()
    {
        Activate();
        return _finished.Task;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Root.Loaded -= OnLoaded;
        AsyncEventHandlerGuard.Run(() => _workflow.StartAsync(_lifetime.Token), new AppLogger(), nameof(OnLoaded));
    }

    private void OnPrimary(object sender, RoutedEventArgs args) =>
        AsyncEventHandlerGuard.Run(() => _workflow.ContinueAsync(_lifetime.Token), new AppLogger(), nameof(OnPrimary));

    private void OnDiscardRecords(object sender, RoutedEventArgs args) =>
        AsyncEventHandlerGuard.Run(
            () => _workflow.DiscardRecordsAsync(_lifetime.Token), new AppLogger(), nameof(OnDiscardRecords));

    private void Render()
    {
        if (_workflow.Stage == StoreMigrationStage.Ready && !_workflow.IsBusy)
        {
            _allowClose = true;
            _finished.TrySetResult(true);
            Close();
            return;
        }

        RenderGuidance();
        Progress.IsActive = _workflow.IsBusy;
        Progress.Visibility = _workflow.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        Primary.IsEnabled = Dismiss.IsEnabled = !_workflow.IsBusy;
        // A refused startup task can only be changed in Windows, so there is nothing to retry.
        // Recovery is retried deliberately: the user is told to remove the previous app, and the
        // next pass is what notices they did.
        Primary.Visibility = _workflow.Stage is StoreMigrationStage.StartupRefused
            ? Visibility.Collapsed : Visibility.Visible;
        Primary.Content = LocalizationHelper.GetString(_workflow.Stage == StoreMigrationStage.Consent
            ? "Migration_StoreMigrate" : "Migration_StoreRetry");
        Dismiss.Content = LocalizationHelper.GetString(_workflow.Stage == StoreMigrationStage.Consent
            ? "Migration_StoreNotNow" : "Migration2_Close");
        // Removing the previous app is the remedy for a receipt that no longer matches it, so the
        // same shortcut is offered in recovery.
        InstalledApps.Visibility = _workflow.Stage is StoreMigrationStage.AwaitingRemoval
            or StoreMigrationStage.Recovery ? Visibility.Visible : Visibility.Collapsed;
        InstalledApps.IsEnabled = !_workflow.IsBusy;
        DiscardRecords.Content = LocalizationHelper.GetString("Migration2_DiscardRecords");
        DiscardRecords.Visibility = _workflow.Stage == StoreMigrationStage.Recovery && _workflow.CanDiscardRecords
            ? Visibility.Visible : Visibility.Collapsed;
        DiscardRecords.IsEnabled = !_workflow.IsBusy;
        AutomationProperties.SetName(Progress, LocalizationHelper.GetString("Migration2_Busy"));
        if (_renderedStage != _workflow.Stage)
        {
            _renderedStage = _workflow.Stage;
            // An error raised in a previous stage no longer describes what the user sees.
            Error.IsOpen = false;
            FrameworkElementAutomationPeer.FromElement(Status)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        // A discard that could not run leaves the stage unchanged, so it has to report itself.
        // Only in Recovery: every other stage has its own error to show, and a stale discard
        // message would outlive the screen the button belongs to.
        if (_workflow.Stage == StoreMigrationStage.Recovery
            && _workflow.LastDiscard is StoreMigrationDiscardState.Busy or StoreMigrationDiscardState.Failed)
        {
            Error.Message = LocalizationHelper.GetString("Migration2_DiscardFailed");
            Error.IsOpen = true;
        }
        if (!_workflow.IsBusy)
        {
            // Consent defaults to the non-destructive action; keyboard activation cannot auto-confirm.
            (_workflow.Stage is StoreMigrationStage.Consent or StoreMigrationStage.Recovery
                or StoreMigrationStage.StartupRefused ? Dismiss : Primary)
                .Focus(FocusState.Programmatic);
        }
    }

    private void RenderGuidance()
    {
        Heading.Text = LocalizationHelper.GetString("Migration2_" + (_workflow.Stage switch
        {
            StoreMigrationStage.CloseSource => "CloseInnoTitle",
            StoreMigrationStage.AwaitingRemoval => "RemovalTitle",
            _ => "Title"
        }));
        var consentVisibility = _workflow.Stage == StoreMigrationStage.Consent
            ? Visibility.Visible : Visibility.Collapsed;
        ConsentSteps.Visibility = ConsentHint.Visibility = consentVisibility;
        // Reuses the existing refusal string rather than adding a Migration2_ key, because every
        // new resource has to be seeded across all locales.
        Status.Text = _workflow.Stage == StoreMigrationStage.StartupRefused
            ? LocalizationHelper.GetString("Migration_StoreStartupRefused")
            : LocalizationHelper.GetString("Migration2_" + (_workflow.Stage switch
        {
            StoreMigrationStage.Consent => "Consent",
            StoreMigrationStage.ClosingSource => "Closing",
            StoreMigrationStage.Preparing => "Preparing",
            StoreMigrationStage.Completing => "Completing",
            StoreMigrationStage.Finalizing => "Finalizing",
            StoreMigrationStage.CloseSource => "CloseInno",
            StoreMigrationStage.AwaitingRemoval => "AwaitingRemoval",
            StoreMigrationStage.ValidationFailed => "ValidationFailed",
            StoreMigrationStage.UpdateRequired => "UpdateRequired",
            StoreMigrationStage.Unsupported => "Unsupported",
            StoreMigrationStage.Recovery => "Recovery",
            StoreMigrationStage.FinalizationFailed => "FinalizationFailed",
            StoreMigrationStage.InspectionFailed => "InspectionFailed",
            _ => "Preparing"
        }));
        var warningKey = _workflow.Stage switch
        {
            StoreMigrationStage.Consent => "Consent",
            StoreMigrationStage.CloseSource => "CloseInno",
            StoreMigrationStage.AwaitingRemoval => "Removal",
            _ => null
        };
        Safety.Title = warningKey is null ? string.Empty : LocalizationHelper.GetString($"Migration2_{warningKey}WarningTitle");
        Safety.Message = warningKey is null ? string.Empty : LocalizationHelper.GetString($"Migration2_{warningKey}Warning");
        Safety.IsOpen = warningKey is not null;
        Safety.Visibility = Safety.IsOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnInstalledApps(object sender, RoutedEventArgs args) =>
        AsyncEventHandlerGuard.Run(OpenInstalledAppsAsync, new AppLogger(), nameof(OnInstalledApps));

    private async Task OpenInstalledAppsAsync()
    {
        try
        {
            if (!await global::Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:appsfeatures")))
                throw new InvalidOperationException("Windows declined the Installed apps URI.");
        }
        catch (Exception exception)
        {
            Logger.Error($"Could not open Installed apps: {exception.Message}");
            Error.Message = LocalizationHelper.GetString("Migration2_LaunchFailed");
            Error.IsOpen = true;
        }
    }

    private void OnDismiss(object sender, RoutedEventArgs args) => Close();

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_allowClose && _workflow.IsBusy)
            args.Cancel = true;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _lifetime.Cancel();
        _workflow.Changed -= Render;
        // Dismissing an informational stage returns the user to the app. Only a stage holding
        // a completion receipt keeps launch blocked.
        _finished.TrySetResult(!_workflow.BlocksStartup);
        _lifetime.Dispose();
    }
}
