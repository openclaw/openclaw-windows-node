using OpenClaw.Shared;
using OpenClawTray.Services.Voice;

namespace OpenClaw.Tray.Tests;

public class VoiceChatCoordinatorTests
{
    [Fact]
    public async Task AttachWindow_ReplaysBufferedDraft()
    {
        var runtime = new FakeVoiceRuntime();
        using var coordinator = new VoiceChatCoordinator(runtime, () => VoiceChatWindowSubmitMode.AutoSend, new ImmediateDispatcher());

        runtime.RaiseDraft("hello world", "main", clear: false);

        var window = new FakeVoiceChatWindow();
        coordinator.AttachWindow(window);
        await Task.Yield();

        Assert.Equal("hello world", window.LastDraftText);
        Assert.False(window.LastDraftClear);
    }

    [Fact]
    public async Task Submitter_AutoSend_UsesChatWindowSubmit()
    {
        var runtime = new FakeVoiceRuntime();
        using var coordinator = new VoiceChatCoordinator(runtime, () => VoiceChatWindowSubmitMode.AutoSend, new ImmediateDispatcher());
        var window = new FakeVoiceChatWindow { SubmitResult = true };
        coordinator.AttachWindow(window);

        var result = await runtime.TranscriptSubmitter!("send this", "main");

        Assert.Equal(VoiceTranscriptSubmitOutcome.Submitted, result);
        Assert.Equal(1, window.TrySubmitCallCount);
        Assert.Equal(0, window.PrepareCallCount);
        Assert.Equal("send this", window.LastSubmittedText);
    }

    [Fact]
    public async Task Submitter_WaitForUser_PreparesDraftInsteadOfSending()
    {
        var runtime = new FakeVoiceRuntime();
        using var coordinator = new VoiceChatCoordinator(runtime, () => VoiceChatWindowSubmitMode.WaitForUser, new ImmediateDispatcher());
        var window = new FakeVoiceChatWindow { PrepareResult = true };
        coordinator.AttachWindow(window);

        var result = await runtime.TranscriptSubmitter!("draft only", "main");

        Assert.Equal(VoiceTranscriptSubmitOutcome.DeferredToUser, result);
        Assert.Equal(0, window.TrySubmitCallCount);
        Assert.Equal(1, window.PrepareCallCount);
        Assert.Equal("draft only", window.LastPreparedText);
    }

    [Fact]
    public async Task Submitter_WithoutWindow_FallsBackAsUnavailable()
    {
        var runtime = new FakeVoiceRuntime();
        using var coordinator = new VoiceChatCoordinator(runtime, () => VoiceChatWindowSubmitMode.AutoSend, new ImmediateDispatcher());

        var result = await runtime.TranscriptSubmitter!("headless", "main");

        Assert.Equal(VoiceTranscriptSubmitOutcome.Unavailable, result);
    }

    [Fact]
    public async Task ManualSubmit_NotifiesRuntime_AndClearsBufferedDraft()
    {
        var runtime = new FakeVoiceRuntime();
        using var coordinator = new VoiceChatCoordinator(runtime, () => VoiceChatWindowSubmitMode.WaitForUser, new ImmediateDispatcher());
        var firstWindow = new FakeVoiceChatWindow();
        coordinator.AttachWindow(firstWindow);

        runtime.RaiseDraft("working draft", "main", clear: false);
        await Task.Yield();

        firstWindow.RaiseSubmitted("final text", "main");

        Assert.Equal("final text", runtime.LastManualSubmitText);
        Assert.Equal("main", runtime.LastManualSubmitSessionKey);

        var secondWindow = new FakeVoiceChatWindow();
        coordinator.AttachWindow(secondWindow);
        await Task.Yield();

        Assert.Equal(string.Empty, secondWindow.LastDraftText);
        Assert.True(secondWindow.LastDraftClear);
    }

    [Fact]
    public void ManualSubmit_AllowsRuntimeToUseCurrentSession_WhenWindowDoesNotSpecifyOne()
    {
        var runtime = new FakeVoiceRuntime();
        using var coordinator = new VoiceChatCoordinator(runtime, () => VoiceChatWindowSubmitMode.WaitForUser, new ImmediateDispatcher());
        var window = new FakeVoiceChatWindow();
        coordinator.AttachWindow(window);

        window.RaiseSubmitted("follow up", null);

        Assert.Equal("follow up", runtime.LastManualSubmitText);
        Assert.Null(runtime.LastManualSubmitSessionKey);
    }

    [Fact]
    public void ConversationTurn_IsForwarded()
    {
        var runtime = new FakeVoiceRuntime();
        using var coordinator = new VoiceChatCoordinator(runtime, () => VoiceChatWindowSubmitMode.AutoSend, new ImmediateDispatcher());
        VoiceConversationTurnEventArgs? received = null;
        coordinator.ConversationTurnAvailable += (_, args) => received = args;

        runtime.RaiseConversationTurn(new VoiceConversationTurnEventArgs
        {
            Direction = VoiceConversationDirection.Incoming,
            Message = "reply",
            SessionKey = "main"
        });

        Assert.NotNull(received);
        Assert.Equal("reply", received!.Message);
        Assert.Equal(VoiceConversationDirection.Incoming, received.Direction);
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool TryEnqueue(Action callback)
        {
            callback();
            return true;
        }
    }

    private sealed class FakeVoiceRuntime : IVoiceRuntime
    {
        public event EventHandler<VoiceConversationTurnEventArgs>? ConversationTurnAvailable;
        public event EventHandler<VoiceTranscriptDraftEventArgs>? TranscriptDraftUpdated;
        public Func<string, string?, Task<VoiceTranscriptSubmitOutcome>>? TranscriptSubmitter { get; set; }

        public string? LastManualSubmitText { get; private set; }
        public string? LastManualSubmitSessionKey { get; private set; }

        public void NotifyManualTranscriptSubmitted(string text, string? sessionKey = null)
        {
            LastManualSubmitText = text;
            LastManualSubmitSessionKey = sessionKey;
        }

        public void RaiseDraft(string text, string? sessionKey, bool clear)
        {
            TranscriptDraftUpdated?.Invoke(this, new VoiceTranscriptDraftEventArgs
            {
                Text = text,
                SessionKey = sessionKey ?? "main",
                Clear = clear
            });
        }

        public void RaiseConversationTurn(VoiceConversationTurnEventArgs args)
        {
            ConversationTurnAvailable?.Invoke(this, args);
        }
    }

    private sealed class FakeVoiceChatWindow : IVoiceChatWindow
    {
        public bool IsClosed { get; set; }
        public event EventHandler<VoiceTranscriptSubmittedEventArgs>? VoiceTranscriptSubmitted;

        public string LastDraftText { get; private set; } = string.Empty;
        public bool LastDraftClear { get; private set; }
        public string? LastSubmittedText { get; private set; }
        public string? LastPreparedText { get; private set; }
        public int TrySubmitCallCount { get; private set; }
        public int PrepareCallCount { get; private set; }
        public bool SubmitResult { get; set; }
        public bool PrepareResult { get; set; }

        public Task UpdateVoiceTranscriptDraftAsync(string text, bool clear)
        {
            LastDraftText = text;
            LastDraftClear = clear;
            return Task.CompletedTask;
        }

        public Task<bool> TrySubmitVoiceTranscriptAsync(string text)
        {
            TrySubmitCallCount++;
            LastSubmittedText = text;
            return Task.FromResult(SubmitResult);
        }

        public Task<bool> PrepareVoiceTranscriptForManualSendAsync(string text)
        {
            PrepareCallCount++;
            LastPreparedText = text;
            return Task.FromResult(PrepareResult);
        }

        public void RaiseSubmitted(string text, string? sessionKey)
        {
            VoiceTranscriptSubmitted?.Invoke(this, new VoiceTranscriptSubmittedEventArgs
            {
                Text = text,
                SessionKey = sessionKey
            });
        }
    }
}
