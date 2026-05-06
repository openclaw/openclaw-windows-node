using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using System;
using System.Linq;
using System.Threading;

namespace OpenClawTray.Pages;

public sealed partial class VoiceSettingsPage : Page
{
    private HubWindow? _hub;
    private VoiceService? _voiceService;
    private bool _suppressEvents;
    // Per-asset CTS so a Piper download doesn't cancel an in-flight Whisper
    // download (and vice versa). Each download type owns its own token.
    private CancellationTokenSource? _whisperDownloadCts;
    private CancellationTokenSource? _piperDownloadCts;

    public VoiceSettingsPage()
    {
        InitializeComponent();
        // Refresh model + voice status every time the page becomes visible so
        // file-state changes (e.g. a silent Whisper auto-download triggered by
        // the Voice Overlay, or a Piper voice downloaded in another window)
        // propagate without forcing the user to renavigate.
        Loaded += (_, _) =>
        {
            UpdateModelStatus();
            UpdatePiperVoiceState();
        };
    }

    public void Initialize(HubWindow hub, VoiceService? voiceService)
    {
        _hub = hub;
        _voiceService = voiceService;
        LoadSettings();
    }

    private void LoadSettings()
    {
        if (_hub?.Settings == null) return;
        _suppressEvents = true;

        try
        {
            var settings = _hub.Settings;

            SttEnabledToggle.IsOn = settings.NodeSttEnabled;

            // Select model in combo
            for (int i = 0; i < ModelCombo.Items.Count; i++)
            {
                if (ModelCombo.Items[i] is ComboBoxItem item &&
                    string.Equals(item.Tag?.ToString(), settings.SttModelName, StringComparison.OrdinalIgnoreCase))
                {
                    ModelCombo.SelectedIndex = i;
                    break;
                }
            }

            // Select language
            for (int i = 0; i < LanguageCombo.Items.Count; i++)
            {
                if (LanguageCombo.Items[i] is ComboBoxItem item &&
                    string.Equals(item.Tag?.ToString(), settings.SttLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    LanguageCombo.SelectedIndex = i;
                    break;
                }
            }
            if (LanguageCombo.SelectedIndex < 0)
                LanguageCombo.SelectedIndex = 0; // auto

            SilenceSlider.Value = settings.SttSilenceTimeout;
            TtsResponseToggle.IsOn = settings.VoiceTtsEnabled;
            AudioFeedbackToggle.IsOn = settings.VoiceAudioFeedback;

            LoadTtsSettings(settings);
            UpdateModelStatus();
            UpdateCardVisibility();
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void UpdateModelStatus()
    {
        // Determine the selected model. Prefer settings; fall back to the
        // ModelCombo selection if settings haven't been wired yet so the
        // status reflects what's on disk even before Initialize completes.
        var modelName = _hub?.Settings?.SttModelName
            ?? (ModelCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString()
            ?? "base";

        // Check the file directly via WhisperModelManager rather than going
        // through VoiceService — _voiceService can be null if the user reaches
        // this page before NodeService finishes wiring it, and we still want
        // accurate status.
        var manager = new OpenClaw.Shared.Audio.WhisperModelManager(
            SettingsManager.SettingsDirectoryPath, new AppLogger());

        if (manager.IsModelDownloaded(modelName))
        {
            ModelStatusText.Text = "✅ Model ready";
            DownloadButtonText.Text = "Re-download";
        }
        else
        {
            ModelStatusText.Text = "⬇️ Download required";
            DownloadButtonText.Text = "Download Model";
        }
    }

    private void UpdateCardVisibility()
    {
        ModelCard.Opacity = SttEnabledToggle.IsOn ? 1.0 : 0.5;
        ModelCard.IsHitTestVisible = SttEnabledToggle.IsOn;
    }

    private void OnSttToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _hub?.Settings == null) return;
        _hub.Settings.NodeSttEnabled = SttEnabledToggle.IsOn;
        _hub.Settings.Save();
        UpdateCardVisibility();
    }

    private void OnModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _hub?.Settings == null) return;

        if (ModelCombo.SelectedItem is ComboBoxItem item && item.Tag is string modelName)
        {
            _hub.Settings.SttModelName = modelName;
            _hub.Settings.Save();
            UpdateModelStatus();
        }
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _hub?.Settings == null) return;

        if (LanguageCombo.SelectedItem is ComboBoxItem item && item.Tag is string lang)
        {
            _hub.Settings.SttLanguage = lang;
            _hub.Settings.Save();
        }
    }

    private void OnSilenceChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressEvents || _hub?.Settings == null) return;
        _hub.Settings.SttSilenceTimeout = (float)SilenceSlider.Value;
        _hub.Settings.Save();
    }

    private void OnTtsResponseToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _hub?.Settings == null) return;
        _hub.Settings.VoiceTtsEnabled = TtsResponseToggle.IsOn;
        _hub.Settings.Save();
    }

    private void OnAudioFeedbackToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _hub?.Settings == null) return;
        _hub.Settings.VoiceAudioFeedback = AudioFeedbackToggle.IsOn;
        _hub.Settings.Save();
    }

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_voiceService == null || _hub?.Settings == null) return;

        // Cancel any in-progress Whisper download (only). Piper downloads are
        // independent and keep running.
        _whisperDownloadCts?.Cancel();
        _whisperDownloadCts = new CancellationTokenSource();

        DownloadButton.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;
        ModelStatusText.Text = "Downloading...";

        try
        {
            var progress = new Progress<(long downloaded, long total)>(p =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (p.total > 0)
                    {
                        var pct = (double)p.downloaded / p.total * 100;
                        DownloadProgress.Value = pct;
                        ModelStatusText.Text = $"Downloading... {pct:F0}%";
                    }
                });
            });

            await _voiceService.DownloadModelAsync(
                _hub.Settings.SttModelName,
                progress,
                _whisperDownloadCts.Token);

            ModelStatusText.Text = "✅ Model ready";
            DownloadButtonText.Text = "Re-download";
        }
        catch (OperationCanceledException)
        {
            ModelStatusText.Text = "Download canceled";
        }
        catch (Exception ex)
        {
            ModelStatusText.Text = $"❌ {ex.Message}";
        }
        finally
        {
            DownloadButton.IsEnabled = true;
            DownloadProgress.Visibility = Visibility.Collapsed;
        }
    }

    // ── TTS Voice Selection ──

    private void LoadTtsSettings(SettingsManager settings)
    {
        // Provider
        var provider = settings.TtsProvider;
        for (int i = 0; i < TtsProviderCombo.Items.Count; i++)
        {
            if (TtsProviderCombo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), provider, StringComparison.OrdinalIgnoreCase))
            {
                TtsProviderCombo.SelectedIndex = i;
                break;
            }
        }
        if (TtsProviderCombo.SelectedIndex < 0)
            TtsProviderCombo.SelectedIndex = 0;  // default to Piper

        // Piper voice catalog
        PopulatePiperVoices(settings);

        // Windows voices
        PopulateWindowsVoices(settings);

        // ElevenLabs
        ElevenLabsApiKeyBox.Password = settings.TtsElevenLabsApiKey ?? "";
        ElevenLabsVoiceIdBox.Text = settings.TtsElevenLabsVoiceId ?? "";
        ElevenLabsModelBox.Text = settings.TtsElevenLabsModel ?? "";

        UpdateTtsProviderVisibility();
        UpdatePiperVoiceState();
    }

    private void PopulatePiperVoices(SettingsManager settings)
    {
        PiperVoiceCombo.Items.Clear();
        var selected = string.IsNullOrWhiteSpace(settings.TtsPiperVoiceId)
            ? "en_US-amy-low"
            : settings.TtsPiperVoiceId;
        int selectedIdx = 0;

        foreach (var v in OpenClaw.Shared.Audio.PiperVoiceManager.AvailableVoices)
        {
            var item = new ComboBoxItem { Content = v.DisplayName, Tag = v.VoiceId };
            PiperVoiceCombo.Items.Add(item);
            if (string.Equals(v.VoiceId, selected, StringComparison.OrdinalIgnoreCase))
                selectedIdx = PiperVoiceCombo.Items.Count - 1;
        }

        if (PiperVoiceCombo.Items.Count > 0)
            PiperVoiceCombo.SelectedIndex = selectedIdx;
    }

    private void OnPiperVoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _hub?.Settings == null) return;
        if (PiperVoiceCombo.SelectedItem is ComboBoxItem item && item.Tag is string voiceId)
        {
            _hub.Settings.TtsPiperVoiceId = voiceId;
            _hub.Settings.Save();
        }
        UpdatePiperVoiceState();
    }

    /// <summary>
    /// Refresh the Piper download/delete/preview buttons + status text based
    /// on whether the currently-selected voice is on disk. Pure UI; touches
    /// the file system once via PiperVoiceManager.IsVoiceDownloaded.
    /// </summary>
    private void UpdatePiperVoiceState()
    {
        if (_hub?.Settings == null) return;
        if (PiperVoiceCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string voiceId)
            return;

        var voices = new OpenClaw.Shared.Audio.PiperVoiceManager(SettingsManager.SettingsDirectoryPath, new AppLogger());
        var downloaded = voices.IsVoiceDownloaded(voiceId);

        PiperDownloadButton.IsEnabled = !downloaded;
        PiperDownloadButtonText.Text = downloaded ? "Downloaded" : "Download Voice";
        PiperDownloadIcon.Glyph = downloaded ? "\uE73E" : "\uE896";  // checkmark vs download arrow
        PiperDeleteButton.Visibility = downloaded ? Visibility.Visible : Visibility.Collapsed;
        PiperPreviewButton.Visibility = downloaded ? Visibility.Visible : Visibility.Collapsed;

        if (downloaded)
        {
            var sizeMb = voices.GetVoiceSize(voiceId) / (1024d * 1024d);
            PiperStatusText.Text = $"Voice ready on this PC ({sizeMb:F1} MB).";
        }
        else
        {
            PiperStatusText.Text = "Voice not downloaded yet. Click Download to fetch the model (~25–150 MB depending on quality).";
        }
        PiperDownloadProgress.Visibility = Visibility.Collapsed;
    }

    private async void OnPiperDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_hub?.Settings == null) return;
        if (PiperVoiceCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string voiceId) return;

        // Cancel any prior Piper download (only). Whisper downloads are
        // independent and continue running.
        try { _piperDownloadCts?.Cancel(); } catch { /* swallow */ }
        _piperDownloadCts = new CancellationTokenSource();
        var ct = _piperDownloadCts.Token;

        PiperDownloadButton.IsEnabled = false;
        PiperDownloadButtonText.Text = "Downloading…";
        PiperDownloadProgress.Visibility = Visibility.Visible;
        PiperDownloadProgress.Value = 0;
        PiperStatusText.Text = "Connecting to sherpa-onnx releases…";

        try
        {
            var voices = new OpenClaw.Shared.Audio.PiperVoiceManager(SettingsManager.SettingsDirectoryPath, new AppLogger());
            var progress = new Progress<(long downloaded, long total)>(p =>
            {
                if (p.total <= 0)
                {
                    PiperDownloadProgress.IsIndeterminate = true;
                    PiperStatusText.Text = $"Downloading… {p.downloaded / (1024 * 1024)} MB so far";
                }
                else
                {
                    PiperDownloadProgress.IsIndeterminate = false;
                    PiperDownloadProgress.Value = (double)p.downloaded * 100 / p.total;
                    PiperStatusText.Text = $"Downloading… {p.downloaded / (1024d * 1024d):F1} / {p.total / (1024d * 1024d):F1} MB";
                }
            });

            await voices.DownloadVoiceAsync(voiceId, progress, ct);
            PiperStatusText.Text = "Download complete. Extracting…";
            // DownloadVoiceAsync extracts inline before returning, so by the
            // time we get here the voice is fully on disk.
            UpdatePiperVoiceState();
        }
        catch (OperationCanceledException)
        {
            PiperStatusText.Text = "Download canceled.";
            UpdatePiperVoiceState();
        }
        catch (Exception ex)
        {
            // The Logger captured full detail; surface a short user-facing
            // message without leaking the URL or stack frame.
            PiperStatusText.Text = $"Download failed: {ex.Message}";
            PiperDownloadButton.IsEnabled = true;
            PiperDownloadButtonText.Text = "Retry Download";
            PiperDownloadProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void OnPiperDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_hub?.Settings == null) return;
        if (PiperVoiceCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string voiceId) return;

        try
        {
            var voices = new OpenClaw.Shared.Audio.PiperVoiceManager(SettingsManager.SettingsDirectoryPath, new AppLogger());
            voices.DeleteVoice(voiceId);
            PiperStatusText.Text = "Voice deleted.";
            UpdatePiperVoiceState();
        }
        catch (Exception ex)
        {
            PiperStatusText.Text = $"Delete failed: {ex.Message}";
        }
    }

    private async void OnPiperPreviewClick(object sender, RoutedEventArgs e)
    {
        if (_hub?.Settings == null) return;
        if (PiperVoiceCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string voiceId) return;

        PiperPreviewButton.IsEnabled = false;
        var oldContent = PiperPreviewButton.Content;
        PiperPreviewButton.Content = "▶ Playing…";

        try
        {
            using var tts = new TextToSpeechService(new AppLogger(), _hub.Settings);
            await tts.SpeakAsync(new OpenClaw.Shared.Capabilities.TtsSpeakArgs
            {
                Text = "Hello! This is a Piper voice running locally on your PC.",
                Provider = OpenClaw.Shared.Capabilities.TtsCapability.PiperProvider,
                VoiceId = voiceId,
                Interrupt = true
            });
        }
        catch (Exception ex)
        {
            PiperStatusText.Text = $"Preview failed: {ex.Message}";
        }
        finally
        {
            PiperPreviewButton.IsEnabled = true;
            PiperPreviewButton.Content = oldContent;
        }
    }

    private void PopulateWindowsVoices(SettingsManager settings)
    {
        WindowsVoiceCombo.Items.Clear();

        try
        {
            var voices = global::Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices;
            int selectedIdx = 0;

            foreach (var voice in voices)
            {
                var label = $"{voice.DisplayName} ({voice.Language})";
                var item = new ComboBoxItem { Content = label, Tag = voice.Id };
                WindowsVoiceCombo.Items.Add(item);

                // Match current setting
                if (!string.IsNullOrEmpty(settings.TtsWindowsVoiceId) &&
                    (string.Equals(voice.Id, settings.TtsWindowsVoiceId, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(voice.DisplayName, settings.TtsWindowsVoiceId, StringComparison.OrdinalIgnoreCase)))
                {
                    selectedIdx = WindowsVoiceCombo.Items.Count - 1;
                }
            }

            if (WindowsVoiceCombo.Items.Count > 0)
                WindowsVoiceCombo.SelectedIndex = selectedIdx;
        }
        catch (Exception ex)
        {
            WindowsVoiceCombo.Items.Add(new ComboBoxItem { Content = $"Error loading voices: {ex.Message}", IsEnabled = false });
        }
    }

    private void UpdateTtsProviderVisibility()
    {
        var providerTag = (TtsProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? TtsCapability.PiperProvider;
        var isPiper = string.Equals(providerTag, "piper", StringComparison.OrdinalIgnoreCase);
        var isElevenLabs = string.Equals(providerTag, "elevenlabs", StringComparison.OrdinalIgnoreCase);
        var isWindows = !isPiper && !isElevenLabs;

        PiperVoicePanel.Visibility = isPiper ? Visibility.Visible : Visibility.Collapsed;
        WindowsVoicePanel.Visibility = isWindows ? Visibility.Visible : Visibility.Collapsed;
        ElevenLabsPanel.Visibility = isElevenLabs ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTtsProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _hub?.Settings == null) return;

        if (TtsProviderCombo.SelectedItem is ComboBoxItem item && item.Tag is string provider)
        {
            _hub.Settings.TtsProvider = provider;
            _hub.Settings.Save();
        }
        UpdateTtsProviderVisibility();
    }

    private void OnWindowsVoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _hub?.Settings == null) return;

        if (WindowsVoiceCombo.SelectedItem is ComboBoxItem item && item.Tag is string voiceId)
        {
            _hub.Settings.TtsWindowsVoiceId = voiceId;
            _hub.Settings.Save();
        }
    }

    private async void OnPreviewVoiceClick(object sender, RoutedEventArgs e)
    {
        if (_hub?.Settings == null) return;

        PreviewVoiceButton.IsEnabled = false;
        PreviewVoiceButton.Content = "▶ Playing...";

        try
        {
            var tts = new TextToSpeechService(new AppLogger(), _hub.Settings);
            try
            {
                await tts.SpeakAsync(new OpenClaw.Shared.Capabilities.TtsSpeakArgs
                {
                    Text = "Hello! I'm Molty, your voice assistant. How can I help you today?",
                    Provider = _hub.Settings.TtsProvider,
                    VoiceId = WindowsVoiceCombo.SelectedItem is ComboBoxItem item ? item.Tag?.ToString() : null,
                    Interrupt = true
                });
            }
            finally
            {
                tts.Dispose();
            }
        }
        catch (Exception ex)
        {
            // Show error inline
            PreviewVoiceButton.Content = $"❌ {ex.Message}";
            await System.Threading.Tasks.Task.Delay(3000);
        }
        finally
        {
            PreviewVoiceButton.IsEnabled = true;
            PreviewVoiceButton.Content = "▶ Preview Voice";
        }
    }

    private void OnElevenLabsKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _hub?.Settings == null) return;
        _hub.Settings.TtsElevenLabsApiKey = ElevenLabsApiKeyBox.Password;
        _hub.Settings.Save();
    }

    private void OnElevenLabsVoiceIdChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents || _hub?.Settings == null) return;
        _hub.Settings.TtsElevenLabsVoiceId = ElevenLabsVoiceIdBox.Text;
        _hub.Settings.Save();
    }

    private void OnElevenLabsModelChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents || _hub?.Settings == null) return;
        _hub.Settings.TtsElevenLabsModel = ElevenLabsModelBox.Text;
        _hub.Settings.Save();
    }
}
