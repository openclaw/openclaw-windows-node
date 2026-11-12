using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.SetupEngine.UI.Controls;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class AiCompletionPage : Page
{
    private Action? _retry;
    private Action? _back;
    private Action? _close;
    private SetupLoadingProgress? _loading;
    public AiCompletionPage()
    {
        InitializeComponent();
        CloseButton.Content = SetupLocalization.GetString("Onboarding_Ready_Close");
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
        CloseButton.Visibility = Visibility.Collapsed;
        Detail.Text = SetupLocalization.GetString(stage switch
        {
            SetupNativeCompletionStage.Draining => "Onboarding_Finishing_Draining",
            SetupNativeCompletionStage.Opening => "Onboarding_Finishing_Restarting",
            SetupNativeCompletionStage.Finalizing => "Onboarding_Ready_Finalizing",
            _ => "Onboarding_Ready_Verifying",
        });
    }

    public void ShowFailure(Action? retry = null, Action? back = null, Exception? error = null, Action? close = null)
    {
        _retry = retry;
        _back = back;
        _close = close;
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
        RetryButton.Visibility = retry is null ? Visibility.Collapsed : Visibility.Visible;
        BackButton.Visibility = back is null ? Visibility.Collapsed : Visibility.Visible;
        CloseButton.Visibility = close is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Retry_Click(object sender, RoutedEventArgs args) => _retry?.Invoke();
    private void Back_Click(object sender, RoutedEventArgs args) => _back?.Invoke();
    private void Close_Click(object sender, RoutedEventArgs args) => _close?.Invoke();
}
