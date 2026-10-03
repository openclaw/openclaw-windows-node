using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenClaw.SetupEngine.UI.Controls;

public sealed partial class SetupLoadingView : UserControl, IDisposable
{
    private SetupLoadingProgress? _source;
    private bool _disposed;
    private SetupLoadingProgress.Scope? _cancellable;
    private Action? _cancel;
    public SetupLoadingView()
    {
        InitializeComponent();
        CancelButton.Content = SetupLocalization.GetString("Onboarding_AiSetup_Cancel.Content");
    }

    public void SetCancellation(SetupLoadingProgress.Scope scope, Action cancel)
    {
        _cancellable = scope;
        _cancel = cancel;
        Refresh();
    }

    public void Bind(SetupLoadingProgress source)
    {
        if (_source is not null) _source.Changed -= Refresh;
        _source = source;
        source.Changed += Refresh;
        Refresh();
    }

    private void Refresh()
    {
        void Apply()
        {
            if (_disposed) return;
            var snapshot = _source?.Current;
            if (_cancellable?.IsCurrent != true) { _cancellable = null; _cancel = null; }
            CancelButton.Visibility = _cancel is null ? Visibility.Collapsed : Visibility.Visible;
            Visibility = snapshot is null ? Visibility.Collapsed : Visibility.Visible;
            if (snapshot is null) return;
            Title.Text = TitleFor(snapshot.Group);
            Detail.Text = snapshot.Activity ?? DetailFor(snapshot.Step);
            var detail = snapshot.Detail;
            ActivityProgress.IsIndeterminate = detail?.Total is not > 0;
            ActivityProgress.Minimum = 0;
            ActivityProgress.Maximum = 1;
            if (detail?.Total is > 0)
                ActivityProgress.Value = Math.Clamp((double)detail.Completed / detail.Total.Value, 0, 1);
            Measurement.Text = detail is null ? "" : FormatDetail(detail);
        }
        if (DispatcherQueue.HasThreadAccess) Apply();
        else DispatcherQueue.TryEnqueue(Apply);
    }

    internal static string TitleFor(SetupLoadingGroup group) => SetupLocalization.GetString(group switch
    {
        SetupLoadingGroup.LocalAi => "Onboarding_AiSetup_LocalInstalling",
        SetupLoadingGroup.Finishing => "Onboarding_Finishing_Heading.Text",
        _ => "Onboarding_Loading_Gateway"
    });

    internal static string DetailFor(SetupLoadingStep step) => SetupLocalization.GetString(step switch
    {
        SetupLoadingStep.PrepareGateway => "Onboarding_Native_InProgress",
        SetupLoadingStep.CheckGatewaySupport => "Onboarding_Native_CheckingSupport",
        SetupLoadingStep.CheckGatewayPackage => "Onboarding_Native_Checking",
        SetupLoadingStep.InstallGatewayPackage => "Onboarding_Native_InstallingPackage",
        SetupLoadingStep.ConnectGateway => "Onboarding_AiSetup_Connecting",
        SetupLoadingStep.DiscoverChoices => "Onboarding_AiSetup_LocalProgress_Detecting",
        SetupLoadingStep.CheckArtifacts => "Onboarding_AiSetup_LocalProgress_CheckingFiles",
        SetupLoadingStep.CheckHardware => "Onboarding_AiSetup_LocalProgress_CheckingHardware",
        SetupLoadingStep.PrepareConsole => "Onboarding_AiSetup_LocalProgress_Console",
        SetupLoadingStep.PrepareLocalAi => "Onboarding_AiSetup_LocalProgress_PreparingGateway",
        SetupLoadingStep.StartLocalAi => "Onboarding_AiSetup_LocalProgress_StartingRuntime",
        SetupLoadingStep.PublishProvider => "Onboarding_AiSetup_LocalProgress_PublishingProvider",
        SetupLoadingStep.VerifyModel => "Onboarding_AiSetup_LocalProgress_Verifying",
        SetupLoadingStep.Drain => "Onboarding_Finishing_Draining",
        SetupLoadingStep.RestartCompanion => "Onboarding_Finishing_Restarting",
        SetupLoadingStep.ReconnectGateway => "Onboarding_AiSetup_Reconnecting",
        SetupLoadingStep.OpenDestination => "Onboarding_Ready_Opening",
        _ => "Onboarding_Loading_" + step
    });

    private static string FormatDetail(SetupLoadingMeasurement detail)
    {
        if (detail.Total is not > 0) return detail.Detail;
        object completed = detail.Bytes ? FormatBytes(detail.Completed) : detail.Completed;
        object total = detail.Bytes ? FormatBytes(detail.Total.Value) : detail.Total.Value;
        return detail.Detail + "  " + SetupLocalization.Format("Onboarding_V5_DownloadMeasurement", completed, total);
    }

    private static string FormatBytes(long bytes) => bytes >= 1_000_000_000
        ? $"{bytes / 1_000_000_000d:0.0} GB" : $"{bytes / 1_000_000d:0} MB";

    public void Dispose()
    {
        _disposed = true;
        if (_source is not null) _source.Changed -= Refresh;
        _source = null;
        _cancel = null;
        _cancellable = null;
    }

    private void Cancel_Click(object sender, RoutedEventArgs args)
    {
        if (!_disposed && _cancellable?.IsCurrent == true) _cancel?.Invoke();
    }
}
