using OpenClaw.Chat;
using OpenClaw.Shared;
using System;
using System.Collections.Generic;

namespace OpenClawTray.Chat;

/// <summary>
/// Immutable per-render projection that <see cref="OpenClawReactorChatRoot"/> pushes
/// into the composer session after it resolves the provider snapshot, selection, and
/// effective thread. This is render-only truth: <see cref="ChatComposerViewModel"/>
/// never mutates it and never subscribes to the provider directly.
/// </summary>
/// <remarks>
/// <see cref="Revision"/> is a strictly increasing counter assigned by the root on
/// every push. <see cref="ChatComposerViewModel.ApplyInputs"/> rejects any input whose
/// revision is not greater than the currently-applied one, so an out-of-order dispatch
/// (for example a delayed UI-thread callback racing a newer render) cannot regress the
/// composer's view of session/model/thinking/queue/connection state.
/// </remarks>
internal sealed record ChatComposerInputs(
    long Revision,
    string ConnectionState,
    bool TurnActive,
    ChatThread CurrentThread,
    IReadOnlyList<ChatThread> AvailableChannels,
    string[] AvailableModels,
    IReadOnlyList<ChatModelChoice>? ModelChoices,
    bool MessageOptionsDisabled,
    IReadOnlyList<ChatQueuedMessage> QueuedMessages,
    IReadOnlyList<GatewayCommand>? AvailableCommands,
    bool CommandsSupported);
