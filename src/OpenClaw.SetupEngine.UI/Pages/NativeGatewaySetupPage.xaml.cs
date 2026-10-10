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

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class NativeGatewaySetupPage : Page
{
    private readonly NativeGatewayPackageResolver _resolver = new();
    private readonly NativeGatewayMsixInstaller _installer = new();
    private CancellationTokenSource? _operationCts;
    private Task? _operation;
    private NativeGatewaySetupService? _setupService;
    private readonly List<SetupPhaseStatus> _rows = [];
    private int _currentStep;
    private SetupLoadingProgress.Scope? _loading;
    internal bool IsBusy => _operation is { IsCompleted: false };

    public NativeGatewaySetupPage()
    {
        InitializeComponent();
        foreach (var key in new[] { "StepSupport", "StepPackage", "StepPrepare", "StepVerify" })
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
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        _operation = RunOperationAsync(_operationCts.Token);
    }

    private async Task RunOperationAsync(CancellationToken cancellationToken)
    {
        _loading = SetupWindow.Active?.BeginLoading(SetupLoadingGroup.GatewayPreparation, SetupLoadingStep.CheckGatewaySupport);
        if (_loading is { } loading)
            SetupWindow.Active?.SetLoadingCancellation(loading, () => _operationCts?.Cancel());
        foreach (var row in _rows)
            row.Apply(SetupInstallationStatus.Pending);
        _currentStep = 0;
        RetryButton.Visibility = Visibility.Collapsed;
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
                    _rows[_currentStep].Apply(SetupInstallationStatus.Failed);
                    StatusText.Text = SetupLogger.Sanitize(discardFailure.Message);
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
            _rows[_currentStep].Apply(SetupInstallationStatus.Failed);
            StatusText.Text = SetupLogger.Sanitize(ex.Message);
            RetryButton.Visibility = Visibility.Visible;
        }
        finally
        {
            _loading?.Dispose();
            SetBusy(false);
        }
    }

    private async Task ConfigureAsync(CancellationToken cancellationToken)
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
        await NativeGatewayPackageAcquisition.EnsureAsync(
            _resolver, InstallAsync,
            () => StatusText.Text = SetupLocalization.GetString("Onboarding_Native_VerifyingPackage"),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        SetCurrentStep(2);
        using var logger = new SetupLogger(filePath: null);
        var appLogger = new SetupOpenClawLogger(logger);
        var registry = new GatewayRegistry(window.DataDir, logger: appLogger);
        var loading = _loading;
        var service = _setupService = new NativeGatewaySetupService(
            registry,
            _resolver,
            new NativeGatewaySetupHost(stageProgress: stage => loading?.Report(
                stage == NativeGatewaySetupStage.StartingGateway ? SetupLoadingStep.StartGateway : SetupLoadingStep.ConnectGateway)),
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
        try
        {
            StatusText.Text = SetupLocalization.GetString("Onboarding_AiSetup_Preparing");
            await using var preparation = await GatewayAiPreparation.PrepareNativeAsync(session, cancellationToken, loading);
            cancellationToken.ThrowIfCancellationRequested();
            if (window.IsClosed)
            {
                await session.DisposeAsync();
                return;
            }
            _rows[3].Apply(SetupInstallationStatus.Complete);
            window.NavigateToNativeAiSetup(session, preparation);
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private async Task InstallAsync(CancellationToken cancellationToken)
    {
        _loading?.Report(SetupLoadingStep.InstallGatewayPackage);
        StatusText.Text = SetupLocalization.GetString("Onboarding_Native_InstallingPackage");
        using var logger = new SetupLogger(filePath: null);
        await _installer.InstallAsync(new CommandRunner(logger), cancellationToken);
    }

    private void SetCurrentStep(int index)
    {
        _loading?.Report(index switch
        {
            0 => SetupLoadingStep.CheckGatewaySupport,
            1 => SetupLoadingStep.CheckGatewayPackage,
            2 => SetupLoadingStep.PrepareGateway,
            _ => SetupLoadingStep.ConnectGateway
        });
        for (var i = 0; i < index; i++)
            _rows[i].Apply(SetupInstallationStatus.Complete);
        _currentStep = index;
        _rows[index].Apply(SetupInstallationStatus.Running);
    }

    private void SetBusy(bool busy)
    {
        BackButton.IsEnabled = !busy;
        RetryButton.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
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
