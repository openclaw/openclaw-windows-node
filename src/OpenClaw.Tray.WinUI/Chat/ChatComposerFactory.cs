using OpenClaw.Chat;
using OpenClawTray.Presentation;
using System;

namespace OpenClawTray.Chat;

/// <summary>
/// Production <see cref="IChatComposerFactory"/>. Stateless apart from the injected,
/// App-owned <see cref="IUiDispatcher"/> singleton; starts no background work.
/// </summary>
internal sealed class ChatComposerFactory(IUiDispatcher dispatcher) : IChatComposerFactory
{
    private readonly IUiDispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public ChatComposerSession Create(
        IChatDataProvider provider,
        ChatComposerHostActions hostActions,
        bool initialSpeakerMuted)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(hostActions);

        var port = new ChatComposerRuntimePort(provider);
        var viewModel = new ChatComposerViewModel(_dispatcher, initialSpeakerMuted);
        var controller = new ChatComposerController(viewModel, port, hostActions);
        return new ChatComposerSession(viewModel, controller, hostActions);
    }
}
