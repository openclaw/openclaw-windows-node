using System;
using System.Threading;

namespace OpenClawTray.Chat;

/// <summary>
/// One transient host-mount bundle: a <see cref="ChatComposerViewModel"/>, a
/// <see cref="ChatComposerController"/>, and their shared
/// <see cref="ChatComposerHostActions"/>. Created by the stateless
/// <see cref="IChatComposerFactory"/> and owned/disposed exactly once by
/// <see cref="MountedReactorChat"/>. <see cref="ChatPage"/> and <see cref="ChatWindow"/>
/// each hold a separate session over the same provider, so draft, attachment, focus,
/// popup, and voice state stay host-local while provider/runtime state is shared.
/// Public only because it is a property type on the pre-existing public
/// <see cref="OpenClawReactorChatRootProps"/> and a constructor parameter of the
/// pre-existing public <see cref="MountedReactorChat"/>; every other member (and the
/// constructor itself) stays internal — only <see cref="Dispose"/> is a public
/// <see cref="IDisposable"/> member the pre-existing host API needs to call.
/// </summary>
public sealed class ChatComposerSession : IDisposable
{
    private int _disposed;

    internal ChatComposerSession(
        ChatComposerViewModel viewModel,
        ChatComposerController controller,
        ChatComposerHostActions hostActions)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Controller = controller ?? throw new ArgumentNullException(nameof(controller));
        HostActions = hostActions ?? throw new ArgumentNullException(nameof(hostActions));
    }

    internal ChatComposerViewModel ViewModel { get; }

    internal ChatComposerController Controller { get; }

    internal ChatComposerHostActions HostActions { get; }

    /// <summary>Applies the root's latest immutable projection to the view model.</summary>
    internal void ApplyInputs(ChatComposerInputs inputs) => ViewModel.ApplyInputs(inputs);

    /// <summary>Disposes the controller then the view model exactly once. Safe to
    /// call multiple times; repeated calls are a no-op.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Controller.Dispose();
        ViewModel.Dispose();
    }
}
