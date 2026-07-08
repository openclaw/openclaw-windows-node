using OpenClaw.Chat;
using OpenClaw.Shared;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.FunctionalUI.Hosting;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpenClawTray.Chat;

/// <summary>
/// Helper for hosting the <see cref="ChatRoot"/> FunctionalUI tree
/// inside an existing XAML window/page. The FunctionalUI host renders
/// into a target <see cref="Border"/>
/// rather than replacing <see cref="Window.Content"/>, so the surrounding
/// XAML chrome (TitleBar, NavigationView, popup header, ...) is preserved.
/// </summary>
public static class FunctionalChatHostExtensions
{
    /// <summary>
    /// Builds an "post to UI thread" callback suitable for
    /// <see cref="OpenClawChatDataProvider"/>'s <c>post</c> argument from
    /// the supplied window's dispatcher queue.
    /// </summary>
    public static Action<Action> AsPost(this DispatcherQueue dispatcher) =>
        action =>
        {
            // Always queue rather than invoking inline when we already hold
            // the dispatcher thread. The synchronous shortcut would let a
            // UI-thread Publish jump ahead of background-thread Publishes
            // that were already enqueued, so an older snapshot built before
            // ours could fire LAST and overwrite the latest state in the
            // subscribers (observed with the local exec-approval deny card
            // being clobbered by a stale 135-entry snapshot one ms later).
            // FIFO dispatch order keeps "newest build wins" for free.
            if (!dispatcher.TryEnqueue(() => action()))
                System.Diagnostics.Debug.WriteLine("Dropped chat UI update because DispatcherQueue rejected the work item.");
        };

    /// <summary>
    /// Mount <see cref="ChatRoot"/> into <paramref name="target"/>.
    /// Returns a <see cref="MountedFunctionalChat"/> that releases the FunctionalUI host
    /// when the page/window unloads and exposes the chat root for file attachment.
    /// </summary>
    public static MountedFunctionalChat MountFunctionalChat(
        this Window window,
        Border target,
        IChatDataProvider provider,
        string? initialThreadId = null,
        Func<string, Task>? onReadAloud = null,
        Action? onStopSpeaking = null,
        Func<CancellationToken, Action?, Task<string?>>? onVoiceRequest = null,
        Action? onAttachClick = null,
        Action? onSettingsClick = null,
        Action<bool>? onSpeakerMuteChanged = null,
        bool initialMuted = false,
        bool isCompact = false,
        bool suppressAutoDispose = false)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(provider);

        var root = new ChatRoot(provider, initialThreadId, onReadAloud, onStopSpeaking, onVoiceRequest, onAttachClick, onSettingsClick, onSpeakerMuteChanged, initialMuted, isCompact);
        var host = new FunctionalHostControl();
        host.SuppressAutoDispose = suppressAutoDispose;
        host.Mount(root);
        target.Child = host;
        return new MountedFunctionalChat(target, host, root);
    }
}

/// <summary>
/// Handle returned by <see cref="FunctionalChatHostExtensions.MountFunctionalChat"/>.
/// Exposes the <see cref="ChatRoot"/> so the host window/page can push file
/// attachments into the composer.
/// </summary>
public sealed class MountedFunctionalChat(Border target, FunctionalHostControl host, ChatRoot root) : IDisposable
{
    public ChatRoot ChatRoot => root;

    /// <summary>Push a picked file into the composer as a pending attachment.</summary>
    public void AttachFile(ChatAttachment attachment) => AttachFiles(new[] { attachment });

    /// <summary>Push picked files into the composer as pending attachments.</summary>
    public void AttachFiles(IReadOnlyList<ChatAttachment> attachments) => root.OnFilesAttached?.Invoke(attachments);

    /// <summary>Push streaming voice transcript text into the composer.</summary>
    public void SetVoiceTranscript(string? text) => root.SetVoiceTranscript?.Invoke(text);

    /// <summary>Push voice audio input level (0.0–1.0) into the composer.</summary>
    public void SetVoiceAudioLevel(float level) => root.SetVoiceAudioLevel?.Invoke(level);

    /// <summary>Programmatically start voice recording (e.g. from hotkey).</summary>
    public void TriggerVoiceRecording() => root.TriggerVoiceRecording?.Invoke();

    /// <summary>Whether the voice trigger callback has been registered by the composer.</summary>
    public bool HasVoiceTrigger => root.TriggerVoiceRecording != null;

    /// <summary>Push mute state from outside (e.g. cross-view sync).</summary>
    public void SetSpeakerMuted(bool muted) => root.SetSpeakerMuted?.Invoke(muted);

    public void Dispose()
    {
        host.Dispose();
        if (ReferenceEquals(target.Child, host))
            target.Child = null;
    }
}
