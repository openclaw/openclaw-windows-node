using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClaw.Shared.Audio;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using WinUIEx;

namespace OpenClawTray.Windows;

/// <summary>
/// Floating voice overlay window for voice chat sessions.
/// Shows conversation transcript, audio levels, and controls.
/// </summary>
public sealed partial class VoiceOverlayWindow : WindowEx
{
    private readonly VoiceService _voiceService;
    private readonly IOpenClawLogger _logger;
    private readonly DispatcherQueue _dispatcherQueue;
    private bool _isMuted;

    /// <summary>Fired when the user submits transcribed text to the agent.</summary>
    public event Action<string>? TextSubmitted;

    /// <summary>Fired when the user clicks the Settings button. Hosts should
    /// navigate to the Voice & Audio page (e.g. via <c>ShowHub("voice")</c>).</summary>
    public event Action? SettingsRequested;

    public VoiceOverlayWindow(VoiceService voiceService, IOpenClawLogger logger)
    {
        InitializeComponent();
        _voiceService = voiceService;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        // Modern custom title bar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _voiceService.TranscriptionReceived += OnTranscriptionReceived;
        _voiceService.UtteranceCompleted += OnUtteranceCompleted;
        _voiceService.SpeakingChanged += OnSpeakingChanged;
        _voiceService.AudioLevelChanged += OnAudioLevelChanged;
        _voiceService.ModeChanged += OnModeChanged;
        _voiceService.PipelineStateChanged += OnPipelineStateChanged;
        _voiceService.DiagnosticMessage += OnDiagnosticMessage;

        Closed += WindowClosed;
        UpdateUI();
    }

    private DateTime _lastUserBubbleTime = DateTime.MinValue;
    private TextBlock? _lastUserTextBlock;

    private void OnTranscriptionReceived(string text)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            // Per-segment bubble update (visual streaming). Consolidate into
            // the last user bubble when fragments arrive within 5 seconds so
            // a multi-segment utterance reads as one bubble in the transcript.
            var elapsed = DateTime.UtcNow - _lastUserBubbleTime;
            if (_lastUserTextBlock != null && elapsed.TotalSeconds < 5)
            {
                _lastUserTextBlock.Text += " " + text;
                _lastUserBubbleTime = DateTime.UtcNow;
                try
                {
                    TranscriptScroller.UpdateLayout();
                    TranscriptScroller.ChangeView(null, TranscriptScroller.ScrollableHeight, null);
                }
                catch { }
            }
            else
            {
                AddTranscriptBubble(text, isUser: true);
            }
            // NOTE: chat submission moved to OnUtteranceCompleted so the
            // gateway receives one message per spoken utterance, not one per
            // Whisper segment.
        });
    }

    private void OnUtteranceCompleted(OpenClaw.Shared.Audio.UtteranceResult utterance)
    {
        // Fire once per silence-bounded utterance. The visual bubble already
        // shows the streamed text; here we just hand the complete sentence
        // to the gateway exactly once.
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (!string.IsNullOrWhiteSpace(utterance.Text))
                TextSubmitted?.Invoke(utterance.Text);
        });
    }

    /// <summary>Add an agent response to the transcript.</summary>
    public void AddAgentResponse(string text)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            AddTranscriptBubble(text, isUser: false);
        });
    }

    private void AddTranscriptBubble(string text, bool isUser)
    {
        try
        {
            // Hide empty state on first message
            if (EmptyState.Visibility == Visibility.Visible)
                EmptyState.Visibility = Visibility.Collapsed;

            var bubble = new Border
            {
                Background = isUser
                    ? new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue)
                    : (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                CornerRadius = isUser
                    ? new CornerRadius(12, 12, 4, 12)
                    : new CornerRadius(12, 12, 12, 4),
                Padding = new Thickness(12, 10, 12, 10),
                HorizontalAlignment = isUser
                    ? HorizontalAlignment.Right
                    : HorizontalAlignment.Left,
                Margin = new Thickness(isUser ? 24 : 0, 4, isUser ? 0 : 24, 4)
            };

            var icon = isUser ? "\uE77B" : "\uE799"; // Person / Robot
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var fontIcon = new FontIcon { Glyph = icon, FontSize = 12, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) };
            Grid.SetColumn(fontIcon, 0);
            grid.Children.Add(fontIcon);

            var textBlock = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                IsTextSelectionEnabled = true
            };
            if (isUser)
            {
                textBlock.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
                _lastUserTextBlock = textBlock;
                _lastUserBubbleTime = DateTime.UtcNow;
            }
            else
            {
                // Agent response breaks the consolidation window
                _lastUserTextBlock = null;
            }
            Grid.SetColumn(textBlock, 1);
            grid.Children.Add(textBlock);

            bubble.Child = grid;
            TranscriptPanel.Children.Add(bubble);

            // Auto-scroll to bottom
            TranscriptScroller.UpdateLayout();
            TranscriptScroller.ChangeView(null, TranscriptScroller.ScrollableHeight, null);
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to add transcript bubble", ex);
        }
    }

    private static string L(string key) => LocalizationHelper.GetString(key);
    private static string Lf(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, LocalizationHelper.GetString(key), args);

    private void OnSpeakingChanged(bool isSpeaking)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            StatusText.Text = isSpeaking
                ? L("VoiceOverlayWindow_StatusListening")
                : L("VoiceOverlayWindow_StatusSpeakNow");
        });
    }

    private void OnAudioLevelChanged(float level)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            // Scale the level bar width (max width = parent width)
            var maxWidth = AudioLevelBar.Parent is FrameworkElement parent ? parent.ActualWidth : 300;
            AudioLevelBar.Width = Math.Max(0, level * maxWidth);
        });
    }

    private void OnModeChanged(VoiceMode mode)
    {
        _dispatcherQueue.TryEnqueue(UpdateUI);
    }

    private void OnDiagnosticMessage(string message)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            StatusText.Text = message;
        });
    }

    private void OnPipelineStateChanged(AudioPipelineState state)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            StatusBadge.Text = state switch
            {
                AudioPipelineState.Stopped    => L("VoiceOverlayWindow_BadgeStopped"),
                AudioPipelineState.Starting   => L("VoiceOverlayWindow_BadgeStartingDots"),
                AudioPipelineState.Listening  => L("VoiceOverlayWindow_BadgeListening"),
                AudioPipelineState.Processing => L("VoiceOverlayWindow_BadgeProcessing"),
                AudioPipelineState.Error      => L("VoiceOverlayWindow_StateError"),
                _                             => L("VoiceOverlayWindow_BadgeUnknown")
            };

            StatusText.Text = state switch
            {
                AudioPipelineState.Stopped    => L("VoiceOverlayWindow_StatusReadyMessage"),
                AudioPipelineState.Starting   => L("VoiceOverlayWindow_StatusInitMic"),
                AudioPipelineState.Listening  => L("VoiceOverlayWindow_StatusSpeakNow"),
                AudioPipelineState.Processing => L("VoiceOverlayWindow_StatusTranscribing"),
                AudioPipelineState.Error      => L("VoiceOverlayWindow_StatusErrorOccurred"),
                _                             => ""
            };
        });
    }

    private void UpdateUI()
    {
        var isActive = _voiceService.CurrentMode != VoiceMode.Inactive;

        StartStopIcon.Glyph = isActive ? "\uE71A" : "\uE768"; // Stop / Play
        StartStopText.Text = isActive
            ? L("VoiceOverlayWindow_StopText")
            : L("VoiceOverlayWindow_ButtonStartListening");
        MuteButton.IsEnabled = isActive;

        if (!isActive)
        {
            StatusBadge.Text = L("VoiceOverlayWindow_BadgeReady");
            StatusText.Text = L("VoiceOverlayWindow_StatusReadyMessage");
            AudioLevelBar.Width = 0;
        }
    }

    private async void OnStartStopClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_voiceService.CurrentMode == VoiceMode.Inactive)
            {
                StatusText.Text = L("VoiceOverlayWindow_StateInitializing");
                StatusBadge.Text = L("VoiceOverlayWindow_StateStarting");
                StartStopButton.IsEnabled = false;

                // Initialize models if needed (may trigger downloads)
                if (!_voiceService.IsModelLoaded)
                {
                    if (!_voiceService.IsModelDownloaded)
                    {
                        StatusText.Text = L("VoiceOverlayWindow_StateDownloadingModel");
                        var progress = new Progress<(long downloaded, long total)>(p =>
                        {
                            _dispatcherQueue.TryEnqueue(() =>
                            {
                                var pct = p.total > 0 ? (int)(p.downloaded * 100 / p.total) : 0;
                                StatusText.Text = Lf("VoiceOverlayWindow_StateDownloadingPct", pct);
                            });
                        });
                        await _voiceService.DownloadModelAsync(progress: progress);
                    }

                    StatusText.Text = L("VoiceOverlayWindow_StateLoadingModel");
                    await _voiceService.InitializeAsync();
                }

                StatusText.Text = L("VoiceOverlayWindow_StateStartingMic");
                await _voiceService.StartVoiceChatAsync();
            }
            else
            {
                StatusText.Text = L("VoiceOverlayWindow_StateStopping");
                await _voiceService.StopAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Voice overlay start/stop failed", ex);
            // Sanitized — full ex.Message is in the log.
            StatusText.Text = L("VoiceOverlayWindow_StatusError");
            StatusBadge.Text = L("VoiceOverlayWindow_StateError");
        }
        finally
        {
            StartStopButton.IsEnabled = true;
            UpdateUI();
        }
    }

    private async void OnMuteClick(object sender, RoutedEventArgs e)
    {
        _isMuted = !_isMuted;
        MuteIcon.Glyph = _isMuted ? "\uE74F" : "\uE767"; // Muted / Volume

        if (_isMuted)
        {
            await _voiceService.StopAsync();
            StatusText.Text = L("VoiceOverlayWindow_StatusMuted");
        }
        else
        {
            await _voiceService.StartVoiceChatAsync();
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke();
    }

    private void WindowClosed(object sender, WindowEventArgs args)
    {
        _voiceService.TranscriptionReceived -= OnTranscriptionReceived;
        _voiceService.UtteranceCompleted -= OnUtteranceCompleted;
        _voiceService.SpeakingChanged -= OnSpeakingChanged;
        _voiceService.AudioLevelChanged -= OnAudioLevelChanged;
        _voiceService.ModeChanged -= OnModeChanged;
        _voiceService.PipelineStateChanged -= OnPipelineStateChanged;
        _voiceService.DiagnosticMessage -= OnDiagnosticMessage;

        // Stop voice session when window closes
        _ = _voiceService.StopAsync();
    }
}
