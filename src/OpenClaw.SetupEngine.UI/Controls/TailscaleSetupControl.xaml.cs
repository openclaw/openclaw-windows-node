using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using System.ComponentModel;
using System.Diagnostics;

namespace OpenClaw.SetupEngine.UI.Controls;

public sealed partial class TailscaleSetupControl : UserControl
{
    private SetupAccessDraft? _draft;
    private SetupConfig? _config;
    private bool _initializing;
    private CancellationTokenSource? _tailscaleStatusCancellation;
    private int _tailscaleStatusGeneration;
    private readonly Func<CancellationToken, Task<(int ExitCode, string Output)>> _statusProbe;
    public event EventHandler? StateChanged;

    public TailscaleSetupControl() : this(ProbeWindowsStatusAsync) { }

    public TailscaleSetupControl(Func<CancellationToken, Task<(int ExitCode, string Output)>> statusProbe)
    {
        _statusProbe = statusProbe ?? throw new ArgumentNullException(nameof(statusProbe));
        InitializeComponent();
        Unloaded += (_, _) => Deactivate();
    }

    public void Initialize(SetupAccessDraft draft)
    {
        CancelTailscaleStatusProbe();
        _draft = draft;
        _config = draft.Config;
        _initializing = true;
        TailscaleToggle.IsOn = _config.Tailscale.Enabled;
        TailscaleTrustAuthToggle.IsOn = _config.Tailscale.TrustTailscaleAuth;
        TailscaleAuthModeSelector.SelectedIndex = _config.Tailscale.AuthMode == TailscaleAuthMode.AuthKey ? 1 : 0;
        TailscaleAuthKeyBox.Password = _config.Tailscale.AuthKey ?? string.Empty;
        _initializing = false;
        UpdateTailscaleOptions();
    }

    public void Deactivate()
    {
        CancelTailscaleStatusProbe();
        _draft = null;
        _config = null;
        _initializing = true;
        TailscaleAuthKeyBox.Password = "";
        _initializing = false;
    }

    private void AuthKey_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _config is null) return;
        _config.Tailscale.AuthKey = TailscaleAuthKeyBox.Password;
        PublishState();
    }

    private void Recheck_Click(object sender, RoutedEventArgs e) => UpdateTailscaleOptions();
    private void PublishState() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void TailscaleToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _config is null) return;
        _config.Tailscale.Enabled = TailscaleToggle.IsOn == true;
        UpdateTailscaleOptions();
        PublishState();
    }

    private void TailscaleAuthMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        TailscaleAuthKeyBox.Visibility = TailscaleAuthModeSelector.SelectedIndex == 1
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!_initializing && _config is not null)
        {
            _config.Tailscale.AuthMode = TailscaleAuthModeSelector.SelectedIndex == 1
                ? TailscaleAuthMode.AuthKey : TailscaleAuthMode.Browser;
            PublishState();
        }
    }

    private void TailscaleTrustAuthToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _config is null)
            return;

        _config.Tailscale.TrustTailscaleAuth = TailscaleTrustAuthToggle.IsOn == true;
        PublishState();
    }

    private void UpdateTailscaleOptions()
    {
        CancelTailscaleStatusProbe();
        if (_draft is null || _config is null) return;
        var enabled = TailscaleToggle.IsOn == true;
        TailscaleOptions.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        TailscaleAuthKeyBox.Visibility = enabled && TailscaleAuthModeSelector.SelectedIndex == 1
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!enabled)
            return;

        var cancellation = new CancellationTokenSource();
        _tailscaleStatusCancellation = cancellation;
        _ = RefreshWindowsTailscaleStatusAsync(
            _tailscaleStatusGeneration,
            cancellation);
    }

    private async Task RefreshWindowsTailscaleStatusAsync(
        int generation,
        CancellationTokenSource cancellation)
    {
        _draft!.TailscaleReady = false;
        PublishState();
        TailscaleStatusText.Text = SetupLocalization.GetString("Onboarding_V2_TailscaleChecking");
        try
        {
            var result = await _statusProbe(cancellation.Token);
            if (!IsCurrentTailscaleStatusProbe(generation, cancellation))
                return;

            string? dnsName = null;
            string? tailnetDnsSuffix = null;
            if (result.ExitCode == 0 &&
                TailscaleSetupPolicy.TryParseStatus(result.Output, out var status) &&
                status.IsRunning &&
                SetupTailscaleReadiness.IsMagicDnsEnabled(result.Output))
            {
                dnsName = status.DnsName;
                tailnetDnsSuffix = TailscaleSetupPolicy.GetTailnetDnsSuffix(dnsName);
            }
            TailscaleStatusText.Text = tailnetDnsSuffix is not null
                ? SetupLocalization.Format("Onboarding_V2_TailscaleConnected", dnsName)
                : SetupLocalization.GetString("Onboarding_V2_TailscaleRequired");
            if (_config is not null && TailscaleToggle.IsOn == true)
            {
                _draft!.TailscaleReady = tailnetDnsSuffix is not null;
                _config.Tailscale.TailnetDnsSuffix = tailnetDnsSuffix;
                PublishState();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (
            ex is Win32Exception or
                IOException or
                InvalidOperationException or
                NotSupportedException or
                AggregateException or
                UnauthorizedAccessException)
        {
            if (!IsCurrentTailscaleStatusProbe(generation, cancellation))
                return;

            Trace.WriteLine(
                $"TailscaleSetupControl: Windows Tailscale status probe failed ({ex.GetType().Name}).");
            TailscaleStatusText.Text = SetupLocalization.GetString("Onboarding_V2_TailscaleRequired");
            if (_config is not null && TailscaleToggle.IsOn == true)
            {
                _config.Tailscale.TailnetDnsSuffix = null;
                PublishState();
            }
        }
        finally
        {
            if (ReferenceEquals(_tailscaleStatusCancellation, cancellation))
                _tailscaleStatusCancellation = null;
            cancellation.Dispose();
        }
    }

    private static Task<(int ExitCode, string Output)> ProbeWindowsStatusAsync(CancellationToken cancellationToken)
    {
        var path = PreflightWindowsTailscaleStep.ResolveWindowsTailscaleCliPath();
        var psi = new ProcessStartInfo
        {
            FileName = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("status");
        psi.ArgumentList.Add("--json");
        psi.ArgumentList.Add("--peers=false");
        return Task.Run(() => BoundedProcessOutput.ReadAsync(
            psi, BoundedProcessOutput.DefaultTimeoutMs, cancellationToken), cancellationToken);
    }

    private void CancelTailscaleStatusProbe()
    {
        _tailscaleStatusGeneration++;
        var cancellation = _tailscaleStatusCancellation;
        _tailscaleStatusCancellation = null;
        cancellation?.Cancel();
    }

    private bool IsCurrentTailscaleStatusProbe(
        int generation,
        CancellationTokenSource cancellation) =>
        generation == _tailscaleStatusGeneration &&
        ReferenceEquals(_tailscaleStatusCancellation, cancellation) &&
        !cancellation.IsCancellationRequested;

}
