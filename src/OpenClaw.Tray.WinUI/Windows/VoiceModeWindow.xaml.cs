using Microsoft.UI.Xaml;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Services.Voice;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using WinUIEx;

namespace OpenClawTray.Windows;

public sealed partial class VoiceModeWindow : WindowEx
{
    private readonly SettingsManager _settings;
    private readonly IVoiceRuntimeControlApi _voiceRuntimeControlApi;
    private readonly IVoiceConfigurationApi _voiceConfigurationApi;

    public bool IsClosed { get; private set; }

    public event EventHandler? OpenSettingsRequested;

    public VoiceModeWindow(
        SettingsManager settings,
        IVoiceRuntimeControlApi voiceRuntimeControlApi,
        IVoiceConfigurationApi voiceConfigurationApi)
    {
        _settings = settings;
        _voiceRuntimeControlApi = voiceRuntimeControlApi;
        _voiceConfigurationApi = voiceConfigurationApi;

        InitializeComponent();

        Title = "Voice Mode";
        this.SetWindowSize(520, 620);
        this.CenterOnScreen();
        this.SetIcon(AppIconHelper.GetStatusIconPath(ConnectionStatus.Connected));

        Closed += (s, e) => IsClosed = true;

        RefreshStatus();
    }

    public void RefreshStatus()
    {
        var running = _voiceRuntimeControlApi.CurrentStatus;
        var catalog = _voiceConfigurationApi.GetProviderCatalog();

        StatusItemsControl.ItemsSource = new List<DetailRow>
        {
            new("Mode", VoiceDisplayHelper.GetModeLabel(_settings.Voice.Mode)),
            new("Runtime", VoiceDisplayHelper.GetRuntimeLabel(running)),
            new("Node Mode", _settings.EnableNodeMode ? "Enabled" : "Disabled"),
            new("Session", string.IsNullOrWhiteSpace(running.SessionKey) ? "main" : running.SessionKey!),
            new("State", VoiceDisplayHelper.GetStateLabel(running.State)),
            new("Queued replies", running.PendingReplyCount.ToString())
        };

        ConfigurationItemsControl.ItemsSource = new List<DetailRow>
        {
            new("Speech to text", ResolveProviderName(catalog.SpeechToTextProviders, _settings.Voice.SpeechToTextProviderId, "Windows Speech Recognition")),
            new("Text to speech", ResolveProviderName(catalog.TextToSpeechProviders, _settings.Voice.TextToSpeechProviderId, "Windows Speech Synthesis")),
            new("Listen device", DescribeDevice(_settings.Voice.InputDeviceId, "System default microphone")),
            new("Talk device", DescribeDevice(_settings.Voice.OutputDeviceId, "System default speaker")),
            new("Voice toasts", _settings.Voice.ShowConversationToasts ? "Enabled" : "Disabled")
        };

        RecentItemsControl.ItemsSource = new List<DetailRow>
        {
            new("Last utterance", FormatTimestamp(running.LastUtteranceUtc)),
            new("Last wake", FormatTimestamp(running.LastVoiceWakeUtc)),
            new("Last issue", string.IsNullOrWhiteSpace(running.LastError) ? "None" : running.LastError!)
        };

        UpdateTroubleshooting(running.LastError);
    }

    private static string ResolveProviderName(
        IReadOnlyList<VoiceProviderOption> providers,
        string? providerId,
        string fallback)
    {
        foreach (var provider in providers)
        {
            if (string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase))
            {
                return provider.Name;
            }
        }

        return fallback;
    }

    private static string DescribeDevice(string? deviceId, string defaultLabel)
    {
        return string.IsNullOrWhiteSpace(deviceId) ? defaultLabel : "Selected device";
    }

    private static string FormatTimestamp(DateTime? value)
    {
        return value?.ToLocalTime().ToString("HH:mm:ss") ?? "None";
    }

    private void UpdateTroubleshooting(string? error)
    {
        TroubleshootingPanel.Visibility = Visibility.Collapsed;
        OpenSpeechSettingsButton.Visibility = Visibility.Collapsed;
        OpenMicrophoneSettingsButton.Visibility = Visibility.Collapsed;
        TroubleshootingTextBlock.Text = string.Empty;

        if (string.IsNullOrWhiteSpace(error))
        {
            return;
        }

        if (error.Contains("online speech recognition is disabled", StringComparison.OrdinalIgnoreCase))
        {
            TroubleshootingPanel.Visibility = Visibility.Visible;
            OpenSpeechSettingsButton.Visibility = Visibility.Visible;
            TroubleshootingTextBlock.Text =
                "To fix this: open Windows Settings, go to Privacy & security > Speech, turn on Online speech recognition, then restart voice mode.";
            return;
        }

        if (error.Contains("microphone access is blocked", StringComparison.OrdinalIgnoreCase))
        {
            TroubleshootingPanel.Visibility = Visibility.Visible;
            OpenMicrophoneSettingsButton.Visibility = Visibility.Visible;
            TroubleshootingTextBlock.Text =
                "To fix this: open Windows Settings, go to Privacy & security > Microphone, allow microphone access and enable desktop app access, then restart voice mode.";
        }
    }

    private void OnOpenSpeechSettings(object sender, RoutedEventArgs e)
    {
        OpenSettingsUri("ms-settings:privacy-speech");
    }

    private void OnOpenMicrophoneSettings(object sender, RoutedEventArgs e)
    {
        OpenSettingsUri("ms-settings:privacy-microphone");
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        RefreshStatus();
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static void OpenSettingsUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private sealed record DetailRow(string Label, string Value);
}
