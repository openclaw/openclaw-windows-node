using Microsoft.UI.Xaml;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

public sealed partial class VoiceSettingsPage
{
    private bool _gatewayVoicePageActive;
    private bool _gatewayVoiceReady;
    private IOperatorGatewayClient? _gatewayObservedClient;
    private CancellationTokenSource? _gatewayStatusCts;
    private CancellationTokenSource? _gatewayPreviewCts;

    private void InitializeGatewaySpeech()
    {
        Loaded += (_, _) =>
        {
            _gatewayVoicePageActive = true;
            CurrentApp.ChatProviderChanged += OnGatewayVoiceConnectionChanged;
            if (CurrentApp.ChatSpeech is { } speech) speech.SpeechStateChanged += OnGatewaySpeechState;
            _ = RefreshGatewayVoiceAsync();
        };
        Unloaded += (_, _) =>
        {
            _gatewayVoicePageActive = false;
            ObserveGatewayClient(null);
            _gatewayStatusCts?.Cancel();
            _gatewayPreviewCts?.Cancel();
            CurrentApp.ChatProviderChanged -= OnGatewayVoiceConnectionChanged;
            if (CurrentApp.ChatSpeech is { } speech) speech.SpeechStateChanged -= OnGatewaySpeechState;
        };
    }

    private void OnGatewayVoiceConnectionChanged(object? sender, EventArgs e) => _ = RefreshGatewayVoiceAsync();

    private void ObserveGatewayClient(IOperatorGatewayClient? client)
    {
        if (ReferenceEquals(_gatewayObservedClient, client)) return;
        if (_gatewayObservedClient is { } previous)
        {
            previous.StatusChanged -= OnGatewayTransportStatus;
            previous.HandshakeSucceeded -= OnGatewayHandshake;
        }
        _gatewayObservedClient = client;
        if (client is not null)
        {
            client.StatusChanged += OnGatewayTransportStatus;
            client.HandshakeSucceeded += OnGatewayHandshake;
        }
    }

    private void OnGatewayTransportStatus(object? sender, ConnectionStatus status) => QueueGatewayReadiness(sender);
    private void OnGatewayHandshake(object? sender, EventArgs e) => QueueGatewayReadiness(sender);
    private void QueueGatewayReadiness(object? sender)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (_gatewayVoicePageActive && ReferenceEquals(sender, _gatewayObservedClient))
                _ = RefreshGatewayVoiceAsync();
        });
    }

    private void OnGatewayVoiceToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || CurrentApp.Settings is not { } settings) return;
        _gatewayPreviewCts?.Cancel();
        CurrentApp.ChatSpeech?.StopGatewaySpeaking();
        settings.UseGatewayVoice = GatewayVoiceToggle.IsOn;
        settings.Save();
        GatewayVoicePanel.Visibility = settings.UseGatewayVoice ? Visibility.Visible : Visibility.Collapsed;
        _ = RefreshGatewayVoiceAsync();
    }

    private void OnGatewayVoiceRefresh(object sender, RoutedEventArgs e) => _ = RefreshGatewayVoiceAsync();

    private async Task RefreshGatewayVoiceAsync()
    {
        _gatewayStatusCts?.Cancel();
        if (!_gatewayVoicePageActive) return;
        ObserveGatewayClient(CurrentApp.GatewayClient);
        if (!GatewayVoiceToggle.IsOn) return;
        using var request = new CancellationTokenSource();
        _gatewayStatusCts = request;
        var client = CurrentApp.GatewayClient;
        var epoch = client?.SessionMutationConnectionEpoch;
        _gatewayVoiceReady = false;
        GatewayVoicePreview.IsEnabled = false;
        GatewayVoiceStatus.Text = L("GatewayVoice_Checking");
        try
        {
            if (client is null || epoch is null)
                throw new GatewaySpeechException(GatewaySpeechFailure.Disconnected);
            GatewaySpeech.RequireAccess(client, epoch.Value);
            var status = await client.GetSpeechStatusAsync(epoch.Value, request.Token);
            if (!IsCurrent()) return;
            GatewaySpeech.RequireAccess(client, epoch.Value);
            if (!status.IsReady) throw new GatewaySpeechException(GatewaySpeechFailure.ProviderUnavailable);
            _gatewayVoiceReady = true;
            GatewayVoiceStatus.Text = Lf("GatewayVoice_Ready", status.Provider,
                string.Join(", ", status.ConfiguredProviders.Take(5)));
            GatewayVoicePreview.IsEnabled = CurrentApp.Settings?.NodeTtsEnabled == true && _gatewayPreviewCts is null;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (GatewaySpeechException ex)
        {
            if (IsCurrent()) GatewayVoiceStatus.Text = L(GatewaySpeechPresentation.FailureKey(ex.Failure));
        }
        catch (Exception)
        {
            if (IsCurrent()) GatewayVoiceStatus.Text = L("GatewayVoice_SynthesisFailed");
        }
        finally
        {
            if (ReferenceEquals(_gatewayStatusCts, request)) _gatewayStatusCts = null;
        }
        bool IsCurrent() => _gatewayVoicePageActive && !request.IsCancellationRequested
            && ReferenceEquals(_gatewayStatusCts, request) && ReferenceEquals(CurrentApp.GatewayClient, client)
            && client?.SessionMutationConnectionEpoch == epoch;
    }

    private void OnGatewayVoicePreview(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(PreviewGatewayVoiceAsync, new AppLogger(), nameof(OnGatewayVoicePreview));

    private async Task PreviewGatewayVoiceAsync()
    {
        if (_gatewayPreviewCts is not null || CurrentApp.Settings?.NodeTtsEnabled != true
            || !GatewayVoiceToggle.IsOn || CurrentApp.ChatSpeech is not { } speech) return;
        _gatewayStatusCts?.Cancel();
        using var preview = new CancellationTokenSource();
        _gatewayPreviewCts = preview;
        GatewayVoicePreview.IsEnabled = false;
        GatewayVoiceRefresh.IsEnabled = false;
        GatewayVoiceStop.IsEnabled = true;
        try { await speech.PreviewGatewayVoiceAsync(L("VoiceSettingsPage_CompanionPreviewText"), preview.Token); }
        finally
        {
            if (ReferenceEquals(_gatewayPreviewCts, preview)) _gatewayPreviewCts = null;
            GatewayVoiceRefresh.IsEnabled = true;
            GatewayVoiceStop.IsEnabled = false;
            GatewayVoicePreview.IsEnabled = _gatewayVoicePageActive && GatewayVoiceToggle.IsOn && _gatewayVoiceReady
                && CurrentApp.Settings?.NodeTtsEnabled == true && CurrentApp.GatewayClient?.IsConnectedToGateway == true;
        }
    }

    private void OnGatewayVoiceStop(object sender, RoutedEventArgs e)
    {
        _gatewayPreviewCts?.Cancel();
        CurrentApp.ChatSpeech?.StopGatewaySpeaking();
        GatewayVoiceStatus.Text = L("GatewayVoice_Stopped");
    }

    private void OnGatewaySpeechState(GatewaySpeechPlaybackState state)
    {
        if (!_gatewayVoicePageActive || !GatewayVoiceToggle.IsOn) return;
        GatewayVoiceStatus.Text = state.Phase == GatewaySpeechPhase.Idle
            ? L("GatewayVoice_Stopped") : GatewaySpeechPresentation.Format(state, L);
        GatewayVoiceStop.IsEnabled = state.Phase is GatewaySpeechPhase.Synthesizing or GatewaySpeechPhase.Playing;
    }
}
