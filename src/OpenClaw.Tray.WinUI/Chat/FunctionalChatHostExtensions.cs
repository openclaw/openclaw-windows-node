using OpenClaw.Chat;
using OpenClaw.Shared;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.FunctionalUI.Hosting;
using System;
using System.Threading.Tasks;

namespace OpenClawTray.Chat;

/// <summary>
/// Helper for hosting the <see cref="OpenClawChatRoot"/> FunctionalUI tree
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
            if (dispatcher.HasThreadAccess)
            {
                action();
                return;
            }

            if (!dispatcher.TryEnqueue(() => action()))
                System.Diagnostics.Debug.WriteLine("Dropped chat UI update because DispatcherQueue rejected the work item.");
        };

    /// <summary>
    /// Mount <see cref="OpenClawChatRoot"/> into <paramref name="target"/>.
    /// Returns a <see cref="MountedFunctionalChat"/> that releases the FunctionalUI host
    /// when the page/window unloads and exposes the chat root for file attachment.
    /// </summary>
    public static MountedFunctionalChat MountFunctionalChat(
        this Window window,
        Border target,
        IChatDataProvider provider,
        string? initialThreadId = null,
        Func<string, Task>? onReadAloud = null,
        Func<CancellationToken, Task<string?>>? onVoiceRequest = null,
        Action? onAttachClick = null,
        Action? onSettingsClick = null,
        Action<bool>? onSpeakerMuteChanged = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(provider);

        var root = new OpenClawChatRoot(provider, initialThreadId, onReadAloud, onVoiceRequest, onAttachClick, onSettingsClick, onSpeakerMuteChanged);
        var host = new FunctionalHostControl();
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
public sealed class MountedFunctionalChat(Border target, FunctionalHostControl host, OpenClawChatRoot root) : IDisposable
{
    public OpenClawChatRoot ChatRoot => root;

    /// <summary>Push a picked file into the composer as a pending attachment.</summary>
    public void AttachFile(ChatAttachment attachment) => root.OnFileAttached?.Invoke(attachment);

    /// <summary>Push streaming voice transcript text into the composer.</summary>
    public void SetVoiceTranscript(string? text) => root.SetVoiceTranscript?.Invoke(text);

    /// <summary>Push voice audio input level (0.0–1.0) into the composer.</summary>
    public void SetVoiceAudioLevel(float level) => root.SetVoiceAudioLevel?.Invoke(level);

    public void Dispose()
    {
        host.Dispose();
        if (ReferenceEquals(target.Child, host))
            target.Child = null;
    }
}
