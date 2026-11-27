using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.SetupEngine.UI.Controls;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class AiCompletionPage : Page
{
    private Action? _retry;
    private Action? _back;
    private Action? _refresh;
    private Func<FrameworkElement, Task>? _collectLogs;
    private bool _collectingLogs;
    private SetupLoadingProgress? _loading;
    public AiCompletionPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadingChanged();
        Unloaded += (_, _) =>
        {
            if (_loading is not null) _loading.Changed -= LoadingChanged;
            _loading = null;
        };
    }

    public void BindLoading(SetupLoadingProgress loading)
    {
        if (_loading is not null) _loading.Changed -= LoadingChanged;
        _loading = loading;
        loading.Changed += LoadingChanged;
        LoadingChanged();
    }

    private void LoadingChanged()
    {
        void Apply()
        {
            if (_loading?.Current is { } current)
                Detail.Text = current.Activity ?? SetupLoadingView.DetailFor(current.Step);
        }
        if (DispatcherQueue.HasThreadAccess) Apply();
        else DispatcherQueue.TryEnqueue(Apply);
    }

    public void ShowRuntimeVerification() =>
        Detail.Text = SetupLocalization.GetString("Onboarding_Ready_Verifying");

    public void ShowStage(SetupNativeCompletionStage stage)
    {
        Progress.Visibility = Visibility.Visible;
        ErrorBar.IsOpen = false;
        RetryButton.Visibility = BackButton.Visibility = Visibility.Collapsed;
        CollectLogsButton.Visibility = Visibility.Collapsed;
        DiagnosticsStatus.Visibility = Visibility.Collapsed;
        Detail.Text = SetupLocalization.GetString(stage switch
        {
            SetupNativeCompletionStage.Draining => "Onboarding_Finishing_Draining",
            SetupNativeCompletionStage.Opening => "Onboarding_Finishing_Restarting",
            SetupNativeCompletionStage.Finalizing => "Onboarding_Ready_Finalizing",
            _ => "Onboarding_Ready_Verifying",
        });
    }

    public void ShowFailure(Action? retry = null, Action? back = null, Exception? error = null)
    {
        _retry = retry;
        _back = back;
        Progress.Visibility = Visibility.Collapsed;
        ErrorBar.Message = SetupLocalization.GetString(retry is not null || back is not null
            ? "Onboarding_Finishing_Recovery" : "Onboarding_Finishing_Interrupted");
        if (error is not null)
        {
            Detail.Text = SetupLocalization.GetString(AiSetupReadinessPresentation.CompletionFailureKey(
                error, "Onboarding_Ready_FinalizingFailed"));
            System.Diagnostics.Trace.TraceWarning("Setup completion failed ({0}; phase: {1}).",
                error.GetType().Name, (error as SetupNativeCompletionTimeoutException)?.Phase.ToString() ?? "none");
        }
        ErrorBar.IsOpen = true;
        RetryButton.Visibility = retry is null && _refresh is null ? Visibility.Collapsed : Visibility.Visible;
        BackButton.Visibility = back is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void ConfigureRecovery(Action? refresh, Func<FrameworkElement, Task>? collectLogs)
    {
        _refresh = refresh;
        _collectLogs = collectLogs;
        RetryButton.Visibility = refresh is null && _retry is null ? Visibility.Collapsed : Visibility.Visible;
        CollectLogsButton.Visibility = Visibility.Visible;
        CollectLogsButton.IsEnabled = collectLogs is not null && !_collectingLogs;
    }

    private void CollectLogs_Click(object sender, RoutedEventArgs args)
    {
        if (_collectingLogs || _collectLogs is not { } collectLogs) return;
        _collectingLogs = true;
        CollectLogsButton.IsEnabled = false;
        OpenClaw.Shared.AsyncEventHandlerGuard.Run(async () =>
        {
            DiagnosticsStatus.Visibility = Visibility.Collapsed;
            try { await collectLogs(this); }
            finally
            {
                _collectingLogs = false;
                CollectLogsButton.IsEnabled = _collectLogs is not null;
            }
        }, onError: error =>
        {
            System.Diagnostics.Trace.TraceWarning("Setup diagnostics collection failed ({0}).", error.GetType().Name);
            DiagnosticsStatus.Text = SetupLocalization.Format("Onboarding_Finishing_CollectLogsFailed", error.GetType().Name);
            DiagnosticsStatus.Visibility = Visibility.Visible;
        });
    }

    private void Retry_Click(object sender, RoutedEventArgs args)
    {
        _refresh?.Invoke();
        _retry?.Invoke();
    }
    private void Back_Click(object sender, RoutedEventArgs args) => _back?.Invoke();
}
