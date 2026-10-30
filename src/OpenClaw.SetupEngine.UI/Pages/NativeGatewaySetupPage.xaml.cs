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
    private const int MaxLogLines = 300;
    private readonly NativeGatewayPackageResolver _resolver = new();
    private readonly NativeGatewayMsixInstaller _installer = new();
    private readonly List<SetupPhaseStatus> _rows = [];
    private readonly List<string> _stepHeaders = [];
    private readonly Queue<string> _logLines = new();
    private SetupLogger? _log;
    private int _loggedStep = -1;
    private CancellationTokenSource? _operationCts;
    private CancellationTokenSource _recoveryCts = new();
    private Task? _operation;
    private NativeGatewaySetupService? _setupService;
    private IDisposable? _recoveryTerminal;
    private int _currentStep;
    internal bool IsBusy => _operation is { IsCompleted: false };


    public NativeGatewaySetupPage()
    {
        InitializeComponent();
        foreach (var key in StepKeys)
        {
            var status = new SetupPhaseStatus();
            status.Apply(SetupInstallationStatus.Pending);
            var header = SetupLocalization.GetString($"Onboarding_Native_{key}");
            var row = new SettingsCard
            {
                Header = header,
                Content = status,
            };
            AutomationProperties.SetAutomationId(row, $"NativeGateway{key}");
            _rows.Add(status);
            _stepHeaders.Add(header);
            StepsPanel.Children.Add(row);
        }
        Loaded += (_, _) => StartOperation();
        Unloaded += (_, _) => _operationCts?.Cancel();
        Unloaded += (_, _) => ReleaseRecoveryResources();
        Unloaded += (_, _) => DetachLog();
    }

    private void AttachLog(SetupLogger? log)
    {
        if (_log is not null)
            _log.LogEmitted -= OnLogEmitted;
        _log = log;
        if (log is not null)
        {
            // Idempotent subscribe: a double attach must not duplicate lines.
            log.LogEmitted -= OnLogEmitted;
            log.LogEmitted += OnLogEmitted;
        }
        OpenLogButton.Visibility = log?.FilePath is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void DetachLog() => _log?.LogEmitted -= OnLogEmitted;

    private void OnLogEmitted(object? sender, LogEntry entry)
    {
        var line = $"[{entry.Timestamp.ToLocalTime():HH:mm:ss}] [{entry.Level}] {entry.Message}";
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded)
                AppendLogLine(line);
        });
    }

    private void AppendLogLine(string line)
    {
        _logLines.Enqueue(line);
        while (_logLines.Count > MaxLogLines)
            _logLines.Dequeue();
        LogText.Text = string.Join('\n', _logLines);
        LogScroller.ChangeView(null, LogScroller.ScrollableHeight, null);
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e) => LogFileLauncher.RevealInExplorer(_log?.FilePath);


    private void StartOperation()
    {
        if (IsBusy)
            return;
        if (SetupPreview.IsActive)
        {
            RenderPreview();
            return;
        }
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        _operation = RunOperationAsync(_operationCts.Token);
    }

    private void RenderPreview()
    {
        var failed = SetupPreview.RequestedPage == "native-failed";
        if (failed)
        {
            SetCurrentStep(3);
            ShowFailure(new NativeGatewayStartupTimeoutException(
                NativeGatewayStartupTimeoutPhase.Readiness,
                TimeSpan.FromSeconds(180),
                new IsolatedGatewayStatus("starting", null, null, [], "running", "not-ready")));
        }
        StatusText.Text = SetupLocalization.GetString("Onboarding_Native_Preview");
        if (!failed)
        {
            foreach (var key in new[]
            {
                "[12:04:01] [Info] step.started: Check device support",
                "[12:04:01] [Info] Device support: Available.",
                "[12:04:02] [Info] Gateway package OpenClaw.Gateway_123456789abcd 2026.10.1.0 is registered.",
                "[12:04:03] [Debug] clawctl setup --json exited 0 in 2140 ms",
                "[12:04:06] [Info] Native Gateway start requested on port 19001 (budget 180s).",
                "[12:04:07] [Info] Native Gateway startup status: gateway starting, session running, readiness not-ready.",
            })
                AppendLogLine(key);
        }
    }

    private async Task RunOperationAsync(CancellationToken cancellationToken)
    {
        AttachLog(SetupWindow.Active?.NativeSetupLog);
        FailureBar.IsOpen = false;
        _loggedStep = -1;
        _log?.Info("Native Gateway setup started.");
        foreach (var row in _rows)
            row.Apply(SetupInstallationStatus.Pending);
        _currentStep = 0;
        RetryButton.Visibility = Visibility.Collapsed;
        HideRecovery();
        SetBusy(true);
        try
        {
            await ConfigureAsync(cancellationToken);
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
                    await ConfigureAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _log?.Warn("Native Gateway setup was cancelled.");
                    _rows[_currentStep].Apply(SetupInstallationStatus.Cancelled);
                    StatusText.Text = SetupLocalization.GetString("Onboarding_Native_Cancelled");
                    RetryButton.Visibility = Visibility.Visible;
                }
                catch (OperationCanceledException connectionFailure) when (!cancellationToken.IsCancellationRequested)
                {
                    Trace.TraceError($"Native Gateway draft retry lost its operation: {connectionFailure}");
                    _rows[_currentStep].Apply(SetupInstallationStatus.Failed);
                    StatusText.Text = SetupLogger.Sanitize(connectionFailure.Message);
                    RetryButton.Visibility = Visibility.Visible;
                }
                catch (Exception discardFailure) when (discardFailure is InvalidOperationException or IOException or
                                                       UnauthorizedAccessException or Win32Exception or COMException or
                                                       JsonException or InvalidDataException or TimeoutException or AggregateException)
                {
                    Trace.TraceError($"Native Gateway draft discard: {discardFailure}");
                    ShowFailure(discardFailure);
                }
            }
            else
            {
                ShowFailure(ex);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _log?.Warn("Native Gateway setup was cancelled.");
            _rows[_currentStep].Apply(SetupInstallationStatus.Cancelled);
            StatusText.Text = SetupLocalization.GetString("Onboarding_Native_Cancelled");
            RetryButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException connectionFailure) when (!cancellationToken.IsCancellationRequested)
        {
            Trace.TraceError($"Native Gateway setup lost its operation: {connectionFailure}");
            _rows[_currentStep].Apply(SetupInstallationStatus.Failed);
            StatusText.Text = SetupLogger.Sanitize(connectionFailure.Message);
            RetryButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
                                   or Win32Exception or COMException or JsonException or InvalidDataException or TimeoutException or AggregateException)
        {
            Trace.TraceError($"Native Gateway setup: {ex}");
            ShowFailure(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowFailure(Exception error)
    {
        var step = _currentStep;
        _rows[step].Apply(SetupInstallationStatus.Failed);
        var message = SetupLogger.Sanitize(error.Message);
        var details = NativeGatewaySetupDiagnostics.Describe(error);
        LogFailure(error);
        foreach (var line in details)
            _log?.Error($"diagnosis: {line}");
        StatusText.Text = "";
        FailureBar.Title = SetupLocalization.Format("Onboarding_Native_FailedStep", _stepHeaders[step]);
        FailureBar.Message = message;
        FailureDetails.Text = string.Join(Environment.NewLine, details);
        FailureDetails.Visibility = details.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        FailureBar.IsOpen = true;
        ActivityExpander.IsExpanded = true;
        ShowRecovery(error);
        RetryButton.Visibility = Visibility.Visible;
    }

    private async Task ConfigureAsync(CancellationToken cancellationToken)
    {
        SetCurrentStep(0);
        StatusText.Text = SetupLocalization.GetString("Onboarding_Native_CheckingSupport");
        var window = SetupWindow.Active ?? throw new InvalidOperationException("The setup window is closed.");
        var eligibility = await window.GetNativeGatewayEligibilityAsync();
        cancellationToken.ThrowIfCancellationRequested();
        _log?.Info($"Device support: {eligibility}.");
        if (eligibility != NativeGatewayEligibility.Available)
            throw new InvalidOperationException(NativeGatewayEligibilityText.Get(eligibility));
        SetCurrentStep(1);
        StatusText.Text = SetupLocalization.GetString("Onboarding_Native_Checking");
        var acquiredMissingPackage = false;
        var package = await NativeGatewayPackageAcquisition.EnsureAsync(
            _resolver, async ct => { acquiredMissingPackage = await InstallAsync(ct); },
            () =>
            {
                _log?.Info("WinGet finished. Waiting for the Gateway package registration.");
                StatusText.Text = SetupLocalization.GetString("Onboarding_Native_VerifyingPackage");
            },
            cancellationToken);
        _log?.Info($"Gateway package {package.PackageFamilyName} {package.Version} is registered.");
        var log = _log ?? throw new InvalidOperationException("The native setup log is unavailable.");
        if (acquiredMissingPackage)
            await GatewayPackageCleanup.RegisterAsync(new CommandRunner(log), ownNativePackage: true, ct: CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
        SetCurrentStep(2);
        var appLogger = new SetupOpenClawLogger(log, prefix: "");
        var registry = new GatewayRegistry(window.DataDir, logger: appLogger);
        void DispatchProgress(Action update)
        {
            void Apply()
            {
                if (IsLoaded && IsBusy && !cancellationToken.IsCancellationRequested &&
                    _operationCts?.Token == cancellationToken)
                    update();
            }
            if (DispatcherQueue.HasThreadAccess) Apply();
            else if (!DispatcherQueue.TryEnqueue(Apply))
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
        var progress = new Progress<SetupLoadingStep>(step => ReportProgress(SetupLoadingView.DetailFor(step)));
        var service = _setupService = new NativeGatewaySetupService(
            registry,
            _resolver,
            new NativeGatewaySetupHost(log, ReportProgress, ReportStage),
            () => NativeGatewayRuntimeRouter.Create(registry, _resolver, appLogger));
        window.NativeSetupDraft = await service.CreateDraftAsync(cancellationToken);
        log.Info($"Using a setup profile on loopback port {window.NativeSetupDraft.Port} ({window.NativeSetupDraft.Contract} runtime).");
        StatusText.Text = SetupLocalization.GetString("Onboarding_Native_InProgress");
        var session = await service.PrepareAsync(window.NativeSetupDraft, cancellationToken);
        if (window.IsClosed || cancellationToken.IsCancellationRequested)
        {
            await session.DisposeAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }
        SetCurrentStep(3);
        try
        {
            StatusText.Text = SetupLocalization.GetString("Onboarding_AiSetup_Preparing");
            await using var preparation = await GatewayAiPreparation.PrepareNativeAsync(session, cancellationToken, progress);
            cancellationToken.ThrowIfCancellationRequested();
            if (window.IsClosed)
            {
                await session.DisposeAsync();
                return;
            }
            await CompleteAiPreparationAsync(preparation, window.LocalAiHost,
                session.IsIsolated ? session.Record : null, session.AuthorizeAsync, session.LifetimeToken,
                prepared =>
                {
                    if (window.IsClosed) throw new OperationCanceledException(cancellationToken);
                    window.NavigateToNativeAiSetup(session, prepared);
                }, cancellationToken);
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    internal async Task CompleteAiPreparationAsync(
        GatewayAiPreparation gateway, ISetupLocalAiHost? host, GatewayRecord? isolatedRecord,
        Func<CancellationToken, Task>? authorize, CancellationToken lifetimeToken,
        Action<NativeAiPreparation> navigate, CancellationToken cancellationToken)
    {
        SetCurrentStep(3);
        await using var preparation = await NativeAiPreparation.PrepareAsync(
            gateway, host, isolatedRecord, authorize, lifetimeToken, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _rows[3].Apply(SetupInstallationStatus.Complete);
        navigate(preparation);
    }

    private async Task<bool> InstallAsync(CancellationToken cancellationToken)
    {
        StatusText.Text = SetupLocalization.GetString("Onboarding_Native_InstallingPackage");
        _log?.Info("Installing the Gateway package from Microsoft Store using WinGet.");
        var commands = new CommandRunner(_log ?? throw new InvalidOperationException("The native setup log is unavailable."));
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
        if (_loggedStep != index)
        {
            _loggedStep = index;
            _log?.StepStarted(StepKeys[index], _stepHeaders[index]);
        }
    }

    private void SetBusy(bool busy)
    {
        BackButton.IsEnabled = !busy;
        RetryButton.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LogFailure(Exception ex)
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
        _log?.Error($"Native Gateway setup failed during {step}: {ex.GetType().Name}: {ex.Message}", data);
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
            var bundle = await RecoveryHost().CollectDiagnosticsAsync(package, token);
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
            _recoveryTerminal = RecoveryHost().OpenRecoveryTerminal(package, new Dictionary<string, string>());
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

    private NativeGatewaySetupHost RecoveryHost() => new(_log ?? new SetupLogger(filePath: null));

    private void ReleaseRecoveryResources()
    {
        _recoveryCts.Cancel();
        _recoveryCts.Dispose();
        _recoveryCts = new CancellationTokenSource();
        // Matches the setup session, which closes its recovery terminal on dispose.
        _recoveryTerminal?.Dispose();
        _recoveryTerminal = null;
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
