using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Connection;
using OpenClaw.Connection.NativeGateway;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class NativeGatewaySetupPage : Page
{
    private readonly NativeGatewayPackageResolver _resolver = new();
    private readonly NativeGatewayMsixInstaller _installer = new();
    private CancellationTokenSource? _operationCts;
    private Task? _operation;
    private readonly List<StepRow> _rows = [];
    private int _currentStep;
    internal bool IsBusy => _operation is { IsCompleted: false };

    public NativeGatewaySetupPage()
    {
        InitializeComponent();
        foreach (var key in new[] { "StepSupport", "StepPackage", "StepPrepare", "StepVerify" })
        {
            var row = new StepRow(SetupLocalization.GetString($"Onboarding_Native_{key}"));
            AutomationProperties.SetAutomationId(row.Element, $"NativeGateway{key}");
            _rows.Add(row);
            StepsPanel.Children.Add(row.Element);
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
        foreach (var row in _rows)
            row.SetStatus(StepStatus.Idle);
        _currentStep = 0;
        RetryButton.Visibility = Visibility.Collapsed;
        SetBusy(true);
        try
        {
            await ConfigureAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _rows[_currentStep].SetStatus(StepStatus.Idle);
            StatusText.Text = SetupLocalization.GetString("Onboarding_Native_Cancelled");
            RetryButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
                                   or Win32Exception or COMException or JsonException or TimeoutException or AggregateException)
        {
            Trace.TraceError($"Native Gateway setup: {ex}");
            _rows[_currentStep].SetStatus(StepStatus.Failed);
            StatusText.Text = SetupLogger.Sanitize(ex.Message);
            RetryButton.Visibility = Visibility.Visible;
        }
        finally
        {
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
            () => StatusText.Text = SetupLocalization.GetString("Onboarding_Native_InstallerOpened"),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        SetCurrentStep(2);
        using var logger = new SetupLogger(filePath: null);
        var appLogger = new SetupOpenClawLogger(logger);
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
        void ReportProgress(string message) => DispatchProgress(() => StatusText.Text = message);
        void ReportStage(NativeGatewaySetupStage stage) => DispatchProgress(() =>
        {
            if (stage is NativeGatewaySetupStage.StartingGateway or NativeGatewaySetupStage.VerifyingEndpoint)
                SetCurrentStep(3);
        });
        var service = new NativeGatewaySetupService(
            registry,
            _resolver,
            new NativeGatewaySetupHost(ReportProgress, ReportStage),
            () => new NativeGatewayRuntime(registry, _resolver, appLogger));
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
        _rows[3].SetStatus(StepStatus.Done);
        window.NavigateToNativeWizard(session);
    }

    private async Task InstallAsync(CancellationToken cancellationToken)
    {
        await _installer.OpenAsync(async (uri, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return await Windows.System.Launcher.LaunchUriAsync(uri);
        }, cancellationToken);
    }

    private void SetCurrentStep(int index)
    {
        for (var i = 0; i < index; i++)
            _rows[i].SetStatus(StepStatus.Done);
        _currentStep = index;
        _rows[index].SetStatus(StepStatus.Running);
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
