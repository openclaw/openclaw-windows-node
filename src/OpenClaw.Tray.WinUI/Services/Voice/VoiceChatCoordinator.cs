using OpenClaw.Shared;
using System;
using System.Threading.Tasks;

namespace OpenClawTray.Services.Voice;

public sealed class VoiceChatCoordinator : IDisposable
{
    private readonly IVoiceRuntime _voiceService;
    private readonly Func<VoiceChatWindowSubmitMode> _getSubmitMode;
    private readonly IUiDispatcher _dispatcher;
    private readonly object _gate = new();

    private IVoiceChatWindow? _webChatWindow;
    private string _voiceTranscriptDraftText = string.Empty;
    private bool _disposed;

    public event EventHandler<VoiceConversationTurnEventArgs>? ConversationTurnAvailable;

    public VoiceChatCoordinator(
        IVoiceRuntime voiceService,
        Func<VoiceChatWindowSubmitMode> getSubmitMode,
        IUiDispatcher dispatcher)
    {
        _voiceService = voiceService;
        _getSubmitMode = getSubmitMode;
        _dispatcher = dispatcher;

        _voiceService.ConversationTurnAvailable += OnVoiceConversationTurnAvailable;
        _voiceService.TranscriptDraftUpdated += OnVoiceTranscriptDraftUpdated;
        _voiceService.TranscriptSubmitter = SubmitVoiceTranscriptAsync;
    }

    public void AttachWindow(IVoiceChatWindow window)
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

    public void DetachWindow(IVoiceChatWindow? window)
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
        _dispatcher.TryEnqueue(() =>
        {
            ConversationTurnAvailable?.Invoke(this, args);
        });
    }

    private void OnVoiceTranscriptDraftUpdated(object? sender, VoiceTranscriptDraftEventArgs args)
    {
        _dispatcher.TryEnqueue(() =>
        {
            _voiceTranscriptDraftText = args.Clear ? string.Empty : (args.Text ?? string.Empty);

            IVoiceChatWindow? window;
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
        try
        {
            IVoiceChatWindow? window;
            lock (_gate)
            {
                window = _webChatWindow;
            }

            if (window == null || window.IsClosed)
            {
                return VoiceTranscriptSubmitOutcome.Unavailable;
            }

            if (_getSubmitMode() == VoiceChatWindowSubmitMode.WaitForUser)
            {
                return await window.PrepareVoiceTranscriptForManualSendAsync(text)
                    ? VoiceTranscriptSubmitOutcome.DeferredToUser
                    : VoiceTranscriptSubmitOutcome.Unavailable;
            }

            return await window.TrySubmitVoiceTranscriptAsync(text)
                ? VoiceTranscriptSubmitOutcome.Submitted
                : VoiceTranscriptSubmitOutcome.Unavailable;
        }
        catch
        {
            return VoiceTranscriptSubmitOutcome.Unavailable;
        }
    }
}
