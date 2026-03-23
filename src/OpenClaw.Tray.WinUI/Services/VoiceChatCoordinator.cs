using Microsoft.UI.Dispatching;
using OpenClaw.Shared;
using OpenClawTray.Windows;
using System;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

public sealed class VoiceChatCoordinator : IDisposable
{
    private readonly VoiceService _voiceService;
    private readonly SettingsManager _settings;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly object _gate = new();

    private WebChatWindow? _webChatWindow;
    private string _voiceTranscriptDraftText = string.Empty;
    private bool _disposed;

    public event EventHandler<VoiceConversationTurnEventArgs>? ConversationTurnAvailable;

    public VoiceChatCoordinator(
        VoiceService voiceService,
        SettingsManager settings,
        DispatcherQueue dispatcherQueue)
    {
        _voiceService = voiceService;
        _settings = settings;
        _dispatcherQueue = dispatcherQueue;

        _voiceService.ConversationTurnAvailable += OnVoiceConversationTurnAvailable;
        _voiceService.TranscriptDraftUpdated += OnVoiceTranscriptDraftUpdated;
        _voiceService.TranscriptSubmitter = SubmitVoiceTranscriptAsync;
    }

    public void AttachWindow(WebChatWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        lock (_gate)
        {
            if (ReferenceEquals(_webChatWindow, window))
            {
                return;
            }

            if (_webChatWindow != null)
            {
                _webChatWindow.VoiceTranscriptSubmitted -= OnWebChatVoiceTranscriptSubmitted;
            }

            _webChatWindow = window;
            _webChatWindow.VoiceTranscriptSubmitted += OnWebChatVoiceTranscriptSubmitted;
        }

        _ = window.UpdateVoiceTranscriptDraftAsync(
            _voiceTranscriptDraftText,
            clear: string.IsNullOrWhiteSpace(_voiceTranscriptDraftText));
    }

    public void DetachWindow(WebChatWindow? window)
    {
        lock (_gate)
        {
            if (_webChatWindow == null)
            {
                return;
            }

            if (window != null && !ReferenceEquals(_webChatWindow, window))
            {
                return;
            }

            _webChatWindow.VoiceTranscriptSubmitted -= OnWebChatVoiceTranscriptSubmitted;
            _webChatWindow = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DetachWindow(null);
        _voiceService.ConversationTurnAvailable -= OnVoiceConversationTurnAvailable;
        _voiceService.TranscriptDraftUpdated -= OnVoiceTranscriptDraftUpdated;
        _voiceService.TranscriptSubmitter = null;
    }

    private void OnVoiceConversationTurnAvailable(object? sender, VoiceConversationTurnEventArgs args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            ConversationTurnAvailable?.Invoke(this, args);
        });
    }

    private void OnVoiceTranscriptDraftUpdated(object? sender, VoiceTranscriptDraftEventArgs args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            _voiceTranscriptDraftText = args.Clear ? string.Empty : (args.Text ?? string.Empty);

            WebChatWindow? window;
            lock (_gate)
            {
                window = _webChatWindow;
            }

            if (window == null || window.IsClosed)
            {
                return;
            }

            _ = window.UpdateVoiceTranscriptDraftAsync(_voiceTranscriptDraftText, args.Clear);
        });
    }

    private void OnWebChatVoiceTranscriptSubmitted(object? sender, VoiceTranscriptSubmittedEventArgs args)
    {
        _voiceTranscriptDraftText = string.Empty;
        _voiceService.NotifyManualTranscriptSubmitted(args.Text, args.SessionKey);
    }

    private async Task<VoiceTranscriptSubmitOutcome> SubmitVoiceTranscriptAsync(string text, string? sessionKey)
    {
        WebChatWindow? window;
        lock (_gate)
        {
            window = _webChatWindow;
        }

        if (window == null || window.IsClosed)
        {
            return VoiceTranscriptSubmitOutcome.Unavailable;
        }

        if (_settings.Voice.AlwaysOn.ChatWindowSubmitMode == VoiceChatWindowSubmitMode.WaitForUser)
        {
            return await window.PrepareVoiceTranscriptForManualSendAsync(text)
                ? VoiceTranscriptSubmitOutcome.DeferredToUser
                : VoiceTranscriptSubmitOutcome.Unavailable;
        }

        return await window.TrySubmitVoiceTranscriptAsync(text)
            ? VoiceTranscriptSubmitOutcome.Submitted
            : VoiceTranscriptSubmitOutcome.Unavailable;
    }
}
