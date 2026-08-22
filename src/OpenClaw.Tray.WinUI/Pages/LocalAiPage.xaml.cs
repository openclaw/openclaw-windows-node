using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using System.Globalization;

namespace OpenClawTray.Pages;

public sealed partial class LocalAiPage : Page
{
    private LocalAiPageViewModel? _viewModel;

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

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        RefreshFromViewModel();

    private void RefreshFromViewModel()
    {
        if (_viewModel is null)
            return;
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
            LocalAiPageViewModel.ContextLengthText,
            LocalAiPageViewModel.KvCacheText);
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
        EngineBusyIndicator.IsActive = _viewModel.IsBusy;
        EngineBusyIndicator.Visibility = _viewModel.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        StartButton.IsEnabled = _viewModel.CanStart;
        StopButton.IsEnabled = _viewModel.CanStop;
        RestartButton.IsEnabled = _viewModel.CanRestart;
        OpenLogsButton.IsEnabled = _viewModel.CanOpenLogs;
        RetrySetupButton.IsEnabled = _viewModel.CanRetrySetup;
        RetrySetupButton.Visibility = _viewModel.CanRetrySetup ? Visibility.Visible : Visibility.Collapsed;
        RepairConnectionButton.IsEnabled = _viewModel.CanRepairConnection;
        OpenChatButton.IsEnabled = _viewModel.CanOpenChat;
    }

    private void OnStart(object sender, RoutedEventArgs e) => RunAction(() => _viewModel?.StartAsync() ?? Task.FromResult(false), nameof(OnStart));
    private void OnStop(object sender, RoutedEventArgs e) => RunAction(() => _viewModel?.StopAsync() ?? Task.FromResult(false), nameof(OnStop));
    private void OnRestart(object sender, RoutedEventArgs e) => RunAction(() => _viewModel?.RestartAsync() ?? Task.FromResult(false), nameof(OnRestart));
    private void OnOpenLogs(object sender, RoutedEventArgs e) => _viewModel?.OpenLogs();
    private void OnRetrySetup(object sender, RoutedEventArgs e) => _viewModel?.RetrySetup();
    private void OnRepairConnection(object sender, RoutedEventArgs e) => _viewModel?.RepairConnection();
    private void OnOpenChat(object sender, RoutedEventArgs e) => _viewModel?.OpenChat();
    private static void RunAction(Func<Task<bool>> action, string source) =>
        AsyncEventHandlerGuard.Run(action, new AppLogger(), source);
    private static Brush ResolveBrush(string key) => (Brush)Application.Current.Resources[key];
}
