using System;
using System.Threading;
using OpenClawTray.Presentation;

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
    private long _inputRevision;
    private readonly ISettingsStore _settingsStore;

    internal ChatComposerSession(
        ChatComposerViewModel viewModel,
        ChatComposerController controller,
        ChatComposerHostActions hostActions,
        ISettingsStore settingsStore)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Controller = controller ?? throw new ArgumentNullException(nameof(controller));
        HostActions = hostActions ?? throw new ArgumentNullException(nameof(hostActions));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _settingsStore.Changed += OnSettingsChanged;
        ViewModel.ApplySandboxSettings(_settingsStore.Current);
    }

    internal ChatComposerViewModel ViewModel { get; }

    internal ChatComposerController Controller { get; }

    internal ChatComposerHostActions HostActions { get; }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs args) =>
        ViewModel.ApplySandboxSettings(args.Snapshot);

    /// <summary>Applies the latest committed immutable projection with a
    /// session-monotonic revision that survives composer view remounts.</summary>
    internal void ApplyInputs(ChatComposerInputs inputs) =>
        ViewModel.ApplyInputs(inputs with
        {
            Revision = Interlocked.Increment(ref _inputRevision),
        });

    /// <summary>Disposes the controller then the view model exactly once. Safe to
    /// call multiple times; repeated calls are a no-op.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _settingsStore.Changed -= OnSettingsChanged;
        Controller.Dispose();
        ViewModel.Dispose();
    }
}
