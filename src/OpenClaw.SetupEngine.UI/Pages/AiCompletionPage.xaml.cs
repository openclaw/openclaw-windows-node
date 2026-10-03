using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.SetupEngine.UI.Controls;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class AiCompletionPage : Page
{
    private Action? _retry;
    private Action? _back;
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
        Detail.Text = SetupLocalization.GetString(stage switch
        {
            SetupNativeCompletionStage.Draining => "Onboarding_Finishing_Draining",
            SetupNativeCompletionStage.Opening => "Onboarding_Finishing_Restarting",
            SetupNativeCompletionStage.Finalizing => "Onboarding_Ready_Finalizing",
            _ => "Onboarding_Ready_Verifying",
        });
    }

    public void ShowFailure(Action? retry = null, Action? back = null)
    {
        _retry = retry;
        _back = back;
        Progress.Visibility = Visibility.Collapsed;
        ErrorBar.Message = SetupLocalization.GetString(retry is not null || back is not null
            ? "Onboarding_Finishing_Recovery" : "Onboarding_Finishing_Interrupted");
        ErrorBar.IsOpen = true;
        RetryButton.Visibility = retry is null ? Visibility.Collapsed : Visibility.Visible;
        BackButton.Visibility = back is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Retry_Click(object sender, RoutedEventArgs args) => _retry?.Invoke();
    private void Back_Click(object sender, RoutedEventArgs args) => _back?.Invoke();
}
