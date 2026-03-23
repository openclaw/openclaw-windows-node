using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Services.Voice;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using WinUIEx;

namespace OpenClawTray.Windows;

public sealed partial class VoiceModeWindow : WindowEx
{
    private readonly SettingsManager _settings;
    private readonly VoiceService _voiceService;
    private List<ProviderOption> _speechToTextOptions = new();
    private List<ProviderOption> _textToSpeechOptions = new();
    private List<DeviceOption> _inputOptions = new();
    private List<DeviceOption> _outputOptions = new();

    public bool IsClosed { get; private set; }

    public VoiceModeWindow(SettingsManager settings, VoiceService voiceService)
    {
        _settings = settings;
        _voiceService = voiceService;

        InitializeComponent();

        Title = "Voice Mode";
        this.SetWindowSize(520, 620);
        this.CenterOnScreen();
        this.SetIcon(IconHelper.GetStatusIconPath(ConnectionStatus.Connected));

        Closed += (s, e) => IsClosed = true;

        LoadSettings();
        _ = LoadDevicesAsync();
    }

    private void LoadSettings()
    {
        LoadProviders();
        SelectMode(_settings.Voice.Mode);
        SelectChatWindowSubmitMode(_settings.Voice.AlwaysOn.ChatWindowSubmitMode);
        VoiceConversationToastsCheckBox.IsChecked = _settings.Voice.ShowConversationToasts;
        UpdateModeInfo();
        UpdateProviderInfo();
        StatusTextBlock.Text = BuildStatusText();
    }

    private void LoadProviders()
    {
        var catalog = _voiceService.GetProviderCatalog();

        _speechToTextOptions = catalog.SpeechToTextProviders
            .Select(p => new ProviderOption(p.Id, p.Name, p.Runtime, p.Description))
            .ToList();
        _textToSpeechOptions = catalog.TextToSpeechProviders
            .Select(p => new ProviderOption(p.Id, p.Name, p.Runtime, p.Description))
            .ToList();

        SpeechToTextProviderComboBox.ItemsSource = _speechToTextOptions;
        TextToSpeechProviderComboBox.ItemsSource = _textToSpeechOptions;

        SpeechToTextProviderComboBox.SelectedItem =
            _speechToTextOptions.FirstOrDefault(p => p.Id == _settings.Voice.SpeechToTextProviderId)
            ?? _speechToTextOptions.FirstOrDefault();
        TextToSpeechProviderComboBox.SelectedItem =
            _textToSpeechOptions.FirstOrDefault(p => p.Id == _settings.Voice.TextToSpeechProviderId)
            ?? _textToSpeechOptions.FirstOrDefault();
    }

    private async Task LoadDevicesAsync()
    {
        try
        {
            StatusTextBlock.Text = "Loading audio devices...";
            var devices = await _voiceService.ListDevicesAsync();

            _inputOptions =
            [
                new DeviceOption(null, "System default microphone")
            ];
            _inputOptions.AddRange(devices
                .Where(d => d.IsInput)
                .Select(d => new DeviceOption(d.DeviceId, d.Name)));

            _outputOptions =
            [
                new DeviceOption(null, "System default speaker")
            ];
            _outputOptions.AddRange(devices
                .Where(d => d.IsOutput)
                .Select(d => new DeviceOption(d.DeviceId, d.Name)));

            InputDeviceComboBox.ItemsSource = _inputOptions;
            OutputDeviceComboBox.ItemsSource = _outputOptions;

            InputDeviceComboBox.SelectedItem = _inputOptions.FirstOrDefault(o => o.DeviceId == _settings.Voice.InputDeviceId) ?? _inputOptions[0];
            OutputDeviceComboBox.SelectedItem = _outputOptions.FirstOrDefault(o => o.DeviceId == _settings.Voice.OutputDeviceId) ?? _outputOptions[0];

            StatusTextBlock.Text = BuildStatusText();
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Failed to load devices: {ex.Message}";
        }
    }

    private void SelectMode(VoiceActivationMode mode)
    {
        var target = mode switch
        {
            VoiceActivationMode.WakeWord => "WakeWord",
            VoiceActivationMode.AlwaysOn => "AlwaysOn",
            _ => "Off"
        };

        foreach (var item in ModeComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), target, StringComparison.Ordinal))
            {
                ModeComboBox.SelectedItem = item;
                return;
            }
        }

        ModeComboBox.SelectedIndex = 0;
    }

    private VoiceActivationMode GetSelectedMode()
    {
        var tag = (ModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return tag switch
        {
            "WakeWord" => VoiceActivationMode.WakeWord,
            "AlwaysOn" => VoiceActivationMode.AlwaysOn,
            _ => VoiceActivationMode.Off
        };
    }

    private void SelectChatWindowSubmitMode(VoiceChatWindowSubmitMode mode)
    {
        var target = mode == VoiceChatWindowSubmitMode.WaitForUser ? "WaitForUser" : "AutoSend";

        foreach (var item in ChatWindowSubmitModeComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), target, StringComparison.Ordinal))
            {
                ChatWindowSubmitModeComboBox.SelectedItem = item;
                return;
            }
        }

        ChatWindowSubmitModeComboBox.SelectedIndex = 0;
    }

    private VoiceChatWindowSubmitMode GetSelectedChatWindowSubmitMode()
    {
        var tag = (ChatWindowSubmitModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return tag == "WaitForUser"
            ? VoiceChatWindowSubmitMode.WaitForUser
            : VoiceChatWindowSubmitMode.AutoSend;
    }

    private string BuildStatusText()
    {
        var running = _voiceService.CurrentStatus;
        var runtime = running.State == VoiceRuntimeState.Paused
            ? $"{running.Mode} ({running.State})"
            : running.Running
                ? $"{running.Mode} ({running.State})"
                : "Off";
        var nodeMode = _settings.EnableNodeMode ? "enabled" : "disabled";
        var stt = (SpeechToTextProviderComboBox.SelectedItem as ProviderOption)?.Name ?? "Windows Speech Recognition";
        var tts = (TextToSpeechProviderComboBox.SelectedItem as ProviderOption)?.Name ?? "Windows Speech Synthesis";
        var error = string.IsNullOrWhiteSpace(running.LastError)
            ? string.Empty
            : $" Last issue: {running.LastError}.";
        UpdateTroubleshooting(running.LastError);
        return $"Runtime: {runtime}. Node Mode is {nodeMode}. STT: {stt}. TTS: {tts}.{error}";
    }

    public void RefreshStatus()
    {
        StatusTextBlock.Text = BuildStatusText();
    }

    private void UpdateModeInfo()
    {
        var mode = GetSelectedMode();
        ModeInfoBar.Message = mode switch
        {
            VoiceActivationMode.WakeWord => "WakeWord settings are saved now, but NanoWakeWord activation is still the next implementation step.",
            VoiceActivationMode.AlwaysOn => "AlwaysOn is the first active runtime target. It uses Windows speech recognition and turn-based reply playback today.",
            _ => "Voice runtime stays off until you choose a listening mode."
        };
    }

    private void UpdateProviderInfo()
    {
        var stt = SpeechToTextProviderComboBox.SelectedItem as ProviderOption;
        var tts = TextToSpeechProviderComboBox.SelectedItem as ProviderOption;

        var details = new List<string>();
        if (stt != null)
        {
            details.Add($"STT: {stt.Name}");
        }

        if (tts != null)
        {
            details.Add($"TTS: {tts.Name}");
        }

        var fallbackNotice = (stt != null && !string.Equals(stt.Id, VoiceProviderIds.Windows, StringComparison.OrdinalIgnoreCase)) ||
                             (tts != null && !string.Equals(tts.Id, VoiceProviderIds.Windows, StringComparison.OrdinalIgnoreCase))
            ? " Selected non-Windows providers are saved now but will fall back to Windows until their runtime adapters are added."
            : string.Empty;

        ProviderInfoTextBlock.Text =
            $"{string.Join(" · ", details)}. Configure extra providers in {VoiceProviderCatalogService.CatalogFilePath}.{fallbackNotice}";
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
                "To fix this: open Windows Settings, go to Privacy & security > Speech, turn on Online speech recognition, then restart Voice Mode.";
            return;
        }

        if (error.Contains("microphone access is blocked", StringComparison.OrdinalIgnoreCase))
        {
            TroubleshootingPanel.Visibility = Visibility.Visible;
            OpenMicrophoneSettingsButton.Visibility = Visibility.Visible;
            TroubleshootingTextBlock.Text =
                "To fix this: open Windows Settings, go to Privacy & security > Microphone, allow microphone access and enable desktop app access, then restart Voice Mode.";
        }
    }

    private async void OnRefreshDevices(object sender, RoutedEventArgs e)
    {
        await LoadDevicesAsync();
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateModeInfo();
        StatusTextBlock.Text = BuildStatusText();
    }

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateProviderInfo();
        StatusTextBlock.Text = BuildStatusText();
    }

    private void OnOpenSpeechSettings(object sender, RoutedEventArgs e)
    {
        OpenSettingsUri("ms-settings:privacy-speech");
    }

    private void OnOpenMicrophoneSettings(object sender, RoutedEventArgs e)
    {
        OpenSettingsUri("ms-settings:privacy-microphone");
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        var updated = new VoiceSettings
        {
            Mode = GetSelectedMode(),
            Enabled = GetSelectedMode() != VoiceActivationMode.Off,
            ShowConversationToasts = VoiceConversationToastsCheckBox.IsChecked ?? false,
            SpeechToTextProviderId = (SpeechToTextProviderComboBox.SelectedItem as ProviderOption)?.Id ?? VoiceProviderIds.Windows,
            TextToSpeechProviderId = (TextToSpeechProviderComboBox.SelectedItem as ProviderOption)?.Id ?? VoiceProviderIds.Windows,
            InputDeviceId = (InputDeviceComboBox.SelectedItem as DeviceOption)?.DeviceId,
            OutputDeviceId = (OutputDeviceComboBox.SelectedItem as DeviceOption)?.DeviceId,
            SampleRateHz = _settings.Voice.SampleRateHz,
            CaptureChunkMs = _settings.Voice.CaptureChunkMs,
            BargeInEnabled = _settings.Voice.BargeInEnabled,
            WakeWord = new VoiceWakeWordSettings
            {
                Engine = _settings.Voice.WakeWord.Engine,
                ModelId = _settings.Voice.WakeWord.ModelId,
                TriggerThreshold = _settings.Voice.WakeWord.TriggerThreshold,
                TriggerCooldownMs = _settings.Voice.WakeWord.TriggerCooldownMs,
                PreRollMs = _settings.Voice.WakeWord.PreRollMs,
                EndSilenceMs = _settings.Voice.WakeWord.EndSilenceMs
            },
            AlwaysOn = new VoiceAlwaysOnSettings
            {
                MinSpeechMs = _settings.Voice.AlwaysOn.MinSpeechMs,
                EndSilenceMs = _settings.Voice.AlwaysOn.EndSilenceMs,
                MaxUtteranceMs = _settings.Voice.AlwaysOn.MaxUtteranceMs,
                ChatWindowSubmitMode = GetSelectedChatWindowSubmitMode()
            }
        };

        try
        {
            await _voiceService.UpdateSettingsAsync(new VoiceSettingsUpdateArgs
            {
                Settings = updated,
                Persist = true
            });

            if (_settings.EnableNodeMode)
            {
                if (updated.Mode == VoiceActivationMode.Off)
                {
                    await _voiceService.StopAsync(new VoiceStopArgs { Reason = "Voice mode disabled by user" });
                }
                else
                {
                    await _voiceService.StartAsync(new VoiceStartArgs { Mode = updated.Mode });
                }
            }

            Close();
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Failed to save voice settings: {ex.Message}";
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
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

    private sealed record DeviceOption(string? DeviceId, string Name);
    private sealed record ProviderOption(string Id, string Name, string Runtime, string? Description);
}
