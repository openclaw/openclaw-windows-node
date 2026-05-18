using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace OpenClawTray.Pages;

public sealed partial class VoiceSettingsPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current;
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
        Loaded += (_, _) =>
        {
            UpdateModelStatus();
            UpdatePiperVoiceState();
        };
        Unloaded += async (_, _) =>
        {
            if (App.Current is App app)
                app.SpeakerMuteChanged -= OnAppSpeakerMuteChanged;

            if (_inlineTestVoiceService != null)
            {
                await StopInlineTestAsync();
                await _inlineTestVoiceService.DisposeAsync();
                _inlineTestVoiceService = null;
            }
        };
    }

    public void Initialize(VoiceService? voiceService)
    {
        _voiceService = voiceService;
        if (App.Current is App app)
        {
            app.SpeakerMuteChanged -= OnAppSpeakerMuteChanged;
            app.SpeakerMuteChanged += OnAppSpeakerMuteChanged;
        }
        LoadSettings();
    }

    private void OnAppSpeakerMuteChanged(bool muted)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            _suppressEvents = true;
            TtsResponseToggle.IsOn = !muted;
            _suppressEvents = false;
        });
    }

    private void LoadSettings()
    {
        if (CurrentApp.Settings == null) return;
        _suppressEvents = true;

        try
        {
            var settings = CurrentApp.Settings;

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
        var modelName = CurrentApp.Settings?.SttModelName
            ?? (ModelCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString()
            ?? "base";

        // Check the file directly via WhisperModelManager rather than going
        // through VoiceService â€” _voiceService can be null if the user reaches
        // this page before NodeService finishes wiring it, and we still want
        // accurate status.
        var manager = new OpenClaw.Shared.Audio.WhisperModelManager(
            SettingsManager.SettingsDirectoryPath, new AppLogger());

        if (manager.IsModelDownloaded(modelName))
        {
            ModelStatusText.Text = L("VoiceSettingsPage_StatusModelReady");
            DownloadButtonText.Text = L("VoiceSettingsPage_ButtonReDownload");
            TestVoiceButton.Visibility = Visibility.Visible;
        }
        else
        {
            ModelStatusText.Text = L("VoiceSettingsPage_StatusDownloadRequired");
            DownloadButtonText.Text = L("VoiceSettingsPage_ButtonDownloadModel");
            TestVoiceButton.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateCardVisibility()
    {
        ModelCard.Opacity = SttEnabledToggle.IsOn ? 1.0 : 0.5;
        ModelCard.IsHitTestVisible = SttEnabledToggle.IsOn;
    }

    private void OnSttToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || CurrentApp.Settings == null) return;
        CurrentApp.Settings.NodeSttEnabled = SttEnabledToggle.IsOn;
        CurrentApp.Settings.Save();
        UpdateCardVisibility();
    }

    private void OnModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || CurrentApp.Settings == null) return;

        if (ModelCombo.SelectedItem is ComboBoxItem item && item.Tag is string modelName)
        {
            CurrentApp.Settings.SttModelName = modelName;
            CurrentApp.Settings.Save();
            UpdateModelStatus();
        }
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || CurrentApp.Settings == null) return;

        if (LanguageCombo.SelectedItem is ComboBoxItem item && item.Tag is string lang)
        {
            CurrentApp.Settings.SttLanguage = lang;
            CurrentApp.Settings.Save();
        }
    }

    private void OnSilenceChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressEvents || CurrentApp.Settings == null) return;
        CurrentApp.Settings.SttSilenceTimeout = (float)SilenceSlider.Value;
        CurrentApp.Settings.Save();
    }

    private void OnTtsResponseToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || CurrentApp.Settings == null) return;
        // Use centralized SetChatSpeakerMuted which persists + broadcasts
        (App.Current as App)?.SetChatSpeakerMuted(!TtsResponseToggle.IsOn);
    }

    private void OnAudioFeedbackToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || CurrentApp.Settings == null) return;
        CurrentApp.Settings.VoiceAudioFeedback = AudioFeedbackToggle.IsOn;
        CurrentApp.Settings.Save();
    }

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (CurrentApp.Settings == null) return;

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
            // â€” each one Posts to the SyncContext and then queues a
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
            manager.DeleteModel(CurrentApp.Settings.SttModelName);
            await manager.DownloadModelAsync(
                CurrentApp.Settings.SttModelName,
                progress,
                _whisperDownloadCts.Token);

            ModelStatusText.Text = L("VoiceSettingsPage_StatusModelReady");
            DownloadButtonText.Text = L("VoiceSettingsPage_ButtonReDownload");
            TestVoiceButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            ModelStatusText.Text = L("VoiceSettingsPage_StatusDownloadCanceled");
            TestVoiceButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            // Privacy: never put ex.Message in the UI â€” it can carry URLs,
            // file paths, hash digests, or HTTP body fragments. Log the full
            // detail; show a generic message.
            Logger.Error($"Whisper model download failed: {ex}");
            ModelStatusText.Text = L("VoiceSettingsPage_StatusError");
            TestVoiceButton.Visibility = Visibility.Collapsed;
        }
        finally
        {
            DownloadButton.IsEnabled = true;
            DownloadProgress.Visibility = Visibility.Collapsed;
        }
    }

    // â”€â”€ TTS Voice Selection â”€â”€

    private async void OnTestVoiceClick(object sender, RoutedEventArgs e)
    {
        if (CurrentApp.Settings == null) return;

        // Toggle: hide button, show inline test panel
        TestVoiceButton.Visibility = Visibility.Collapsed;
        InlineTestArea.Visibility = Visibility.Visible;
        InlineTestTranscriptPanel.Children.Clear();
        InlineTestTranscriptScroll.Visibility = Visibility.Collapsed;
        _lastInlineTestBubbleText = null;
        SetInlineTestStatus("");
        InlineTestPillHost.Visibility = Visibility.Collapsed;
    }

    private async void OnInlineTestCloseClick(object sender, RoutedEventArgs e)
    {
        await StopInlineTestAsync();
        InlineTestArea.Visibility = Visibility.Collapsed;
        TestVoiceButton.Visibility = Visibility.Visible;
    }

    private VoiceService? _inlineTestVoiceService;
    private bool _inlineTestListening;
    private bool _inlineTestHandlersAttached;
    private Border[]? _inlineTestPillBars;
    private Border? _inlineTestPillDot;
    private Border? _inlineTestPillContainer;
    private TextBlock? _lastInlineTestBubbleText;

    private void SetInlineTestStatus(string text)
    {
        InlineTestStatus.Text = text;
        InlineTestStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void EnsureInlineTestPill()
    {
        if (_inlineTestPillContainer != null) return;

        var accentColor = (global::Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
        _inlineTestPillDot = new Border
        {
            Width = 6, Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Red),
            VerticalAlignment = VerticalAlignment.Center
        };
        var pillLabel = new TextBlock
        {
            Text = "Recording",
            FontSize = 11,
            Foreground = new SolidColorBrush(accentColor),
            VerticalAlignment = VerticalAlignment.Center
        };
        var pillWavePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1.5, VerticalAlignment = VerticalAlignment.Center };
        _inlineTestPillBars = new Border[16];
        for (int bi = 0; bi < 16; bi++)
        {
            var bar = new Border
            {
                Width = 2, Height = 3,
                CornerRadius = new CornerRadius(1),
                Background = new SolidColorBrush(accentColor),
                VerticalAlignment = VerticalAlignment.Center
            };
            _inlineTestPillBars[bi] = bar;
            pillWavePanel.Children.Add(bar);
        }
        var pillContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        pillContent.Children.Add(_inlineTestPillDot);
        pillContent.Children.Add(pillLabel);
        pillContent.Children.Add(pillWavePanel);
        _inlineTestPillContainer = new Border
        {
            Child = pillContent,
            Padding = new Thickness(10, 5, 12, 5),
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(accentColor) { Opacity = 0.1 },
            BorderBrush = new SolidColorBrush(accentColor) { Opacity = 0.3 },
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        InlineTestPillHost.Children.Add(_inlineTestPillContainer);
    }

    private async void OnInlineTestStartClick(object sender, RoutedEventArgs e)
    {
        if (CurrentApp.Settings == null) return;
        InlineTestStartBtn.IsEnabled = false;

        if (!_inlineTestListening)
        {
            try
            {
                _inlineTestVoiceService ??= new VoiceService(new AppLogger(), CurrentApp.Settings);
                _inlineTestListening = true;
                InlineTestBtnIcon.Glyph = "\uE71A";
                InlineTestBtnLabel.Text = "Stop";
                SetInlineTestStatus("");
                EnsureInlineTestPill();
                _inlineTestPillContainer!.Visibility = Visibility.Visible;
                InlineTestPillHost.Visibility = Visibility.Visible;

                if (!_inlineTestHandlersAttached)
                {
                    _inlineTestVoiceService.TranscriptionReceived += OnInlineTestTranscription;
                    _inlineTestVoiceService.AudioLevelChanged += OnInlineTestAudioLevel;
                    _inlineTestVoiceService.SpeakingChanged += OnInlineTestSpeaking;
                    _inlineTestHandlersAttached = true;
                }
                await _inlineTestVoiceService.StartVoiceChatAsync();
                SetInlineTestStatus("");
            }
            catch (Exception ex)
            {
                Logger.Error($"Inline voice test failed: {ex}");
                SetInlineTestStatus("Could not start voice input");
                _inlineTestListening = false;
                InlineTestBtnIcon.Glyph = "\uE720";
                InlineTestBtnLabel.Text = "Record";
                InlineTestPillHost.Visibility = Visibility.Collapsed;
                if (_inlineTestPillContainer != null)
                    _inlineTestPillContainer.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            await StopInlineTestAsync();
        }

        InlineTestStartBtn.IsEnabled = true;
    }

    private async Task StopInlineTestAsync()
    {
        if (_inlineTestVoiceService != null && _inlineTestListening)
        {
            try { await _inlineTestVoiceService.StopAsync(); } catch { }
        }
        _inlineTestListening = false;
        InlineTestBtnIcon.Glyph = "\uE720";
        InlineTestBtnLabel.Text = "Record";
        SetInlineTestStatus(InlineTestTranscriptPanel.Children.Count > 0 ? "" : "Done");
        InlineTestPillHost.Visibility = Visibility.Collapsed;
        if (_inlineTestPillContainer != null)
            _inlineTestPillContainer.Visibility = Visibility.Collapsed;
        if (_inlineTestHandlersAttached && _inlineTestVoiceService != null)
        {
            _inlineTestVoiceService.TranscriptionReceived -= OnInlineTestTranscription;
            _inlineTestVoiceService.AudioLevelChanged -= OnInlineTestAudioLevel;
            _inlineTestVoiceService.SpeakingChanged -= OnInlineTestSpeaking;
            _inlineTestHandlersAttached = false;
        }
    }

    private void OnInlineTestTranscription(string text)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            InlineTestTranscriptScroll.Visibility = Visibility.Visible;

            // Consolidate rapid segments into same bubble
            if (_lastInlineTestBubbleText != null)
            {
                _lastInlineTestBubbleText.Text += " " + text;
            }
            else
            {
                var accentColor = (global::Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
                var bubbleText = new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
                };
                var bubble = new Border
                {
                    Child = bubbleText,
                    Background = new SolidColorBrush(accentColor),
                    CornerRadius = new CornerRadius(12, 12, 4, 12),
                    Padding = new Thickness(12, 8, 12, 8),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(24, 0, 0, 0)
                };
                InlineTestTranscriptPanel.Children.Add(bubble);
                _lastInlineTestBubbleText = bubbleText;
            }

            InlineTestTranscriptScroll.UpdateLayout();
            InlineTestTranscriptScroll.ChangeView(null, InlineTestTranscriptScroll.ScrollableHeight, null);
        });
    }

    private void OnInlineTestAudioLevel(float level)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (_inlineTestPillBars == null) return;
            for (int bi = 0; bi < _inlineTestPillBars.Length; bi++)
            {
                var phase = (bi % 3 == 0) ? 0.7 : (bi % 3 == 1) ? 1.0 : 0.5;
                _inlineTestPillBars[bi].Height = 2.0 + Math.Min(level * phase, 1.0) * 8.0;
                _inlineTestPillBars[bi].Opacity = 0.5 + Math.Min(level, 1f) * 0.5;
            }
            if (_inlineTestPillDot != null)
                _inlineTestPillDot.Opacity = 0.5 + Math.Min(level, 1f) * 0.5;
        });
    }

    private void OnInlineTestSpeaking(bool isSpeaking)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            SetInlineTestStatus("");
            if (isSpeaking)
            {
                // New speech utterance â†’ new bubble
                _lastInlineTestBubbleText = null;
            }
        });
    }


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
        if (_suppressEvents || CurrentApp.Settings == null) return;
        if (PiperVoiceCombo.SelectedItem is ComboBoxItem item && item.Tag is string voiceId)
        {
            CurrentApp.Settings.TtsPiperVoiceId = voiceId;
            CurrentApp.Settings.Save();
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
        if (CurrentApp.Settings == null) return;
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
        if (CurrentApp.Settings == null) return;
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
            // streaming callback Ã— ~150 MB voices = ~1,800 reports. Coalesce
            // to â‰¥150 ms intervals so we don't choke the dispatcher.
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
        if (CurrentApp.Settings == null) return;
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
        if (CurrentApp.Settings == null) return;
        if (PiperVoiceCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string voiceId) return;

        PiperPreviewButton.IsEnabled = false;
        var oldContent = PiperPreviewButton.Content;
        PiperPreviewButton.Content = L("VoiceSettingsPage_PreviewButtonPlaying");

        try
        {
            using var tts = new TextToSpeechService(new AppLogger(), CurrentApp.Settings);
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
        if (_suppressEvents || CurrentApp.Settings == null) return;

        if (TtsProviderCombo.SelectedItem is ComboBoxItem item && item.Tag is string provider)
        {
            CurrentApp.Settings.TtsProvider = provider;
            CurrentApp.Settings.Save();
        }
        UpdateTtsProviderVisibility();
    }

    private void OnWindowsVoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || CurrentApp.Settings == null) return;

        if (WindowsVoiceCombo.SelectedItem is ComboBoxItem item && item.Tag is string voiceId)
        {
            CurrentApp.Settings.TtsWindowsVoiceId = voiceId;
            CurrentApp.Settings.Save();
        }
    }

    private async void OnPreviewVoiceClick(object sender, RoutedEventArgs e)
    {
        if (CurrentApp.Settings == null) return;

        PreviewVoiceButton.IsEnabled = false;
        PreviewVoiceButton.Content = L("VoiceSettingsPage_PreviewButtonPlaying");

        try
        {
            var tts = new TextToSpeechService(new AppLogger(), CurrentApp.Settings);
            try
            {
                await tts.SpeakAsync(new OpenClaw.Shared.Capabilities.TtsSpeakArgs
                {
                    Text = L("VoiceSettingsPage_CompanionPreviewText"),
                    Provider = CurrentApp.Settings.TtsProvider,
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
            // Show error inline (sanitized â€” full detail in the log).
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
        if (_suppressEvents || CurrentApp.Settings == null) return;
        CurrentApp.Settings.TtsElevenLabsApiKey = ElevenLabsApiKeyBox.Password;
        CurrentApp.Settings.Save();
    }

    private void OnElevenLabsVoiceIdChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents || CurrentApp.Settings == null) return;
        CurrentApp.Settings.TtsElevenLabsVoiceId = ElevenLabsVoiceIdBox.Text;
        CurrentApp.Settings.Save();
    }

    private void OnElevenLabsModelChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents || CurrentApp.Settings == null) return;
        CurrentApp.Settings.TtsElevenLabsModel = ElevenLabsModelBox.Text;
        CurrentApp.Settings.Save();
    }
}
