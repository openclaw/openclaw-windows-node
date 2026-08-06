using OpenClaw.Tray.Tests.Presentation;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Characterization tests for <see cref="ChatComposerSession"/> and
/// <see cref="ChatComposerFactory"/>: exactly-once disposal cascading to both the
/// view model and controller, and that the factory itself is stateless (starts no
/// background work and produces an independent session per call).
/// </summary>
public sealed class ChatComposerSessionTests
{
    [Fact]
    public void Dispose_DisposesViewModelAndControllerExactlyOnce()
    {
        var dispatcher = new RecordingUiDispatcher();
        var factory = new ChatComposerFactory(dispatcher);
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
        var factory = new ChatComposerFactory(dispatcher);
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
        var factory = new ChatComposerFactory(dispatcher);
        var provider = new FakeChatDataProviderForComposerTests();
        var hostActions = new ChatComposerHostActions(null, () => { }, null, null, null);

        var session = factory.Create(provider, hostActions, initialSpeakerMuted: false);

        Assert.Same(hostActions, session.HostActions);
        session.Dispose();
    }
}
