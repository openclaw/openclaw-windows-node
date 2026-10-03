using OpenClaw.Chat;
using OpenClawTray.Presentation;
using System;

namespace OpenClawTray.Chat;

/// <summary>
/// Production <see cref="IChatComposerFactory"/>. Borrows shared presentation
/// services; each session owns its settings subscription.
/// </summary>
internal sealed class ChatComposerFactory(IUiDispatcher dispatcher, ISettingsStore settingsStore) : IChatComposerFactory
{
    private readonly IUiDispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    private readonly ISettingsStore _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));

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
        return new ChatComposerSession(viewModel, controller, hostActions, _settingsStore);
    }
}
