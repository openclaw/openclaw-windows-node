using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClaw.Shared.Inference.Catalog;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using System.Globalization;

namespace OpenClawTray.Pages;

public sealed partial class LocalAiPage : Page
{
    private const double BytesPerGigabyte = 1024d * 1024 * 1024;
    private LocalAiPageViewModel? _viewModel;
    private IReadOnlyList<LocalAiModelOption>? _renderedModelOptions;
    private bool _suppressModelSelection;

    public LocalAiPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = args.NewValue as LocalAiPageViewModel;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        RefreshFromViewModel();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LocalAiPageViewModel.SwitchProgress) && _viewModel is { } viewModel)
            RefreshSwitchProgress(viewModel);
        else
            RefreshFromViewModel();
    }

    private void RefreshFromViewModel()
    {
        if (_viewModel is null)
            return;
        LocalAiProfileSelectionText.Text = _viewModel.ProfileRecommendation is { } recommendation
            ? LocalizationHelper.Format("LocalAi_ProfileFallback", recommendation.PreferredModelName,
                recommendation.SelectedModelName, recommendation.ContextTokens)
            : string.Empty;
        LocalAiProfileSelectionText.Visibility = _viewModel.ProfileRecommendation is null
            ? Visibility.Collapsed : Visibility.Visible;
        EngineStatusText.Text = LocalizationHelper.GetString(_viewModel.EngineStatusResourceKey);
        EngineOwnershipText.Text = LocalizationHelper.GetString(_viewModel.EngineOwnershipResourceKey);
        EngineVersionText.Text = _viewModel.EngineVersion ?? LocalizationHelper.GetString("LocalAiPage_Value_Unknown");
        EndpointText.Text = _viewModel.Endpoint;
        ProcessIdText.Text = _viewModel.ProcessId ?? LocalizationHelper.GetString("LocalAiPage_Process_NotRunning");
        EngineDetailText.Text = _viewModel.EngineDetail ?? string.Empty;
        EngineDetailText.Visibility = string.IsNullOrWhiteSpace(EngineDetailText.Text) ? Visibility.Collapsed : Visibility.Visible;
        EngineStatusDot.Fill = EngineStatusText.Foreground = ResolveBrush(_viewModel.EngineState switch
        {
            LocalAiEnginePresentationState.Running => "SystemFillColorSuccessBrush",
            LocalAiEnginePresentationState.Starting => "SystemFillColorCautionBrush",
            LocalAiEnginePresentationState.Error => "SystemFillColorCriticalBrush",
            _ => "SystemFillColorNeutralBrush",
        });

        ModelStatusText.Text = LocalizationHelper.GetString(_viewModel.ModelStatusResourceKey);
        ModelNameText.Text = _viewModel.ModelName ?? LocalizationHelper.GetString("LocalAiPage_Value_Unknown");
        ModelRecipeText.Text = string.Format(
            CultureInfo.CurrentCulture,
            LocalizationHelper.GetString("LocalAiPage_ModelRecipeFormat"),
            _viewModel.ContextLengthText,
            _viewModel.KvCacheText);
        ModelStatusDot.Fill = ModelStatusText.Foreground = ResolveBrush(_viewModel.ModelState switch
        {
            LocalAiModelPresentationState.Loaded => "SystemFillColorSuccessBrush",
            LocalAiModelPresentationState.Verified => "SystemFillColorSuccessBrush",
            LocalAiModelPresentationState.NotInstalled => "SystemFillColorCriticalBrush",
            _ => "SystemFillColorCautionBrush",
        });

        GatewayStatusText.Text = LocalizationHelper.GetString(_viewModel.GatewayStatusResourceKey);
        GatewayDetailText.Text = _viewModel.GatewayDetail ?? string.Empty;
        GatewayDetailText.Visibility = string.IsNullOrWhiteSpace(GatewayDetailText.Text) ? Visibility.Collapsed : Visibility.Visible;
        GatewayStatusDot.Fill = GatewayStatusText.Foreground = ResolveBrush(_viewModel.GatewayState switch
        {
            LocalAiGatewayPresentationState.Connected => "SystemFillColorSuccessBrush",
            LocalAiGatewayPresentationState.Connecting or LocalAiGatewayPresentationState.NeedsAttention => "SystemFillColorCautionBrush",
            LocalAiGatewayPresentationState.Error => "SystemFillColorCriticalBrush",
            _ => "SystemFillColorNeutralBrush",
        });

        ActionErrorText.Text = _viewModel.ActionError ?? string.Empty;
        ActionErrorText.Visibility = string.IsNullOrWhiteSpace(ActionErrorText.Text) ? Visibility.Collapsed : Visibility.Visible;
        NativeSelectionGuidanceText.Text = LocalizationHelper.GetString("LocalAiPage_NativeSelectionRequired");
        NativeSelectionGuidanceText.Visibility = _viewModel.RequiresNativeSelection ? Visibility.Visible : Visibility.Collapsed;
        EngineBusyIndicator.IsActive = _viewModel.IsBusy;
        EngineBusyIndicator.Visibility = _viewModel.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        LocalAiUnavailableInfoBar.Visibility =
            _viewModel.ShowAvailabilityInfoBar
                ? Visibility.Visible
                : Visibility.Collapsed;
        LocalAiAvailabilityProgressRing.IsActive = _viewModel.IsCheckingAvailability;
        LocalAiAvailabilityProgressRing.Visibility = _viewModel.IsCheckingAvailability
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_viewModel.IsCheckingAvailability)
        {
            LocalAiUnavailableInfoBar.Title = LocalizationHelper.GetString("LocalAiPage_CheckingTitle");
            LocalAiUnavailableInfoBar.Severity = InfoBarSeverity.Informational;
            LocalAiUnavailableInfoBar.Message = LocalizationHelper.GetString("LocalAiPage_CheckingMessage");
        }
        else
        {
            LocalAiUnavailableInfoBar.Title = LocalizationHelper.GetString(
                _viewModel.HasAvailabilityProbeError
                    ? "LocalAiPage_UnavailableProbeTitle"
                    : "LocalAiPage_UnavailableInfoBar.Title");
            LocalAiUnavailableInfoBar.Severity = _viewModel.HasAvailabilityProbeError
                ? InfoBarSeverity.Warning
                : InfoBarSeverity.Informational;
            LocalAiUnavailableInfoBar.Message = LocalizationHelper.GetString(
                _viewModel.HasAvailabilityProbeError
                    ? "LocalAiPage_UnavailableProbeMessage"
                    : "LocalAiPage_UnavailableRequirementsMessage");
        }
        LocalAiRecheckAvailabilityButton.Visibility = _viewModel.CanRecheckAvailability
            ? Visibility.Visible
            : Visibility.Collapsed;
        LocalAiRecheckAvailabilityButton.IsEnabled = _viewModel.CanRecheckAvailability;
        StartButton.IsEnabled = _viewModel.CanStart;
        StopButton.IsEnabled = _viewModel.CanStop;
        ReleaseOwnershipButton.Visibility = _viewModel.ShowReleaseOwnership ? Visibility.Visible : Visibility.Collapsed;
        ReleaseOwnershipButton.IsEnabled = _viewModel.CanReleaseOwnership;
        RestartButton.IsEnabled = _viewModel.CanRestart;
        OpenLogsButton.IsEnabled = _viewModel.CanOpenLogs;
        RetrySetupButton.IsEnabled = _viewModel.CanRetrySetup;
        RetrySetupButton.Visibility = _viewModel.CanRetrySetup ? Visibility.Visible : Visibility.Collapsed;
        ChangeModelPanel.Visibility = _viewModel.HasInstalledModel ? Visibility.Visible : Visibility.Collapsed;
        RefreshModelSelection(_viewModel);
        RepairConnectionButton.IsEnabled = _viewModel.CanRepairConnection;
        OpenChatButton.IsEnabled = _viewModel.CanOpenChat;
    }

    private void OnStart(object sender, RoutedEventArgs e) => RunAction(() => _viewModel?.StartAsync() ?? Task.FromResult(false), nameof(OnStart));
    private void OnStop(object sender, RoutedEventArgs e) => RunAction(() => _viewModel?.StopAsync() ?? Task.FromResult(false), nameof(OnStop));
    private void OnReleaseOwnership(object sender, RoutedEventArgs e) => RunAction(() => _viewModel?.ReleaseOwnershipAsync() ?? Task.FromResult(false), nameof(OnReleaseOwnership));
    private void OnRestart(object sender, RoutedEventArgs e) => RunAction(() => _viewModel?.RestartAsync() ?? Task.FromResult(false), nameof(OnRestart));
    private void OnOpenLogs(object sender, RoutedEventArgs e) => _viewModel?.OpenLogs();
    private void OnRetrySetup(object sender, RoutedEventArgs e) => _viewModel?.RetrySetup();
    private void OnRepairConnection(object sender, RoutedEventArgs e) => _viewModel?.RepairConnection();
    private void OnOpenChat(object sender, RoutedEventArgs e) => _viewModel?.OpenChat();

    private void RefreshModelSelection(LocalAiPageViewModel viewModel)
    {
        if (!ReferenceEquals(_renderedModelOptions, viewModel.ModelOptions))
        {
            _renderedModelOptions = viewModel.ModelOptions;
            _suppressModelSelection = true;
            LocalAiModelSelector.Items.Clear();
            foreach (LocalAiModelOption option in viewModel.ModelOptions)
                LocalAiModelSelector.Items.Add(new ComboBoxItem { Tag = option.ModelId, Content = FormatModelOption(option) });
            _suppressModelSelection = false;
        }
        _suppressModelSelection = true;
        LocalAiModelSelector.SelectedItem = LocalAiModelSelector.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, viewModel.SelectedModelOptionId, StringComparison.OrdinalIgnoreCase));
        _suppressModelSelection = false;
        LocalAiModelSelector.IsEnabled = !viewModel.IsBusy && !viewModel.IsCustomModelActive;

        if (LocalAiCustomModelBox.FocusState == FocusState.Unfocused &&
            !string.Equals(LocalAiCustomModelBox.Text, viewModel.CustomModelReference, StringComparison.Ordinal))
            LocalAiCustomModelBox.Text = viewModel.CustomModelReference;
        LocalAiCustomModelBox.IsEnabled = !viewModel.IsBusy;

        if (viewModel.FitWarning is { } warning)
        {
            LocalAiFitWarning.Message = LocalizationHelper.Format("LocalAiPage_FitWarningFormat",
                warning.RequiredMemoryBytes / BytesPerGigabyte, warning.GpuName,
                (warning.DetectedMemoryBytes ?? 0) / BytesPerGigabyte);
            LocalAiFitWarning.IsOpen = true;
        }
        else
        {
            LocalAiFitWarning.IsOpen = false;
        }
        ForceApplyModelButton.IsEnabled = !viewModel.IsBusy;

        RefreshSwitchProgress(viewModel);

        if (viewModel.SwitchError is { } error)
        {
            ModelSwitchStatusText.Text = error;
            ModelSwitchStatusText.Foreground = ResolveBrush("SystemFillColorCriticalBrush");
            ModelSwitchStatusText.Visibility = Visibility.Visible;
        }
        else if (viewModel.SwitchCanceled)
        {
            ModelSwitchStatusText.Text = LocalizationHelper.GetString("LocalAiPage_SwitchCanceled");
            ModelSwitchStatusText.ClearValue(TextBlock.ForegroundProperty);
            ModelSwitchStatusText.Visibility = Visibility.Visible;
        }
        else
        {
            ModelSwitchStatusText.Visibility = Visibility.Collapsed;
        }

        ApplyModelButton.IsEnabled = viewModel.CanApplyModel;
        CancelModelSwitchButton.Visibility = viewModel.IsSwitchingModel ? Visibility.Visible : Visibility.Collapsed;
        CancelModelSwitchButton.IsEnabled = viewModel.CanCancelModelSwitch;
    }

    private void RefreshSwitchProgress(LocalAiPageViewModel viewModel)
    {
        if (viewModel.SwitchProgress is { } progress)
        {
            string phase = LocalizationHelper.GetString($"LocalAiPage_SwitchPhase_{progress.Phase}");
            ModelSwitchProgressText.Text = progress.TotalBytes > 0
                ? $"{phase} {LocalizationHelper.Format("LocalAiPage_SwitchBytesFormat", progress.CompletedBytes / BytesPerGigabyte, progress.TotalBytes / BytesPerGigabyte)}"
                : phase;
            ModelSwitchProgressBar.IsIndeterminate = progress.TotalBytes <= 0;
            ModelSwitchProgressBar.Value = progress.TotalBytes > 0 ? (double)progress.CompletedBytes / progress.TotalBytes : 0;
            ModelSwitchProgressPanel.Visibility = Visibility.Visible;
        }
        else if (viewModel.IsSwitchingModel)
        {
            ModelSwitchProgressText.Text = LocalizationHelper.GetString("LocalAiPage_SwitchPhase_Resolving");
            ModelSwitchProgressBar.IsIndeterminate = true;
            ModelSwitchProgressPanel.Visibility = Visibility.Visible;
        }
        else
        {
            ModelSwitchProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    private static string FormatModelOption(LocalAiModelOption option)
    {
        string label = LocalizationHelper.Format("LocalAiPage_ModelOptionFormat",
            option.DisplayName,
            $"{option.DownloadBytes / BytesPerGigabyte:F1} GB",
            FormatContext(option.ContextTokens),
            LocalModelCatalog.ToDisplayCacheType(option.KeyCachePrecision));
        return option.IsCurrent
            ? LocalizationHelper.Format("LocalAiPage_ModelOptionCurrent", label)
            : option.IsRecommended
                ? LocalizationHelper.Format("LocalAiPage_ModelOptionRecommended", label)
                : label;
    }

    private static string FormatContext(int tokens) =>
        tokens % 1024 == 0 ? $"{tokens / 1024}K" : tokens.ToString("N0", CultureInfo.CurrentCulture);

    private void OnModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressModelSelection || _viewModel is null)
            return;
        if (LocalAiModelSelector.SelectedItem is ComboBoxItem { Tag: string modelId })
            _viewModel.SelectedModelOptionId = modelId;
    }

    private void OnCustomModelChanged(object sender, TextChangedEventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.CustomModelReference = LocalAiCustomModelBox.Text;
    }

    private void OnApplyModel(object sender, RoutedEventArgs e) =>
        RunAction(() => _viewModel?.ApplyModelAsync() ?? Task.FromResult(false), nameof(OnApplyModel));
    private void OnForceApplyModel(object sender, RoutedEventArgs e) =>
        RunAction(() => _viewModel?.ForceApplyModelAsync() ?? Task.FromResult(false), nameof(OnForceApplyModel));
    private void OnCancelModelSwitch(object sender, RoutedEventArgs e) => _viewModel?.CancelModelSwitch();
    private void LocalAiRecheckAvailability_Click(object sender, RoutedEventArgs e) => _viewModel?.RecheckAvailability();
    private void LocalAiUnavailableDetails_Click(object sender, RoutedEventArgs e)
    {
        LocalAiUnavailableReasonText.Text = _viewModel?.LocalAiUnavailableReason is { } reason
            ? DescribeUnavailable(reason)
            : string.Empty;
        LocalAiUnavailableDetailsTip.IsOpen = !LocalAiUnavailableDetailsTip.IsOpen;
    }

    /// <summary>
    /// Turns the locale-neutral <see cref="LocalInferenceUnavailableReason"/> the ViewModel
    /// exposes into localized text. This lives in the View (not the ViewModel) because
    /// <see cref="LocalizationHelper"/> depends on the packaged app's resource map, which the
    /// ViewModel's unit tests (source-linked without a WinUI host) cannot resolve.
    /// </summary>
    private static string DescribeUnavailable(LocalInferenceUnavailableReason reason) => reason.Kind switch
    {
        LocalInferenceUnavailableReasonKind.RuntimeUnavailable =>
            LocalizationHelper.GetString("LocalAi_Reason_RuntimeUnavailable"),
        LocalInferenceUnavailableReasonKind.NoNvidiaGpu =>
            LocalizationHelper.GetString("LocalAi_Reason_NoNvidiaGpu"),
        LocalInferenceUnavailableReasonKind.UnknownModel =>
            LocalizationHelper.GetString("LocalAi_Reason_UnknownModel"),
        LocalInferenceUnavailableReasonKind.HardwareFactsIncomplete =>
            LocalizationHelper.GetString("LocalAi_Reason_HardwareFactsIncomplete"),
        LocalInferenceUnavailableReasonKind.InsufficientGpuMemory =>
            LocalizationHelper.Format(
                "LocalAi_Reason_InsufficientGpuMemory",
                reason.ModelDisplayName ?? LocalizationHelper.GetString("LocalAi_Reason_UnknownModelName"),
                FormatGigabytes(reason.RequiredGigabytes),
                reason.DetectedGigabytes is { } detected
                    ? FormatGigabytes(detected)
                    : LocalizationHelper.GetString("LocalAi_Reason_UnknownMemoryAmount")),
        LocalInferenceUnavailableReasonKind.InsufficientSystemMemory =>
            LocalizationHelper.Format("LocalAi_Reason_InsufficientSystemMemory", FormatGigabytes(reason.RequiredSystemGigabytes)),
        LocalInferenceUnavailableReasonKind.InsufficientDiskSpace =>
            LocalizationHelper.Format("LocalAi_Reason_InsufficientDiskSpace", FormatGigabytes(reason.RequiredDiskGigabytes)),
        LocalInferenceUnavailableReasonKind.DriverTooOld =>
            LocalizationHelper.Format(
                "LocalAi_Reason_DriverTooOld",
                reason.DetectedDriverVersion ?? LocalizationHelper.GetString("LocalAi_Reason_UnknownDriverVersion"),
                reason.MinimumDriverVersion),
        LocalInferenceUnavailableReasonKind.CudaCapabilityTooLow =>
            LocalizationHelper.GetString("LocalAi_Reason_CudaCapabilityTooLow"),
        _ => LocalizationHelper.GetString("LocalAi_Reason_Generic"),
    };

    private static string FormatGigabytes(double gigabytes) =>
        LocalizationHelper.Format("LocalAi_Reason_GigabytesFormat", gigabytes);

    private static void RunAction(Func<Task<bool>> action, string source) =>
        AsyncEventHandlerGuard.Run(action, new AppLogger(), source);
    private static Brush ResolveBrush(string key) => (Brush)Application.Current.Resources[key];
}
