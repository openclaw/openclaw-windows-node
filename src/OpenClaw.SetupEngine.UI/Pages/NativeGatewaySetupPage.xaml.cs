using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Connection;
using OpenClaw.Connection.NativeGateway;
using OpenClaw.SetupEngine.UI.Controls;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class NativeGatewaySetupPage : Page
{
    private static readonly string[] StepKeys = ["StepSupport", "StepPackage", "StepPrepare", "StepVerify"];
    // From this step on the package is installed and a setup draft names its family.
    private const int PrepareStepIndex = 2;
    // Lives beside ProgressPage's setup-engine-*.jsonl so diagnostics bundles include it.
    private const string SetupLogPrefix = "native-setup-";

    private readonly NativeGatewayPackageResolver _resolver = new();
    private readonly NativeGatewayMsixInstaller _installer = new();
    private readonly NativeGatewaySetupHost _recoveryHost = new();
    private CancellationTokenSource? _operationCts;
    private CancellationTokenSource _recoveryCts = new();
    private Task? _operation;
    private NativeGatewaySetupService? _setupService;
    private IDisposable? _recoveryTerminal;
    private SetupLogger? _log;
    private readonly List<SetupPhaseStatus> _rows = [];
    private int _currentStep;
    internal bool IsBusy => _operation is { IsCompleted: false };

    public NativeGatewaySetupPage()
    {
        InitializeComponent();
        foreach (var key in StepKeys)
        {
            var status = new SetupPhaseStatus();
            status.Apply(SetupInstallationStatus.Pending);
            var row = new SettingsCard
            {
                Header = SetupLocalization.GetString($"Onboarding_Native_{key}"),
                Content = status,
            };
            AutomationProperties.SetAutomationId(row, $"NativeGateway{key}");
            _rows.Add(status);
            StepsPanel.Children.Add(row);
        }
        Loaded += (_, _) => StartOperation();
        Unloaded += (_, _) => _operationCts?.Cancel();
        Unloaded += (_, _) => ReleaseRecoveryResources();
    }

    private void StartOperation()
    {
        if (IsBusy)
            return;
        if (SetupPreview.IsActive)
        {
            StatusText.Text = SetupLocalization.GetString("Onboarding_Native_Preview");
            return;
        }
        _log ??= CreateSetupLog();
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        _operation = RunOperationAsync(_log, _operationCts.Token);
    }

    private static SetupLogger CreateSetupLog()
    {
        if (SetupWindow.Active is { } window)
        {
            try
            {
                return new SetupLogger(Path.Combine(window.DataDir, "Logs", "Setup",
                    $"{SetupLogPrefix}{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"The native setup log could not be created ({ex.GetType().Name}).");
            }
        }
        return new SetupLogger(filePath: null);
    }

    private async Task RunOperationAsync(SetupLogger log, CancellationToken cancellationToken)
    {
        foreach (var row in _rows)
            row.Apply(SetupInstallationStatus.Pending);
        _currentStep = 0;
        RetryButton.Visibility = Visibility.Collapsed;
        HideRecovery();
        SetBusy(true);
        log.Info("Native Gateway setup attempt started.");
        try
        {
            await ConfigureAsync(log, cancellationToken);
        }
        catch (NativeGatewaySetupService.NativeGatewayDraftRecoveryRequiredException ex)
        {
            var discard = await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = SetupLocalization.GetString("Onboarding_Native_ReplaceDraftTitle"),
                Content = SetupLocalization.GetString("Onboarding_Native_ReplaceDraftContent"),
                PrimaryButtonText = SetupLocalization.GetString("Onboarding_Native_ReplaceDraftPrimary"),
                CloseButtonText = SetupLocalization.GetString("Onboarding_Native_ReplaceDraftClose"),
                DefaultButton = ContentDialogButton.Close,
            }.ShowAsync();
            if (discard == ContentDialogResult.Primary)
            {
                try
                {
                    await (_setupService ?? throw new InvalidOperationException("Native Gateway setup is unavailable."))
                        .DiscardIncompatibleDraftAsync(cancellationToken);
                    await ConfigureAsync(log, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    log.Info("Native Gateway setup cancelled.");
                    _rows[_currentStep].Apply(SetupInstallationStatus.Cancelled);
                    StatusText.Text = SetupLocalization.GetString("Onboarding_Native_Cancelled");
                    RetryButton.Visibility = Visibility.Visible;
                }
                catch (Exception discardFailure) when (discardFailure is InvalidOperationException or IOException or
                                                       UnauthorizedAccessException or Win32Exception or COMException or
                                                       JsonException or TimeoutException or AggregateException)
                {
                    Trace.TraceError($"Native Gateway draft discard: {discardFailure}");
                    LogFailure(log, discardFailure);
                    _rows[_currentStep].Apply(SetupInstallationStatus.Failed);
                    StatusText.Text = SetupLogger.Sanitize(discardFailure.Message);
                    ShowRecovery(discardFailure);
                    RetryButton.Visibility = Visibility.Visible;
                }
            }
            else
            {
                _rows[_currentStep].Apply(SetupInstallationStatus.Failed);
                StatusText.Text = ex.Message;
                RetryButton.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            log.Info("Native Gateway setup cancelled.");
            _rows[_currentStep].Apply(SetupInstallationStatus.Cancelled);
            StatusText.Text = SetupLocalization.GetString("Onboarding_Native_Cancelled");
            RetryButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
                                   or Win32Exception or COMException or JsonException or TimeoutException or AggregateException)
        {
            Trace.TraceError($"Native Gateway setup: {ex}");
            LogFailure(log, ex);
            _rows[_currentStep].Apply(SetupInstallationStatus.Failed);
            StatusText.Text = SetupLogger.Sanitize(ex.Message);
            ShowRecovery(ex);
            RetryButton.Visibility = Visibility.Visible;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ConfigureAsync(SetupLogger log, CancellationToken cancellationToken)
    {
        SetCurrentStep(0);
        StatusText.Text = SetupLocalization.GetString("Onboarding_Native_CheckingSupport");
        var window = SetupWindow.Active ?? throw new InvalidOperationException("The setup window is closed.");
        var eligibility = await window.GetNativeGatewayEligibilityAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (eligibility != NativeGatewayEligibility.Available)
            throw new InvalidOperationException(NativeGatewayEligibilityText.Get(eligibility));
        SetCurrentStep(1);
        StatusText.Text = SetupLocalization.GetString("Onboarding_Native_Checking");
        var acquiredMissingPackage = false;
        await NativeGatewayPackageAcquisition.EnsureAsync(
            _resolver, async ct => { acquiredMissingPackage = await InstallAsync(log, ct); },
            () => StatusText.Text = SetupLocalization.GetString("Onboarding_Native_VerifyingPackage"),
            cancellationToken);
        if (acquiredMissingPackage)
            await GatewayPackageCleanup.RegisterAsync(new CommandRunner(log), ownNativePackage: true, ct: CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
        SetCurrentStep(2);
        var appLogger = new SetupOpenClawLogger(log);
        var registry = new GatewayRegistry(window.DataDir, logger: appLogger);
        var progressDispatcher = DispatcherQueue;
        void DispatchProgress(Action update)
        {
            if (!progressDispatcher.TryEnqueue(() =>
            {
                if (IsLoaded && !cancellationToken.IsCancellationRequested)
                    update();
            }))
                Trace.TraceWarning("The native setup progress update could not be dispatched.");
        }
        void ReportProgress(string message)
        {
            log.Info(message);
            DispatchProgress(() => StatusText.Text = message);
        }
        void ReportStage(NativeGatewaySetupStage stage) => DispatchProgress(() =>
        {
            if (stage is NativeGatewaySetupStage.StartingGateway or NativeGatewaySetupStage.VerifyingEndpoint)
                SetCurrentStep(3);
        });
        var service = _setupService = new NativeGatewaySetupService(
            registry,
            _resolver,
            new NativeGatewaySetupHost(ReportProgress, ReportStage, log),
            () => NativeGatewayRuntimeRouter.Create(registry, _resolver, appLogger));
        window.NativeSetupDraft = await service.CreateDraftAsync(cancellationToken);
        StatusText.Text = SetupLocalization.GetString("Onboarding_Native_InProgress");
        var session = await service.PrepareAsync(window.NativeSetupDraft, cancellationToken);
        if (window.IsClosed || cancellationToken.IsCancellationRequested)
        {
            await session.DisposeAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }
        SetCurrentStep(3);
        _rows[3].Apply(SetupInstallationStatus.Complete);
        window.NavigateToNativeAiSetup(session);
    }

    private async Task<bool> InstallAsync(SetupLogger log, CancellationToken cancellationToken)
    {
        StatusText.Text = SetupLocalization.GetString("Onboarding_Native_InstallingPackage");
        var commands = new CommandRunner(log);
        var missing = false;
        try { await _resolver.ResolveAsync(cancellationToken); }
        catch (NativeGatewayPackageNotInstalledException)
        {
            missing = true;
        }
        await _installer.InstallAsync(commands, cancellationToken);
        return missing;
    }

    private void SetCurrentStep(int index)
    {
        for (var i = 0; i < index; i++)
            _rows[i].Apply(SetupInstallationStatus.Complete);
        _currentStep = index;
        _rows[index].Apply(SetupInstallationStatus.Running);
        _log?.Info($"Native setup step: {StepKeys[index]}.");
    }

    private void SetBusy(bool busy)
    {
        BackButton.IsEnabled = !busy;
        RetryButton.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LogFailure(SetupLogger log, Exception ex)
    {
        var step = StepKeys[_currentStep];
        object data = ex is NativeGatewayStartupTimeoutException timeout
            ? new
            {
                step,
                phase = timeout.Phase.ToString(),
                budget_seconds = (int)timeout.Budget.TotalSeconds,
                last_status = timeout.LastStatus?.Describe(),
                exception = ex.ToString(),
            }
            : new { step, exception = ex.ToString() };
        log.Error($"Native Gateway setup failed during {step}: {ex.GetType().Name}: {ex.Message}", data);
    }

    private void HideRecovery()
    {
        RecoveryPanel.Visibility = Visibility.Collapsed;
        RecoveryResult.Text = "";
    }

    private void ShowRecovery(Exception ex)
    {
        bool packageActions = _currentStep >= PrepareStepIndex &&
            SetupWindow.Active?.NativeSetupDraft?.Contract == NativeGatewayContract.IsolatedSessionV1;
        var packageVisibility = packageActions ? Visibility.Visible : Visibility.Collapsed;
        CollectDiagnosticsButton.Visibility = packageVisibility;
        OpenTerminalButton.Visibility = packageVisibility;
        ViewLogButton.Visibility = _log?.FilePath is not null ? Visibility.Visible : Visibility.Collapsed;
        RecoveryGuidance.Text = ex is NativeGatewayStartupTimeoutException timeout
            ? SetupLocalization.Format("Onboarding_Native_StartTimeoutGuidance",
                (int)timeout.Budget.TotalSeconds, IsolatedGatewayRuntime.StartupTimeoutEnvironmentVariable)
            : packageActions ? SetupLocalization.GetString("Onboarding_Native_RecoveryGuidance") : "";
        RecoveryGuidance.Visibility = RecoveryGuidance.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecoveryResult.Text = "";
        RecoveryPanel.Visibility = packageActions || ViewLogButton.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async Task<NativeGatewayPackage> ResolveRecoveryPackageAsync(CancellationToken cancellationToken)
    {
        var draft = SetupWindow.Active?.NativeSetupDraft
            ?? throw new InvalidOperationException("Native Gateway setup has no saved profile. Retry setup.");
        var package = await _resolver.ResolveAsync(draft.PackageFamilyName, cancellationToken);
        // The recovery actions are offered only for an isolated-session draft.
        return package with { Contract = NativeGatewayContract.IsolatedSessionV1 };
    }

    private void SetRecoveryBusy(bool busy)
    {
        CollectDiagnosticsButton.IsEnabled = !busy;
        OpenTerminalButton.IsEnabled = !busy;
        RetryButton.IsEnabled = !busy;
        BackButton.IsEnabled = !busy;
    }

    private void CollectDiagnostics_Click(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(CollectDiagnosticsAsync, onError: error =>
            Trace.TraceWarning($"Native Gateway diagnostics failed ({error.GetType().Name})."));

    private async Task CollectDiagnosticsAsync()
    {
        var token = _recoveryCts.Token;
        SetRecoveryBusy(true);
        RecoveryResult.Text = SetupLocalization.GetString("Onboarding_Native_CollectingDiagnostics");
        try
        {
            var package = await ResolveRecoveryPackageAsync(token);
            var bundle = await _recoveryHost.CollectDiagnosticsAsync(package, token);
            _log?.Info("Gateway diagnostics collected.",
                new { path = bundle.Path, included = bundle.Included, notes = bundle.Notes });
            if (!IsLoaded)
                return;
            if (string.IsNullOrWhiteSpace(bundle.Path))
            {
                RecoveryResult.Text = SetupLocalization.GetString("Onboarding_Native_DiagnosticsNoPath");
                return;
            }
            RecoveryResult.Text = SetupLocalization.Format("Onboarding_Native_DiagnosticsSaved", bundle.Path);
            LogFileLauncher.RevealInExplorer(bundle.Path);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
                                   or Win32Exception or COMException or TimeoutException)
        {
            _log?.Warn($"Gateway diagnostics collection failed: {ex.GetType().Name}: {ex.Message}");
            if (IsLoaded)
                RecoveryResult.Text = SetupLocalization.Format("Onboarding_Native_DiagnosticsFailed",
                    SetupLogger.Sanitize(ex.Message));
        }
        finally
        {
            if (IsLoaded)
                SetRecoveryBusy(false);
        }
    }

    private void OpenTerminal_Click(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(OpenTerminalAsync, onError: error =>
            Trace.TraceWarning($"Native Gateway terminal failed ({error.GetType().Name})."));

    private async Task OpenTerminalAsync()
    {
        var token = _recoveryCts.Token;
        try
        {
            var package = await ResolveRecoveryPackageAsync(token);
            token.ThrowIfCancellationRequested();
            _recoveryTerminal?.Dispose();
            _recoveryTerminal = _recoveryHost.OpenRecoveryTerminal(package, new Dictionary<string, string>());
            _log?.Info("Opened the isolated Gateway terminal.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException
                                   or UnauthorizedAccessException)
        {
            _log?.Warn($"Gateway terminal could not be opened: {ex.GetType().Name}: {ex.Message}");
            if (IsLoaded)
                RecoveryResult.Text = SetupLocalization.Format("Onboarding_Native_TerminalFailed",
                    SetupLogger.Sanitize(ex.Message));
        }
    }

    private void ViewLog_Click(object sender, RoutedEventArgs e) => LogFileLauncher.RevealInExplorer(_log?.FilePath);

    private void ReleaseRecoveryResources()
    {
        _recoveryCts.Cancel();
        _recoveryCts.Dispose();
        _recoveryCts = new CancellationTokenSource();
        // Matches the setup session, which closes its recovery terminal on dispose.
        _recoveryTerminal?.Dispose();
        _recoveryTerminal = null;
        _log?.Dispose();
        _log = null;
    }

    internal async Task CancelAndWaitAsync()
    {
        _operationCts?.Cancel();
        if (_operation is { } operation)
            await operation;
    }

    private void Retry_Click(object sender, RoutedEventArgs e) => StartOperation();
    private void Cancel_Click(object sender, RoutedEventArgs e) => _operationCts?.Cancel();
    private void Back_Click(object sender, RoutedEventArgs e) => SetupWindow.Active?.NavigateToNativeCapabilities();
}
