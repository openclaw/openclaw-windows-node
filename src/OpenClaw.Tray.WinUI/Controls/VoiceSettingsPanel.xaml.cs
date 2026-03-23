using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Services;
using OpenClawTray.Services.Voice;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OpenClawTray.Controls;

public sealed partial class VoiceSettingsPanel : UserControl
{
    private SettingsManager? _settings;
    private VoiceService? _voiceService;
    private VoiceProviderConfigurationStore _voiceProviderConfigurationDraft = new();
    private string _activeTtsProviderId = VoiceProviderIds.Windows;
    private bool _updatingVoiceProviderFields;
    private List<VoiceProviderOption> _speechToTextOptions = new();
    private List<VoiceProviderOption> _textToSpeechOptions = new();
    private List<DeviceOption> _inputOptions = new();
    private List<DeviceOption> _outputOptions = new();
    private List<string> _activeTtsModelOptions = new();

    public VoiceSettingsPanel()
    {
        InitializeComponent();
    }

    public void Initialize(SettingsManager settings, VoiceService voiceService)
    {
        _settings = settings;
        _voiceService = voiceService;

        LoadVoiceSettings();
        _ = LoadVoiceDevicesAsync();
    }

    public void ApplyTo(SettingsManager settings)
    {
        CaptureSelectedVoiceProviderSettings();

        settings.Voice = new VoiceSettings
        {
            Mode = GetSelectedVoiceMode(),
            Enabled = GetSelectedVoiceMode() != VoiceActivationMode.Off,
            ShowConversationToasts = VoiceConversationToastsCheckBox.IsChecked ?? false,
            SpeechToTextProviderId = (VoiceSpeechToTextProviderComboBox.SelectedItem as VoiceProviderOption)?.Id ?? VoiceProviderIds.Windows,
            TextToSpeechProviderId = (VoiceTextToSpeechProviderComboBox.SelectedItem as VoiceProviderOption)?.Id ?? VoiceProviderIds.Windows,
            InputDeviceId = (VoiceInputDeviceComboBox.SelectedItem as DeviceOption)?.DeviceId,
            OutputDeviceId = (VoiceOutputDeviceComboBox.SelectedItem as DeviceOption)?.DeviceId,
            SampleRateHz = settings.Voice.SampleRateHz,
            CaptureChunkMs = settings.Voice.CaptureChunkMs,
            BargeInEnabled = settings.Voice.BargeInEnabled,
            VoiceWake = new VoiceWakeSettings
            {
                Engine = settings.Voice.VoiceWake.Engine,
                ModelId = settings.Voice.VoiceWake.ModelId,
                TriggerThreshold = settings.Voice.VoiceWake.TriggerThreshold,
                TriggerCooldownMs = settings.Voice.VoiceWake.TriggerCooldownMs,
                PreRollMs = settings.Voice.VoiceWake.PreRollMs,
                EndSilenceMs = settings.Voice.VoiceWake.EndSilenceMs
            },
            TalkMode = new TalkModeSettings
            {
                MinSpeechMs = settings.Voice.TalkMode.MinSpeechMs,
                EndSilenceMs = settings.Voice.TalkMode.EndSilenceMs,
                MaxUtteranceMs = settings.Voice.TalkMode.MaxUtteranceMs,
                ChatWindowSubmitMode = GetSelectedChatWindowSubmitMode()
            }
        };
        settings.VoiceProviderConfiguration = _voiceProviderConfigurationDraft.Clone();
    }

    private void LoadVoiceSettings()
    {
        if (_settings == null || _voiceService == null)
        {
            return;
        }

        _voiceProviderConfigurationDraft = _settings.VoiceProviderConfiguration.Clone();
        LoadVoiceProviders();
        SelectVoiceMode(_settings.Voice.Mode);
        SelectChatWindowSubmitMode(_settings.Voice.TalkMode.ChatWindowSubmitMode);
        VoiceConversationToastsCheckBox.IsChecked = _settings.Voice.ShowConversationToasts;
        UpdateVoiceProviderSettingsEditor();
        UpdateVoiceSettingsInfo();
    }

    private void LoadVoiceProviders()
    {
        var catalog = _voiceService!.GetProviderCatalog();

        _speechToTextOptions = catalog.SpeechToTextProviders
            .Select(Clone)
            .ToList();
        _textToSpeechOptions = catalog.TextToSpeechProviders
            .Select(Clone)
            .ToList();

        VoiceSpeechToTextProviderComboBox.ItemsSource = _speechToTextOptions;
        VoiceTextToSpeechProviderComboBox.ItemsSource = _textToSpeechOptions;

        VoiceSpeechToTextProviderComboBox.SelectedItem =
            _speechToTextOptions.FirstOrDefault(p => p.Id == _settings!.Voice.SpeechToTextProviderId)
            ?? _speechToTextOptions.FirstOrDefault();
        VoiceTextToSpeechProviderComboBox.SelectedItem =
            _textToSpeechOptions.FirstOrDefault(p => p.Id == _settings!.Voice.TextToSpeechProviderId)
            ?? _textToSpeechOptions.FirstOrDefault();
    }

    private async Task LoadVoiceDevicesAsync()
    {
        if (_settings == null || _voiceService == null)
        {
            return;
        }

        try
        {
            VoiceSettingsInfoTextBlock.Text = "Loading voice devices...";
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

            VoiceInputDeviceComboBox.ItemsSource = _inputOptions;
            VoiceOutputDeviceComboBox.ItemsSource = _outputOptions;

            VoiceInputDeviceComboBox.SelectedItem = _inputOptions.FirstOrDefault(o => o.DeviceId == _settings.Voice.InputDeviceId) ?? _inputOptions[0];
            VoiceOutputDeviceComboBox.SelectedItem = _outputOptions.FirstOrDefault(o => o.DeviceId == _settings.Voice.OutputDeviceId) ?? _outputOptions[0];

            UpdateVoiceSettingsInfo();
        }
        catch (Exception ex)
        {
            VoiceSettingsInfoTextBlock.Text = $"Failed to load voice devices: {ex.Message}";
        }
    }

    private void SelectVoiceMode(VoiceActivationMode mode)
    {
        var target = mode switch
        {
            VoiceActivationMode.VoiceWake => "VoiceWake",
            VoiceActivationMode.TalkMode => "TalkMode",
            _ => "Off"
        };

        foreach (var item in VoiceModeComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), target, StringComparison.Ordinal))
            {
                VoiceModeComboBox.SelectedItem = item;
                return;
            }
        }

        VoiceModeComboBox.SelectedIndex = 0;
    }

    private VoiceActivationMode GetSelectedVoiceMode()
    {
        var tag = (VoiceModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return tag switch
        {
            "VoiceWake" => VoiceActivationMode.VoiceWake,
            "TalkMode" => VoiceActivationMode.TalkMode,
            _ => VoiceActivationMode.Off
        };
    }

    private void SelectChatWindowSubmitMode(VoiceChatWindowSubmitMode mode)
    {
        var target = mode == VoiceChatWindowSubmitMode.WaitForUser ? "WaitForUser" : "AutoSend";

        foreach (var item in VoiceChatWindowSubmitModeComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), target, StringComparison.Ordinal))
            {
                VoiceChatWindowSubmitModeComboBox.SelectedItem = item;
                return;
            }
        }

        VoiceChatWindowSubmitModeComboBox.SelectedIndex = 0;
    }

    private VoiceChatWindowSubmitMode GetSelectedChatWindowSubmitMode()
    {
        var tag = (VoiceChatWindowSubmitModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return tag == "WaitForUser"
            ? VoiceChatWindowSubmitMode.WaitForUser
            : VoiceChatWindowSubmitMode.AutoSend;
    }

    private void UpdateVoiceSettingsInfo()
    {
        var stt = (VoiceSpeechToTextProviderComboBox.SelectedItem as VoiceProviderOption)?.Name ?? "Windows Speech Recognition";
        var tts = (VoiceTextToSpeechProviderComboBox.SelectedItem as VoiceProviderOption)?.Name ?? "Windows Speech Synthesis";
        var input = (VoiceInputDeviceComboBox.SelectedItem as DeviceOption)?.Name ?? "System default microphone";
        var output = (VoiceOutputDeviceComboBox.SelectedItem as DeviceOption)?.Name ?? "System default speaker";
        var fallbackNotice = string.Empty;

        if (VoiceTextToSpeechProviderComboBox.SelectedItem is VoiceProviderOption ttsOption &&
            !VoiceProviderCatalogService.SupportsTextToSpeechRuntime(ttsOption.Id))
        {
            fallbackNotice = " Unsupported TTS providers will fall back to Windows until their runtime adapters are added.";
        }

        VoiceSettingsInfoTextBlock.Text =
            $"Mode: {VoiceDisplayHelper.GetModeLabel(GetSelectedVoiceMode())}. STT: {stt}. TTS: {tts}. Listen: {input}. Talk: {output}.{fallbackNotice}";
    }

    private void UpdateVoiceProviderSettingsEditor()
    {
        var providerId = GetSelectedTextToSpeechProviderId();
        var showProviderSettings = !string.Equals(providerId, VoiceProviderIds.Windows, StringComparison.OrdinalIgnoreCase);

        VoiceTtsProviderSettingsPanel.Visibility = showProviderSettings ? Visibility.Visible : Visibility.Collapsed;
        if (!showProviderSettings)
        {
            _activeTtsProviderId = VoiceProviderIds.Windows;
            return;
        }

        var provider = GetSelectedTextToSpeechProvider();
        var apiKeySetting = FindSetting(provider, VoiceProviderSettingKeys.ApiKey);
        var modelSetting = FindSetting(provider, VoiceProviderSettingKeys.Model);
        var voiceIdSetting = FindSetting(provider, VoiceProviderSettingKeys.VoiceId);
        var voiceSettingsJsonSetting = FindSetting(provider, VoiceProviderSettingKeys.VoiceSettingsJson);
        var modelValue = GetProviderValue(providerId, modelSetting) ?? string.Empty;

        _updatingVoiceProviderFields = true;
        try
        {
            VoiceTtsProviderSettingsTitleTextBlock.Text = $"{GetSelectedTextToSpeechProviderName().ToUpperInvariant()} SETTINGS";
            VoiceTtsApiKeyPasswordBox.Header = apiKeySetting?.Label ?? "API key";
            VoiceTtsApiKeyPasswordBox.Visibility = apiKeySetting != null ? Visibility.Visible : Visibility.Collapsed;
            VoiceTtsApiKeyPasswordBox.Password = GetProviderValue(providerId, apiKeySetting) ?? string.Empty;

            _activeTtsModelOptions = modelSetting?.Options
                .Where(option => !string.IsNullOrWhiteSpace(option))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
                ?? [];

            if (_activeTtsModelOptions.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(modelValue) &&
                    !_activeTtsModelOptions.Contains(modelValue, StringComparer.OrdinalIgnoreCase))
                {
                    _activeTtsModelOptions.Insert(0, modelValue);
                }

                VoiceTtsModelComboBox.Header = modelSetting?.Label ?? "Model";
                VoiceTtsModelComboBox.ItemsSource = _activeTtsModelOptions;
                VoiceTtsModelComboBox.SelectedItem = _activeTtsModelOptions
                    .FirstOrDefault(option => string.Equals(option, modelValue, StringComparison.OrdinalIgnoreCase))
                    ?? _activeTtsModelOptions.FirstOrDefault();
                VoiceTtsModelComboBox.Visibility = Visibility.Visible;
                VoiceTtsModelTextBox.Visibility = Visibility.Collapsed;
            }
            else
            {
                VoiceTtsModelTextBox.Header = modelSetting?.Label ?? "Model";
                VoiceTtsModelTextBox.PlaceholderText = modelSetting?.Placeholder ?? string.Empty;
                VoiceTtsModelTextBox.Visibility = modelSetting != null ? Visibility.Visible : Visibility.Collapsed;
                VoiceTtsModelTextBox.Text = modelValue;
                VoiceTtsModelComboBox.ItemsSource = null;
                VoiceTtsModelComboBox.SelectedItem = null;
                VoiceTtsModelComboBox.Visibility = Visibility.Collapsed;
            }

            VoiceTtsVoiceIdTextBox.Header = voiceIdSetting?.Label ?? "Voice ID";
            VoiceTtsVoiceIdTextBox.PlaceholderText = voiceIdSetting?.Placeholder ?? string.Empty;
            VoiceTtsVoiceIdTextBox.Visibility = voiceIdSetting != null ? Visibility.Visible : Visibility.Collapsed;
            VoiceTtsVoiceIdTextBox.Text = GetProviderValue(providerId, voiceIdSetting) ?? string.Empty;

            VoiceTtsVoiceSettingsJsonTextBox.Header = voiceSettingsJsonSetting?.Label ?? "Voice settings JSON";
            VoiceTtsVoiceSettingsJsonTextBox.PlaceholderText = voiceSettingsJsonSetting?.Placeholder ?? string.Empty;
            VoiceTtsVoiceSettingsJsonTextBox.Visibility = voiceSettingsJsonSetting != null ? Visibility.Visible : Visibility.Collapsed;
            VoiceTtsVoiceSettingsJsonTextBox.Text = GetProviderValue(providerId, voiceSettingsJsonSetting) ?? string.Empty;
            _activeTtsProviderId = providerId;
        }
        finally
        {
            _updatingVoiceProviderFields = false;
        }
    }

    private string GetSelectedTextToSpeechProviderId()
    {
        return (VoiceTextToSpeechProviderComboBox.SelectedItem as VoiceProviderOption)?.Id ?? VoiceProviderIds.Windows;
    }

    private string GetSelectedTextToSpeechProviderName()
    {
        return (VoiceTextToSpeechProviderComboBox.SelectedItem as VoiceProviderOption)?.Name ?? "Provider";
    }

    private VoiceProviderOption? GetSelectedTextToSpeechProvider()
    {
        return VoiceTextToSpeechProviderComboBox.SelectedItem as VoiceProviderOption;
    }

    private void CaptureSelectedVoiceProviderSettings()
    {
        if (_updatingVoiceProviderFields)
        {
            return;
        }

        var providerId = _activeTtsProviderId;
        if (string.Equals(providerId, VoiceProviderIds.Windows, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var provider = _textToSpeechOptions.FirstOrDefault(option =>
            string.Equals(option.Id, providerId, StringComparison.OrdinalIgnoreCase));
        SetProviderValue(providerId, FindSetting(provider, VoiceProviderSettingKeys.ApiKey), VoiceTtsApiKeyPasswordBox.Password);
        SetProviderValue(providerId, FindSetting(provider, VoiceProviderSettingKeys.Model), GetSelectedProviderModelValue());
        SetProviderValue(providerId, FindSetting(provider, VoiceProviderSettingKeys.VoiceId), VoiceTtsVoiceIdTextBox.Text);
        SetProviderValue(providerId, FindSetting(provider, VoiceProviderSettingKeys.VoiceSettingsJson), VoiceTtsVoiceSettingsJsonTextBox.Text);
    }

    private async void OnRefreshVoiceDevices(object sender, RoutedEventArgs e)
    {
        await LoadVoiceDevicesAsync();
    }

    private void OnVoiceModeChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateVoiceSettingsInfo();
    }

    private void OnVoiceProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        CaptureSelectedVoiceProviderSettings();
        UpdateVoiceProviderSettingsEditor();
        UpdateVoiceSettingsInfo();
    }

    private void OnVoiceProviderSettingsChanged(object sender, RoutedEventArgs e)
    {
        CaptureSelectedVoiceProviderSettings();
    }

    private string? GetProviderValue(string providerId, VoiceProviderSettingDefinition? setting)
    {
        if (setting == null)
        {
            return null;
        }

        return _voiceProviderConfigurationDraft.GetValue(providerId, setting.Key) ?? setting.DefaultValue;
    }

    private string? GetSelectedProviderModelValue()
    {
        if (VoiceTtsModelComboBox.Visibility == Visibility.Visible)
        {
            return VoiceTtsModelComboBox.SelectedItem?.ToString();
        }

        return VoiceTtsModelTextBox.Text;
    }

    private sealed record DeviceOption(string? DeviceId, string Name);

    private void SetProviderValue(
        string providerId,
        VoiceProviderSettingDefinition? setting,
        string? value)
    {
        if (setting == null)
        {
            return;
        }

        var normalized = string.IsNullOrWhiteSpace(value)
            ? setting.DefaultValue
            : value.Trim();
        _voiceProviderConfigurationDraft.SetValue(providerId, setting.Key, normalized);
    }

    private static VoiceProviderSettingDefinition? FindSetting(VoiceProviderOption? provider, string settingKey)
    {
        return provider?.Settings.FirstOrDefault(setting =>
            string.Equals(setting.Key, settingKey, StringComparison.OrdinalIgnoreCase));
    }

    private static VoiceProviderOption Clone(VoiceProviderOption source)
    {
        return new VoiceProviderOption
        {
            Id = source.Id,
            Name = source.Name,
            Runtime = source.Runtime,
            Enabled = source.Enabled,
            Description = source.Description,
            Settings = source.Settings
                .Select(setting => new VoiceProviderSettingDefinition
                {
                    Key = setting.Key,
                    Label = setting.Label,
                    Secret = setting.Secret,
                    DefaultValue = setting.DefaultValue,
                    Placeholder = setting.Placeholder,
                    Description = setting.Description,
                    Required = setting.Required,
                    JsonValue = setting.JsonValue,
                    Options = setting.Options.ToList()
                })
                .ToList(),
            TextToSpeechHttp = source.TextToSpeechHttp == null
                ? null
                : new VoiceTextToSpeechHttpContract
                {
                    EndpointTemplate = source.TextToSpeechHttp.EndpointTemplate,
                    HttpMethod = source.TextToSpeechHttp.HttpMethod,
                    AuthenticationHeaderName = source.TextToSpeechHttp.AuthenticationHeaderName,
                    AuthenticationScheme = source.TextToSpeechHttp.AuthenticationScheme,
                    ApiKeySettingKey = source.TextToSpeechHttp.ApiKeySettingKey,
                    RequestContentType = source.TextToSpeechHttp.RequestContentType,
                    RequestBodyTemplate = source.TextToSpeechHttp.RequestBodyTemplate,
                    ResponseAudioMode = source.TextToSpeechHttp.ResponseAudioMode,
                    ResponseAudioJsonPath = source.TextToSpeechHttp.ResponseAudioJsonPath,
                    ResponseStatusCodeJsonPath = source.TextToSpeechHttp.ResponseStatusCodeJsonPath,
                    ResponseStatusMessageJsonPath = source.TextToSpeechHttp.ResponseStatusMessageJsonPath,
                    SuccessStatusValue = source.TextToSpeechHttp.SuccessStatusValue,
                    OutputContentType = source.TextToSpeechHttp.OutputContentType
                }
        };
    }
}
