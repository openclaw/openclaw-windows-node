using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClaw.Tray.Tests.Presentation;
using OpenClawTray.Chat;
using OpenClaw.TestSupport;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Characterization tests for <see cref="ChatComposerSession"/> and
/// <see cref="ChatComposerFactory"/>: exactly-once disposal cascading to both the
/// view model and controller, and that the factory itself is stateless (starts no
/// background work and produces an independent session per call).
/// </summary>
public sealed class ChatComposerSessionTests
{
    private static ChatComposerInputs MakeInputs(string threadId) =>
        new(
            "connected",
            false,
            new ChatThread
            {
                Id = threadId,
                Title = threadId,
                Status = ChatThreadStatus.Running,
                Activity = ChatActivity.Idle,
            },
            System.Array.Empty<string>(),
            null,
            false,
            System.Array.Empty<ChatQueuedMessage>(),
            null,
            false);

    [Fact]
    public void Dispose_DisposesViewModelAndControllerExactlyOnce()
    {
        var dispatcher = new RecordingUiDispatcher();
        using var temp = new TempDirectory();
        using var store = new SettingsStore(new SettingsManager(temp.Path), dispatcher);
        var factory = new ChatComposerFactory(dispatcher, store, new SandboxStatusStore());
        var provider = new FakeChatDataProviderForComposerTests();
        var hostActions = new ChatComposerHostActions(null, null, null, null);
        var session = factory.Create(provider, hostActions, initialSpeakerMuted: false);

        session.Dispose();
        var exception = Record.Exception(session.Dispose);

        Assert.Null(exception);
    }

    [Fact]
    public void Create_ProducesAnIndependentSessionPerCall()
    {
        var dispatcher = new RecordingUiDispatcher();
        using var temp = new TempDirectory();
        using var store = new SettingsStore(new SettingsManager(temp.Path), dispatcher);
        var factory = new ChatComposerFactory(dispatcher, store, new SandboxStatusStore());
        var provider = new FakeChatDataProviderForComposerTests();
        var hostActions = new ChatComposerHostActions(null, null, null, null);

        var first = factory.Create(provider, hostActions, initialSpeakerMuted: false);
        var second = factory.Create(provider, hostActions, initialSpeakerMuted: false);

        Assert.NotSame(first, second);

        first.Dispose();
        second.Dispose();
    }

    [Fact]
    public void HostActions_AreExposedUnchangedFromCreation()
    {
        var dispatcher = new RecordingUiDispatcher();
        using var temp = new TempDirectory();
        using var store = new SettingsStore(new SettingsManager(temp.Path), dispatcher);
        var factory = new ChatComposerFactory(dispatcher, store, new SandboxStatusStore());
        var provider = new FakeChatDataProviderForComposerTests();
        var hostActions = new ChatComposerHostActions(null, () => { }, null, null);

        var session = factory.Create(provider, hostActions, initialSpeakerMuted: false);

        Assert.Same(hostActions, session.HostActions);
        session.Dispose();
    }

    [Fact]
    public async Task QuestionHidesComposer_CancelsCaptureAndBlocksHiddenStarts_WithoutLosingDraft()
    {
        var capture = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanedUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken captureToken = default;
        var calls = 0;
        var dispatcher = new RecordingUiDispatcher();
        using var temp = new TempDirectory();
        using var store = new SettingsStore(new SettingsManager(temp.Path), dispatcher);
        var factory = new ChatComposerFactory(dispatcher, store, new SandboxStatusStore());
        using var session = factory.Create(new FakeChatDataProviderForComposerTests(),
            new ChatComposerHostActions(null, null, (token, _) =>
            {
                calls++;
                captureToken = token;
                return calls == 1 ? capture.Task : Task.FromResult<string?>("New capture");
            }, null, null), initialSpeakerMuted: false);
        session.ApplyInputs(MakeInputs("first"));
        session.ViewModel.SetDraft("Keep my draft");
        var attachment = new ChatAttachment { FileName = "keep.png" };
        session.ViewModel.AddAttachments([attachment]);
        session.Controller.TestOnlyVoiceOperationCleanedUp = () => cleanedUp.TrySetResult();
        session.Controller.StartVoiceRecording();
        Assert.True(session.ViewModel.IsRecording);

        session.ApplyInputs(MakeInputs("first") with { IsVisible = false });

        Assert.True(captureToken.IsCancellationRequested);
        Assert.False(session.ViewModel.IsRecording);
        session.Controller.StartVoiceRecording();
        Assert.Equal(1, calls);
        capture.SetResult("Late hidden transcript");
        await cleanedUp.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Keep my draft", session.ViewModel.Draft);
        Assert.Same(attachment, Assert.Single(session.ViewModel.PendingAttachments));

        session.ApplyInputs(MakeInputs("first"));
        session.Controller.StartVoiceRecording();
        Assert.Equal(2, calls);
        Assert.Equal("Keep my draft New capture", session.ViewModel.Draft);
    }

    [Fact]
    public async Task RetainedHostSuspension_OverridesVisibleProjectionAndFencesCaptureAfterResume()
    {
        var capture = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanedUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken captureToken = default;
        Action? recordingStarted = null;
        var calls = 0;
        var dispatcher = new RecordingUiDispatcher();
        using var temp = new TempDirectory();
        using var store = new SettingsStore(new SettingsManager(temp.Path), dispatcher);
        using var session = new ChatComposerFactory(dispatcher, store, new SandboxStatusStore()).Create(
            new FakeChatDataProviderForComposerTests(),
            new ChatComposerHostActions(null, null, (token, started) =>
            {
                calls++;
                captureToken = token;
                recordingStarted = started;
                return capture.Task;
            }, null), initialSpeakerMuted: false);
        session.ApplyInputs(MakeInputs("first"));
        session.ViewModel.SetDraft("Retain this draft");
        var attachment = new ChatAttachment { FileName = "keep.png" };
        session.ViewModel.AddAttachments([attachment]);
        session.Controller.TestOnlyVoiceOperationCleanedUp = () => cleanedUp.TrySetResult();
        session.Controller.StartVoiceRecording();
        session.ViewModel.SetVoiceTranscript("Partial capture");
        session.ViewModel.SetVoiceAudioLevel(0.7f);

        session.SetHostVisible(false);
        session.ApplyInputs(MakeInputs("first"));
        session.Controller.StartVoiceRecording();
        Assert.True(captureToken.IsCancellationRequested);
        Assert.Equal(1, calls);
        Assert.False(session.ViewModel.IsRecording);
        Assert.Null(session.ViewModel.VoiceTranscript);
        Assert.Equal(0, session.ViewModel.VoiceAudioLevel);

        session.SetHostVisible(true);
        recordingStarted?.Invoke();
        capture.SetResult("Late transcript after returning");
        await cleanedUp.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(session.ViewModel.IsRecording);
        Assert.Equal(1, calls);
        Assert.Equal("Retain this draft", session.ViewModel.Draft);
        Assert.Same(attachment, Assert.Single(session.ViewModel.PendingAttachments));

        session.ApplyInputs(MakeInputs("first") with { IsVisible = false });
        session.SetHostVisible(false);
        session.SetHostVisible(true);
        session.Controller.StartVoiceRecording();
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ApplyInputs_AssignsSessionMonotonicRevisionsAcrossViewRemounts()
    {
        var dispatcher = new RecordingUiDispatcher();
        using var temp = new TempDirectory();
        using var store = new SettingsStore(new SettingsManager(temp.Path), dispatcher);
        var factory = new ChatComposerFactory(dispatcher, store, new SandboxStatusStore());
        var session = factory.Create(
            new FakeChatDataProviderForComposerTests(),
            new ChatComposerHostActions(null, null, null, null),
            initialSpeakerMuted: false);

        session.ApplyInputs(MakeInputs("first"));
        var firstRevision = session.ViewModel.Inputs!.Revision;

        // ReactorChatComposer may unmount and remount while this host-owned
        // session remains alive. The next effect must not restart at revision 1.
        session.ApplyInputs(MakeInputs("second"));

        Assert.Equal(1, firstRevision);
        Assert.Equal(2, session.ViewModel.Inputs!.Revision);
        Assert.Equal("second", session.ViewModel.Inputs.CurrentThread.Id);
        session.Dispose();
    }

    [Fact]
    public void SandboxSettings_RefreshBothSessionsAndStopAfterDisposal()
    {
        using var temp = new TempDirectory();
        var dispatcher = new RecordingUiDispatcher();
        var settings = new SettingsManager(temp.Path) { NodeSystemRunEnabled = true };
        using var store = new SettingsStore(settings, dispatcher);
        var status = new SandboxStatusStore();
        status.Publish(new(true, false, true, [], isolationTier: "base-container", supportsProtectedPathDenies: true));
        var factory = new ChatComposerFactory(dispatcher, store, status);
        var actions = new ChatComposerHostActions(null, null, null, null, null);
        using var first = factory.Create(new FakeChatDataProviderForComposerTests(), actions, false);
        using var second = factory.Create(new FakeChatDataProviderForComposerTests(), actions, false);
        Assert.Equal(SandboxStatus.Available, first.ViewModel.SandboxStatus);
        Assert.Equal(SandboxStatus.Available, second.ViewModel.SandboxStatus);
        first.ViewModel.SetDraft("Keep this draft");
        var attachment = new ChatAttachment { FileName = "keep.txt" };
        first.ViewModel.AddAttachments([attachment]);

        settings.NodeSystemRunEnabled = false;
        settings.Save();

        Assert.Equal(SandboxStatus.Disabled, first.ViewModel.SandboxStatus);
        Assert.Equal(SandboxStatus.Disabled, second.ViewModel.SandboxStatus);
        Assert.Equal("Keep this draft", first.ViewModel.Draft);
        Assert.Same(attachment, Assert.Single(first.ViewModel.PendingAttachments));

        first.Dispose();
        var disposedRevision = first.ViewModel.RenderRevision;
        settings.NodeSystemRunEnabled = true;
        settings.Save();
        status.Publish(new(false, false, true, [], isWindowsUnsupported: true));

        Assert.Equal(disposedRevision, first.ViewModel.RenderRevision);
        Assert.Equal(SandboxStatus.Disabled, first.ViewModel.SandboxStatus);
        Assert.Equal(SandboxStatus.Compatibility, second.ViewModel.SandboxStatus);
    }
}
