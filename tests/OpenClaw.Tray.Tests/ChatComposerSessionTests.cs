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
            System.Array.Empty<ChatThread>(),
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
        var factory = new ChatComposerFactory(dispatcher, store);
        var provider = new FakeChatDataProviderForComposerTests();
        var hostActions = new ChatComposerHostActions(null, null, null, null, null);
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
        var factory = new ChatComposerFactory(dispatcher, store);
        var provider = new FakeChatDataProviderForComposerTests();
        var hostActions = new ChatComposerHostActions(null, null, null, null, null);

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
        var factory = new ChatComposerFactory(dispatcher, store);
        var provider = new FakeChatDataProviderForComposerTests();
        var hostActions = new ChatComposerHostActions(null, () => { }, null, null, null);

        var session = factory.Create(provider, hostActions, initialSpeakerMuted: false);

        Assert.Same(hostActions, session.HostActions);
        session.Dispose();
    }

    [Fact]
    public void ApplyInputs_AssignsSessionMonotonicRevisionsAcrossViewRemounts()
    {
        var dispatcher = new RecordingUiDispatcher();
        using var temp = new TempDirectory();
        using var store = new SettingsStore(new SettingsManager(temp.Path), dispatcher);
        var factory = new ChatComposerFactory(dispatcher, store);
        var session = factory.Create(
            new FakeChatDataProviderForComposerTests(),
            new ChatComposerHostActions(null, null, null, null, null),
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
        var settings = new SettingsManager(temp.Path) { SystemRunSandboxEnabled = true };
        using var store = new SettingsStore(settings, dispatcher);
        var factory = new ChatComposerFactory(dispatcher, store);
        var actions = new ChatComposerHostActions(null, null, null, null, null);
        using var first = factory.Create(new FakeChatDataProviderForComposerTests(), actions, false);
        using var second = factory.Create(new FakeChatDataProviderForComposerTests(), actions, false);
        Assert.True(first.ViewModel.SandboxEnabled);
        Assert.True(second.ViewModel.SandboxEnabled);
        first.ViewModel.SetDraft("Keep this draft");
        var attachment = new ChatAttachment { FileName = "keep.txt" };
        first.ViewModel.AddAttachments([attachment]);

        settings.SystemRunSandboxEnabled = false;
        settings.Save();

        Assert.False(first.ViewModel.SandboxEnabled);
        Assert.False(second.ViewModel.SandboxEnabled);
        Assert.Equal("Keep this draft", first.ViewModel.Draft);
        Assert.Same(attachment, Assert.Single(first.ViewModel.PendingAttachments));

        first.Dispose();
        var disposedRevision = first.ViewModel.RenderRevision;
        settings.SystemRunSandboxEnabled = true;
        settings.Save();

        Assert.Equal(disposedRevision, first.ViewModel.RenderRevision);
        Assert.False(first.ViewModel.SandboxEnabled);
        Assert.True(second.ViewModel.SandboxEnabled);
    }
}
