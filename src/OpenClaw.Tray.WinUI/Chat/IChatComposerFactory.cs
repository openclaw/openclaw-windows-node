using OpenClaw.Chat;

namespace OpenClawTray.Chat;

/// <summary>
/// Stateless factory for a per-host-mount <see cref="ChatComposerSession"/>.
/// Registered as a singleton in the existing DI root; the factory itself starts no
/// background work and holds only the app-owned <c>IUiDispatcher</c> singleton.
/// Internal: only <see cref="ReactorChatHostExtensions.CreateComposerSession"/> (same
/// assembly) resolves and calls it. It is not part of the pre-existing public host
/// API (<see cref="ReactorChatHostExtensions.MountReactorChat"/>,
/// <see cref="MountedReactorChat"/>, <see cref="OpenClawReactorChatRootProps"/>),
/// so it stays internal rather than growing the public surface.
/// </summary>
internal interface IChatComposerFactory
{
    /// <summary>Creates one session bundling a view model, controller, and runtime
    /// port over <paramref name="provider"/>. Call once per host mount; the caller
    /// (<see cref="ReactorChatHostExtensions.CreateComposerSession"/>) owns disposal
    /// via the returned session.</summary>
    ChatComposerSession Create(
        IChatDataProvider provider,
        ChatComposerHostActions hostActions,
        bool initialSpeakerMuted);
}
