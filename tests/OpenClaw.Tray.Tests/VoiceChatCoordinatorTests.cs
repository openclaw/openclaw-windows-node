using OpenClaw.Shared;
using OpenClawTray.Services.Voice;

namespace OpenClaw.Tray.Tests;

public class VoiceChatCoordinatorTests
{
    [Fact]
    public async Task AttachWindow_ReplaysBufferedDraft()
    {
        var runtime = new FakeVoiceRuntime();
        using var coordinator = new VoiceChatCoordinator(runtime, new ImmediateDispatcher());

        runtime.RaiseDraft("hello world", "main", clear: false);

        var window = new FakeVoiceChatWindow();
        coordinator.AttachWindow(window);
        await Task.Yield();

        Assert.Equal("hello world", window.LastDraftText);
        Assert.False(window.LastDraftClear);
    }

    [Fact]
    public async Task DraftClear_IsReplayedWhenWindowAttachesLater()
    {
        var runtime = new FakeVoiceRuntime();
        using var coordinator = new VoiceChatCoordinator(runtime, new ImmediateDispatcher());

        runtime.RaiseDraft("temporary draft", "main", clear: false);
        runtime.RaiseDraft(string.Empty, "main", clear: true);
        await Task.Yield();

        var window = new FakeVoiceChatWindow();
        coordinator.AttachWindow(window);
        await Task.Yield();

        Assert.Equal(string.Empty, window.LastDraftText);
        Assert.True(window.LastDraftClear);
    }

    [Fact]
    public async Task DraftUpdates_AreIgnoredForClosedWindow()
    {
        var runtime = new FakeVoiceRuntime();
        using var coordinator = new VoiceChatCoordinator(runtime, new ImmediateDispatcher());
        var window = new FakeVoiceChatWindow { IsClosed = true };
        coordinator.AttachWindow(window);
        var updateCountAfterAttach = window.UpdateCallCount;

        runtime.RaiseDraft("headless text", "main", clear: false);
        await Task.Yield();

        Assert.Equal(updateCountAfterAttach, window.UpdateCallCount);
    }

    [Fact]
    public async Task DetachWindow_StopsFurtherDraftMirroring()
    {
        var runtime = new FakeVoiceRuntime();
        using var coordinator = new VoiceChatCoordinator(runtime, new ImmediateDispatcher());
        var window = new FakeVoiceChatWindow();
        coordinator.AttachWindow(window);

        coordinator.DetachWindow(window);
        runtime.RaiseDraft("after detach", "main", clear: false);
        await Task.Yield();

        Assert.Equal(1, window.UpdateCallCount);
        Assert.Equal(string.Empty, window.LastDraftText);
        Assert.True(window.LastDraftClear);
    }

    [Fact]
    public void ConversationTurn_IsForwarded()
    {
        var runtime = new FakeVoiceRuntime();
        using var coordinator = new VoiceChatCoordinator(runtime, new ImmediateDispatcher());
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

    [Fact]
    public async Task ConversationTurn_IsMirroredToAttachedWindow()
    {
        var runtime = new FakeVoiceRuntime();
        using var coordinator = new VoiceChatCoordinator(runtime, new ImmediateDispatcher());
        var window = new FakeVoiceChatWindow();
        coordinator.AttachWindow(window);

        runtime.RaiseConversationTurn(new VoiceConversationTurnEventArgs
        {
            Direction = VoiceConversationDirection.Outgoing,
            Message = "hello from voice",
            SessionKey = "main"
        });
        await Task.Yield();

        Assert.Equal("hello from voice", window.LastTurnMessage);
        Assert.Equal(VoiceConversationDirection.Outgoing, window.LastTurnDirection);
        Assert.Equal(1, window.TurnCallCount);
    }

    [Fact]
    public async Task AttachWindow_ReplaysBufferedConversationTurns()
    {
        var runtime = new FakeVoiceRuntime();
        using var coordinator = new VoiceChatCoordinator(runtime, new ImmediateDispatcher());

        runtime.RaiseConversationTurn(new VoiceConversationTurnEventArgs
        {
            Direction = VoiceConversationDirection.Outgoing,
            Message = "replay this",
            SessionKey = "main"
        });
        await Task.Yield();

        var window = new FakeVoiceChatWindow();
        coordinator.AttachWindow(window);
        await Task.Yield();

        Assert.Equal("replay this", window.LastTurnMessage);
        Assert.Equal(VoiceConversationDirection.Outgoing, window.LastTurnDirection);
        Assert.Equal(1, window.TurnCallCount);
    }

    [Fact]
    public async Task DraftAndTurns_AreBroadcastToAllAttachedWindows()
    {
        var runtime = new FakeVoiceRuntime();
        using var coordinator = new VoiceChatCoordinator(runtime, new ImmediateDispatcher());
        var firstWindow = new FakeVoiceChatWindow();
        var secondWindow = new FakeVoiceChatWindow();

        coordinator.AttachWindow(firstWindow);
        coordinator.AttachWindow(secondWindow);

        runtime.RaiseDraft("shared draft", "main", clear: false);
        runtime.RaiseConversationTurn(new VoiceConversationTurnEventArgs
        {
            Direction = VoiceConversationDirection.Incoming,
            Message = "shared reply",
            SessionKey = "main"
        });
        await Task.Yield();

        Assert.Equal("shared draft", firstWindow.LastDraftText);
        Assert.Equal("shared draft", secondWindow.LastDraftText);
        Assert.Equal("shared reply", firstWindow.LastTurnMessage);
        Assert.Equal("shared reply", secondWindow.LastTurnMessage);
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

        public string LastDraftText { get; private set; } = string.Empty;
        public bool LastDraftClear { get; private set; }
        public int UpdateCallCount { get; private set; }
        public string LastTurnMessage { get; private set; } = string.Empty;
        public VoiceConversationDirection? LastTurnDirection { get; private set; }
        public int TurnCallCount { get; private set; }

        public Task UpdateVoiceTranscriptDraftAsync(string text, bool clear)
        {
            UpdateCallCount++;
            LastDraftText = text;
            LastDraftClear = clear;
            return Task.CompletedTask;
        }

        public Task AppendVoiceConversationTurnAsync(VoiceConversationTurnEventArgs args)
        {
            TurnCallCount++;
            LastTurnMessage = args.Message ?? string.Empty;
            LastTurnDirection = args.Direction;
            return Task.CompletedTask;
        }
    }
}
