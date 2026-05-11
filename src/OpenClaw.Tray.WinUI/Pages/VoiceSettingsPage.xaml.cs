using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using System;
using System.Globalization;
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
    private static string L(string key) => LocalizationHelper.GetString(key);
    private static string Lf(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, LocalizationHelper.GetString(key), args);

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
            ModelStatusText.Text = L("VoiceSettingsPage_StatusModelReady");
            DownloadButtonText.Text = L("VoiceSettingsPage_ButtonReDownload");
        }
        else
        {
            ModelStatusText.Text = L("VoiceSettingsPage_StatusDownloadRequired");
            DownloadButtonText.Text = L("VoiceSettingsPage_ButtonDownloadModel");
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
        if (_hub?.Settings == null) return;

        // Cancel any in-progress Whisper download (only). Piper downloads are
        // independent and keep running.
        _whisperDownloadCts?.Cancel();
        _whisperDownloadCts = new CancellationTokenSource();

        DownloadButton.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;
        ModelStatusText.Text = L("VoiceSettingsPage_StatusDownloading");

        try
        {
            // Throttle UI updates: the underlying download streams in 80 KB
            // chunks, so for a 466 MB model that's ~5,800 progress callbacks
            // — each one Posts to the SyncContext and then queues a
            // DispatcherQueue tick. The dispatcher saturates and the app
            // appears frozen. Coalesce to at most one UI update per ~150 ms,
            // and always force a final 100% update when the download
            // completes so the user never sees a stuck "99%" before "Model
            // ready" appears.
            DateTime lastReportUtc = DateTime.MinValue;
            var progress = new Progress<(long downloaded, long total)>(p =>
            {
                var now = DateTime.UtcNow;
                var isFinal = p.total > 0 && p.downloaded >= p.total;
                if (!isFinal && now - lastReportUtc < TimeSpan.FromMilliseconds(150)) return;
                lastReportUtc = now;
                if (p.total > 0)
                {
                    var pct = (double)p.downloaded / p.total * 100;
                    DownloadProgress.Value = pct;
                    ModelStatusText.Text = Lf("VoiceSettingsPage_StatusDownloadingPct", $"{pct:F0}");
                }
            });

            // Download via the model manager directly so the user can fetch
            // a model even before NodeService has registered the STT
            // capability (which only happens after Connect / StartLocalOnly
            // and only when NodeSttEnabled is true). VoiceService still
            // wraps this same manager when it auto-downloads on first use,
            // so the on-disk result is identical.
            var manager = new OpenClaw.Shared.Audio.WhisperModelManager(
                SettingsManager.SettingsDirectoryPath, new AppLogger());
            // Re-download semantic: when the file is already present the
            // button label flips to "Re-download" (UpdateModelStatus). The
            // download manager short-circuits if the file exists, so we
            // delete first to force a fresh fetch + SHA-256 re-verify.
            manager.DeleteModel(_hub.Settings.SttModelName);
            await manager.DownloadModelAsync(
                _hub.Settings.SttModelName,
                progress,
                _whisperDownloadCts.Token);

            ModelStatusText.Text = L("VoiceSettingsPage_StatusModelReady");
            DownloadButtonText.Text = L("VoiceSettingsPage_ButtonReDownload");
        }
        catch (OperationCanceledException)
        {
            ModelStatusText.Text = L("VoiceSettingsPage_StatusDownloadCanceled");
        }
        catch (Exception ex)
        {
            // Privacy: never put ex.Message in the UI — it can carry URLs,
            // file paths, hash digests, or HTTP body fragments. Log the full
            // detail; show a generic message.
            Logger.Error($"Whisper model download failed: {ex}");
            ModelStatusText.Text = L("VoiceSettingsPage_StatusError");
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
        PiperDownloadButtonText.Text = downloaded
            ? L("VoiceSettingsPage_PiperButtonDownloaded")
            : L("VoiceSettingsPage_PiperButtonDownloadVoice");
        PiperDownloadIcon.Glyph = downloaded ? "\uE73E" : "\uE896";  // checkmark vs download arrow
        PiperDeleteButton.Visibility = downloaded ? Visibility.Visible : Visibility.Collapsed;
        PiperPreviewButton.Visibility = downloaded ? Visibility.Visible : Visibility.Collapsed;

        if (downloaded)
        {
            var sizeMb = voices.GetVoiceSize(voiceId) / (1024d * 1024d);
            PiperStatusText.Text = Lf("VoiceSettingsPage_PiperVoiceReady", $"{sizeMb:F1}");
        }
        else
        {
            PiperStatusText.Text = L("VoiceSettingsPage_PiperVoiceNotDownloaded");
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
        PiperDownloadButtonText.Text = L("VoiceSettingsPage_PiperButtonDownloading");
        PiperDownloadProgress.Visibility = Visibility.Visible;
        PiperDownloadProgress.Value = 0;
        PiperStatusText.Text = L("VoiceSettingsPage_PiperConnecting");

        try
        {
            var voices = new OpenClaw.Shared.Audio.PiperVoiceManager(SettingsManager.SettingsDirectoryPath, new AppLogger());
            // Same throttling story as the Whisper download: ~80 KB per
            // streaming callback × ~150 MB voices = ~1,800 reports. Coalesce
            // to ≥150 ms intervals so we don't choke the dispatcher.
            DateTime lastPiperReportUtc = DateTime.MinValue;
            var progress = new Progress<(long downloaded, long total)>(p =>
            {
                var now = DateTime.UtcNow;
                var isFinal = p.total > 0 && p.downloaded >= p.total;
                if (!isFinal && now - lastPiperReportUtc < TimeSpan.FromMilliseconds(150)) return;
                lastPiperReportUtc = now;
                if (p.total <= 0)
                {
                    PiperDownloadProgress.IsIndeterminate = true;
                    PiperStatusText.Text = Lf("VoiceSettingsPage_PiperProgressIndeterminate", p.downloaded / (1024 * 1024));
                }
                else
                {
                    PiperDownloadProgress.IsIndeterminate = false;
                    PiperDownloadProgress.Value = (double)p.downloaded * 100 / p.total;
                    PiperStatusText.Text = Lf("VoiceSettingsPage_PiperProgressBytes",
                        $"{p.downloaded / (1024d * 1024d):F1}",
                        $"{p.total / (1024d * 1024d):F1}");
                }
            });

            await voices.DownloadVoiceAsync(voiceId, progress, ct);
            PiperStatusText.Text = L("VoiceSettingsPage_PiperExtracting");
            // DownloadVoiceAsync extracts inline before returning, so by the
            // time we get here the voice is fully on disk.
            UpdatePiperVoiceState();
        }
        catch (OperationCanceledException)
        {
            PiperStatusText.Text = L("VoiceSettingsPage_PiperDownloadCanceled");
            UpdatePiperVoiceState();
        }
        catch (Exception ex)
        {
            // The Logger captured full detail; surface a short user-facing
            // message without leaking the URL, hash, or stack frame.
            Logger.Error($"Piper voice download failed: {ex}");
            PiperStatusText.Text = L("VoiceSettingsPage_PiperDownloadFailed");
            PiperDownloadButton.IsEnabled = true;
            PiperDownloadButtonText.Text = L("VoiceSettingsPage_PiperButtonRetry");
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
            PiperStatusText.Text = L("VoiceSettingsPage_PiperDeleted");
            UpdatePiperVoiceState();
        }
        catch (Exception ex)
        {
            Logger.Error($"Piper voice delete failed: {ex}");
            PiperStatusText.Text = L("VoiceSettingsPage_PiperDeleteFailed");
        }
    }

    private async void OnPiperPreviewClick(object sender, RoutedEventArgs e)
    {
        if (_hub?.Settings == null) return;
        if (PiperVoiceCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string voiceId) return;

        PiperPreviewButton.IsEnabled = false;
        var oldContent = PiperPreviewButton.Content;
        PiperPreviewButton.Content = L("VoiceSettingsPage_PreviewButtonPlaying");

        try
        {
            using var tts = new TextToSpeechService(new AppLogger(), _hub.Settings);
            await tts.SpeakAsync(new OpenClaw.Shared.Capabilities.TtsSpeakArgs
            {
                Text = L("VoiceSettingsPage_CompanionPreviewText"),
                Provider = OpenClaw.Shared.Capabilities.TtsCapability.PiperProvider,
                VoiceId = voiceId,
                Interrupt = true
            });
        }
        catch (Exception ex)
        {
            Logger.Error($"Piper voice preview failed: {ex}");
            PiperStatusText.Text = L("VoiceSettingsPage_PiperPreviewFailed");
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
            Logger.Error($"Loading Windows TTS voices failed: {ex}");
            WindowsVoiceCombo.Items.Add(new ComboBoxItem { Content = L("VoiceSettingsPage_VoiceErrorLoading"), IsEnabled = false });
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
        PreviewVoiceButton.Content = L("VoiceSettingsPage_PreviewButtonPlaying");

        try
        {
            var tts = new TextToSpeechService(new AppLogger(), _hub.Settings);
            try
            {
                await tts.SpeakAsync(new OpenClaw.Shared.Capabilities.TtsSpeakArgs
                {
                    Text = L("VoiceSettingsPage_CompanionPreviewText"),
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
            // Show error inline (sanitized — full detail in the log).
            Logger.Error($"Windows TTS preview failed: {ex}");
            PreviewVoiceButton.Content = L("VoiceSettingsPage_StatusError");
            await System.Threading.Tasks.Task.Delay(3000);
        }
        finally
        {
            PreviewVoiceButton.IsEnabled = true;
            PreviewVoiceButton.Content = L("VoiceSettingsPage_PreviewVoiceButtonContent");
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
