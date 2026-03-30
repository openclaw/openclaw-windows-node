using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Services.Voice;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Windows.Graphics;
using WinUIEx;

namespace OpenClawTray.Windows;

public sealed partial class VoiceRepeaterWindow : WindowEx, IVoiceChatWindow
{
    private const int MaxConversationItems = 24;
    private const int DefaultWidth = 360;
    private const int DefaultHeight = 170;
    private const int DefaultMargin = 12;
    private const double DefaultTextSize = 13;
    private const double DefaultCaptionSize = 10;

    private readonly SettingsManager _settings;
    private readonly IVoiceRuntimeControlApi _voiceRuntimeControlApi;
    private readonly ObservableCollection<ConversationItem> _conversationItems = [];
    private readonly DispatcherQueueTimer? _refreshTimer;
    private readonly DispatcherQueueTimer? _layoutSaveTimer;

    private bool _controlActionInFlight;
    private bool _suppressSettingsEvents;
    private bool _suppressPlacementSave = true;
    private bool _initialPlacementPending = true;
    private bool _placementDirty;
    private bool _autoScrollEnabled;
    private double _messageFontSize = DefaultTextSize;
    private double _captionFontSize = DefaultCaptionSize;

    public bool IsClosed { get; private set; }

    public event EventHandler? OpenVoiceStatusRequested;

    public VoiceRepeaterWindow(
        SettingsManager settings,
        IVoiceRuntimeControlApi voiceRuntimeControlApi)
    {
        _settings = settings;
        _voiceRuntimeControlApi = voiceRuntimeControlApi;
        _autoScrollEnabled = _settings.VoiceRepeaterWindow.AutoScroll;

        InitializeComponent();

        Title = "Voice Mode";
        ApplyStoredWindowPlacement();
        this.SetIcon(IconHelper.GetStatusIconPath(ConnectionStatus.Connected));

        ConversationItemsControl.ItemsSource = _conversationItems;

        Closed += OnWindowClosed;
        Activated += OnWindowActivated;

        var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        if (dispatcherQueue != null)
        {
            _refreshTimer = dispatcherQueue.CreateTimer();
            _refreshTimer.Interval = TimeSpan.FromMilliseconds(400);
            _refreshTimer.Tick += (_, _) => RefreshStatus();
            _refreshTimer.Start();

            _layoutSaveTimer = dispatcherQueue.CreateTimer();
            _layoutSaveTimer.Interval = TimeSpan.FromMilliseconds(600);
            _layoutSaveTimer.IsRepeating = false;
            _layoutSaveTimer.Tick += (_, _) =>
            {
                _layoutSaveTimer.Stop();
                SaveWindowPlacement();
            };
        }

        if (AppWindow is not null)
        {
            AppWindow.Changed += OnAppWindowChanged;
        }

        ApplyViewSettings();
        RefreshStatus();
        UpdateConversationPlaceholder();
    }

    public void RefreshStatus()
    {
        var status = _voiceRuntimeControlApi.CurrentStatus;
        ApplyStatus(status);
    }

    public Task UpdateVoiceTranscriptDraftAsync(string text, bool clear)
    {
        var draftText = clear ? string.Empty : (text ?? string.Empty);
        DraftTextBlock.Text = draftText;
        DraftPanel.Visibility = string.IsNullOrWhiteSpace(draftText)
            ? Visibility.Collapsed
            : Visibility.Visible;

        UpdateConversationPlaceholder();
        ScrollConversationToEnd();
        return Task.CompletedTask;
    }

    public Task AppendVoiceConversationTurnAsync(VoiceConversationTurnEventArgs args)
    {
        if (args == null || string.IsNullOrWhiteSpace(args.Message))
        {
            return Task.CompletedTask;
        }

        var item = new ConversationItem(
            args.Direction == VoiceConversationDirection.Outgoing ? "You" : "Assistant",
            DateTime.Now.ToString("HH:mm:ss"),
            args.Message,
            _messageFontSize,
            _captionFontSize);

        _conversationItems.Add(item);
        while (_conversationItems.Count > MaxConversationItems)
        {
            _conversationItems.RemoveAt(0);
        }

        UpdateConversationPlaceholder();
        ScrollConversationToEnd();
        return Task.CompletedTask;
    }

    private async void OnPauseResumeClick(object sender, RoutedEventArgs e)
    {
        if (_controlActionInFlight)
        {
            return;
        }

        _controlActionInFlight = true;
        ApplyStatus(_voiceRuntimeControlApi.CurrentStatus);

        try
        {
            var status = _voiceRuntimeControlApi.CurrentStatus;
            if (status.State == VoiceRuntimeState.Paused)
            {
                await _voiceRuntimeControlApi.ResumeAsync(new VoiceResumeArgs { Reason = "Voice repeater resume button" });
            }
            else
            {
                await _voiceRuntimeControlApi.PauseAsync(new VoicePauseArgs { Reason = "Voice repeater pause button" });
            }
        }
        finally
        {
            _controlActionInFlight = false;
            RefreshStatus();
        }
    }

    private async void OnSkipReplyClick(object sender, RoutedEventArgs e)
    {
        if (_controlActionInFlight || !_voiceRuntimeControlApi.CurrentStatus.CanSkipReply)
        {
            return;
        }

        _controlActionInFlight = true;
        ApplyStatus(_voiceRuntimeControlApi.CurrentStatus);

        try
        {
            await _voiceRuntimeControlApi.SkipCurrentReplyAsync(new VoiceSkipArgs
            {
                Reason = "Voice repeater skip button"
            });
        }
        finally
        {
            _controlActionInFlight = false;
            RefreshStatus();
        }
    }

    private void OnAutoScrollChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents)
        {
            return;
        }

        _autoScrollEnabled = AutoScrollCheckBox.IsChecked == true;
        _settings.VoiceRepeaterWindow.AutoScroll = _autoScrollEnabled;
        _settings.Save(logSuccess: false);

        if (_autoScrollEnabled)
        {
            ScrollConversationToEnd();
        }
    }

    private void OnTextSizeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingsEvents || TextSizeComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        if (!double.TryParse(item.Tag?.ToString(), out var size))
        {
            return;
        }

        _settings.VoiceRepeaterWindow.TextSize = size;
        ApplyViewSettings();
        _settings.Save(logSuccess: false);
    }

    private void OnFloatingEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents)
        {
            return;
        }

        var enabled = FloatingEnabledCheckBox.IsChecked == true;
        _settings.VoiceRepeaterWindow.FloatingEnabled = enabled;
        IsAlwaysOnTop = enabled;
        _settings.Save(logSuccess: false);
    }

    private void OnOpenVoiceStatusClick(object sender, RoutedEventArgs e)
    {
        OpenVoiceStatusRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnWindowClosed(object sender, WindowEventArgs e)
    {
        if (_refreshTimer != null)
        {
            _refreshTimer.Stop();
        }

        if (_layoutSaveTimer != null)
        {
            _layoutSaveTimer.Stop();
        }

        if (AppWindow is not null)
        {
            AppWindow.Changed -= OnAppWindowChanged;
        }

        Activated -= OnWindowActivated;
        FlushWindowPlacement();
        IsClosed = true;
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!_initialPlacementPending)
        {
            return;
        }

        _initialPlacementPending = false;
        ApplyStoredWindowPlacement();

        var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _ = dispatcherQueue?.TryEnqueue(() => _suppressPlacementSave = false);
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_suppressPlacementSave)
        {
            return;
        }

        if (args.DidPositionChange || args.DidSizeChange)
        {
            _placementDirty = true;
            _layoutSaveTimer?.Stop();
            _layoutSaveTimer?.Start();
        }
    }

    private void ApplyStatus(VoiceStatusInfo status)
    {
        Title = $"Voice Mode ({GetWindowStateLabel(status)})";
        DraftCaptionTextBlock.Text = status.State == VoiceRuntimeState.RecordingUtterance
            ? "You (speaking)"
            : "You (draft)";

        if (string.IsNullOrWhiteSpace(status.LastError))
        {
            TroubleshootingTextBlock.Visibility = Visibility.Collapsed;
            TroubleshootingTextBlock.Text = string.Empty;
        }
        else
        {
            TroubleshootingTextBlock.Visibility = Visibility.Visible;
            TroubleshootingTextBlock.Text = status.LastError;
        }

        var paused = status.State == VoiceRuntimeState.Paused;
        PauseResumeButton.IsEnabled = !_controlActionInFlight && status.Mode != VoiceActivationMode.Off;
        PauseResumeIcon.Symbol = paused ? Symbol.Play : Symbol.Pause;
        ToolTipService.SetToolTip(
            PauseResumeButton,
            paused ? "Resume voice mode" : "Pause voice mode");

        SkipReplyButton.IsEnabled = !_controlActionInFlight && status.CanSkipReply;
    }

    private void ApplyStoredWindowPlacement()
    {
        if (AppWindow is null)
        {
            return;
        }

        var prefs = _settings.VoiceRepeaterWindow;
        var width = prefs.HasSavedPlacement
            ? prefs.Width.GetValueOrDefault(DefaultWidth)
            : DefaultWidth;
        var height = prefs.HasSavedPlacement
            ? prefs.Height.GetValueOrDefault(DefaultHeight)
            : DefaultHeight;
        var clampedWidth = Math.Max(width, 320);
        var clampedHeight = Math.Max(height, 150);

        IsAlwaysOnTop = prefs.FloatingEnabled;

        var targetRect = prefs.HasSavedPlacement && prefs.X.HasValue && prefs.Y.HasValue
            ? new RectInt32(prefs.X.Value, prefs.Y.Value, clampedWidth, clampedHeight)
            : GetDefaultAnchorRect(clampedWidth, clampedHeight);

        if (!IsPlacementVisible(targetRect))
        {
            targetRect = GetDefaultAnchorRect(clampedWidth, clampedHeight);
        }

        try
        {
            AppWindow.MoveAndResize(targetRect);
        }
        catch
        {
            this.SetWindowSize(targetRect.Width, targetRect.Height);
            AppWindow.Move(new PointInt32(targetRect.X, targetRect.Y));
        }
    }

    private void ApplyViewSettings()
    {
        _suppressSettingsEvents = true;
        try
        {
            _autoScrollEnabled = _settings.VoiceRepeaterWindow.AutoScroll;
            _messageFontSize = Math.Clamp(
                _settings.VoiceRepeaterWindow.TextSize > 0 ? _settings.VoiceRepeaterWindow.TextSize : DefaultTextSize,
                11,
                15);
            _captionFontSize = Math.Max(9, _messageFontSize - 3);

            DraftTextBlock.FontSize = _messageFontSize;
            DraftCaptionTextBlock.FontSize = _captionFontSize;
            TroubleshootingTextBlock.FontSize = _captionFontSize;

            foreach (var item in _conversationItems)
            {
                item.MessageFontSize = _messageFontSize;
                item.CaptionFontSize = _captionFontSize;
            }

            AutoScrollCheckBox.IsChecked = _autoScrollEnabled;
            FloatingEnabledCheckBox.IsChecked = _settings.VoiceRepeaterWindow.FloatingEnabled;
            SelectTextSizeItem(_messageFontSize);
        }
        finally
        {
            _suppressSettingsEvents = false;
        }
    }

    private void SaveWindowPlacement()
    {
        if (IsClosed || AppWindow is null || _suppressPlacementSave)
        {
            return;
        }

        var size = AppWindow.Size;
        var position = AppWindow.Position;
        _settings.VoiceRepeaterWindow.Width = size.Width;
        _settings.VoiceRepeaterWindow.Height = size.Height;
        _settings.VoiceRepeaterWindow.X = position.X;
        _settings.VoiceRepeaterWindow.Y = position.Y;
        _settings.VoiceRepeaterWindow.HasSavedPlacement = true;
        _settings.Save(logSuccess: false);
        _placementDirty = false;
    }

    private void FlushWindowPlacement()
    {
        if (_placementDirty || !IsClosed)
        {
            SaveWindowPlacement();
        }
    }

    private RectInt32 GetDefaultAnchorRect(int width, int height)
    {
        var displayArea = DisplayArea.Primary;
        var x = displayArea.WorkArea.X + DefaultMargin;
        var y = displayArea.WorkArea.Y + Math.Max(DefaultMargin, displayArea.WorkArea.Height - height - DefaultMargin);
        return new RectInt32(x, y, width, height);
    }

    private static bool IsPlacementVisible(RectInt32 rect)
    {
        try
        {
            var displayArea = DisplayArea.GetFromRect(rect, DisplayAreaFallback.Nearest);
            var workArea = displayArea.WorkArea;
            return rect.Width > 0 &&
                   rect.Height > 0 &&
                   rect.X < workArea.X + workArea.Width &&
                   rect.X + rect.Width > workArea.X &&
                   rect.Y < workArea.Y + workArea.Height &&
                   rect.Y + rect.Height > workArea.Y;
        }
        catch
        {
            return false;
        }
    }

    private void SelectTextSizeItem(double size)
    {
        var sizeTag = ((int)Math.Round(size)).ToString();
        foreach (var entry in TextSizeComboBox.Items)
        {
            if (entry is ComboBoxItem item && string.Equals(item.Tag?.ToString(), sizeTag, StringComparison.Ordinal))
            {
                TextSizeComboBox.SelectedItem = item;
                return;
            }
        }

        TextSizeComboBox.SelectedIndex = 2;
    }

    private void UpdateConversationPlaceholder()
    {
        EmptyConversationTextBlock.Visibility = _conversationItems.Count == 0 && DraftPanel.Visibility != Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ScrollConversationToEnd()
    {
        if (!_autoScrollEnabled)
        {
            return;
        }

        var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _ = dispatcherQueue?.TryEnqueue(() =>
        {
            ConversationScrollViewer.UpdateLayout();
            ConversationScrollViewer.ChangeView(null, ConversationScrollViewer.ScrollableHeight, null, true);
            _ = dispatcherQueue.TryEnqueue(() =>
                ConversationScrollViewer.ChangeView(null, ConversationScrollViewer.ScrollableHeight, null, true));
        });
    }

    private static string GetWindowStateLabel(VoiceStatusInfo status)
    {
        return status.State switch
        {
            VoiceRuntimeState.ListeningForVoiceWake => "listening",
            VoiceRuntimeState.ListeningContinuously => "listening",
            VoiceRuntimeState.RecordingUtterance => "hearing you",
            VoiceRuntimeState.AwaitingResponse => "waiting",
            VoiceRuntimeState.PlayingResponse => "speaking",
            VoiceRuntimeState.Paused => "paused",
            VoiceRuntimeState.Arming => "starting",
            VoiceRuntimeState.Error => "error",
            _ when status.Mode == VoiceActivationMode.Off => "off",
            _ => "idle"
        };
    }

    private sealed class ConversationItem : INotifyPropertyChanged
    {
        private double _messageFontSize;
        private double _captionFontSize;

        public ConversationItem(
            string speaker,
            string timestamp,
            string message,
            double messageFontSize,
            double captionFontSize)
        {
            Speaker = speaker;
            Timestamp = timestamp;
            Message = message;
            _messageFontSize = messageFontSize;
            _captionFontSize = captionFontSize;
        }

        public string Speaker { get; }
        public string Timestamp { get; }
        public string Message { get; }
        public string Caption => $"{Speaker} · {Timestamp}";

        public double MessageFontSize
        {
            get => _messageFontSize;
            set
            {
                if (Math.Abs(_messageFontSize - value) < 0.01)
                {
                    return;
                }

                _messageFontSize = value;
                OnPropertyChanged();
            }
        }

        public double CaptionFontSize
        {
            get => _captionFontSize;
            set
            {
                if (Math.Abs(_captionFontSize - value) < 0.01)
                {
                    return;
                }

                _captionFontSize = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
