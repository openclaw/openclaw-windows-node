using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClaw.Shared.Sessions;
using OpenClawTray.Helpers;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

/// <summary>
/// Mounts the native Reactor chat tree into the existing XAML chat target.
/// </summary>
public static class ReactorChatHostExtensions
{
    public static Action<Action> AsPost(this DispatcherQueue dispatcher) =>
        action =>
        {
            if (!dispatcher.TryEnqueue(() => action()))
                System.Diagnostics.Debug.WriteLine("Dropped chat UI update because DispatcherQueue rejected the work item.");
        };

    /// <summary>Builds the reset-confirmation dialog closure (centralized here so
    /// <see cref="Pages.ChatPage"/> and <see cref="Windows.ChatWindow"/> do not each
    /// duplicate it) and creates one <see cref="ChatComposerSession"/> from the
    /// resolved <paramref name="composerFactory"/>. Callers pass the returned session
    /// into <see cref="MountReactorChat"/>. <see cref="IChatComposerFactory"/> and
    /// <see cref="ChatComposerHostActions"/> stay internal — this helper, not a
    /// public factory parameter on <c>MountReactorChat</c>, is their only call site
    /// outside this file.</summary>
    internal static ChatComposerSession CreateComposerSession(
        Border target,
        IChatComposerFactory composerFactory,
        IChatDataProvider provider,
        Func<CancellationToken, Action?, Task<string?>>? onVoiceRequest,
        Action? onAttachClick,
        Action? onSettingsClick,
        Action<bool>? onSpeakerMuteChanged,
        bool initialMuted)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(composerFactory);
        ArgumentNullException.ThrowIfNull(provider);

        async Task<bool> ConfirmResetAsync(string sessionKey, string? displayName)
        {
            if (target.XamlRoot is null)
                return false;

            var prompt = SessionActionPlanner.BuildPrompt(
                SessionActionKind.Reset,
                sessionKey,
                displayName,
                SessionActionPlanner.IsMainSessionKeyShape(sessionKey));
            if (prompt is null)
                return true;

            var localized = SessionActionPromptLocalizer.Localize(prompt);
            var dialog = new ContentDialog
            {
                Title = localized.Title,
                Content = localized.Body,
                PrimaryButtonText = localized.ConfirmLabel,
                CloseButtonText = LocalizationHelper.GetString("SessionActionPrompt_CancelLabel"),
                DefaultButton = ContentDialogButton.None,
                XamlRoot = target.XamlRoot,
            };
            dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        var hostActions = new ChatComposerHostActions(
            ConfirmResetAsync,
            onAttachClick,
            onVoiceRequest,
            onSettingsClick,
            onSpeakerMuteChanged);
        return composerFactory.Create(provider, hostActions, initialMuted);
    }

    public static MountedReactorChat MountReactorChat(
        this Window window,
        Border target,
        IChatDataProvider provider,
        ChatComposerSession composerSession,
        string? initialThreadId = null,
        Func<string, Task>? onReadAloud = null,
        Action? onStopSpeaking = null,
        Action<string>? onOpenCheckpoints = null,
        bool isCompact = false)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(composerSession);

        // External attachment/voice/mute ingress binds directly to the session, once,
        // instead of being reassigned by the Reactor tree on every render.
        var callbacks = new ReactorChatHostCallbacks
        {
            AttachFiles = attachments => composerSession.Controller.AddAttachments(attachments),
            SetVoiceTranscript = text => composerSession.ViewModel.SetVoiceTranscript(text),
            SetVoiceAudioLevel = level => composerSession.ViewModel.SetVoiceAudioLevel(level),
            TriggerVoiceRecording = () => composerSession.Controller.StartVoiceRecording(),
            SetSpeakerMuted = muted => composerSession.ViewModel.SetSpeakerMuted(muted),
        };

        var props = new OpenClawReactorChatRootProps(
            provider,
            composerSession,
            initialThreadId,
            onReadAloud,
            onStopSpeaking,
            onOpenCheckpoints,
            isCompact);
        var host = new ReactorHostControl();
        host.Mount(_ => Component<OpenClawReactorChatRoot, OpenClawReactorChatRootProps>(props));
        target.Child = host;
        VisualTestCapture.ScheduleSignalCapture(target);
        return new MountedReactorChat(target, host, callbacks, composerSession);
    }
}

/// <summary>
/// Imperative host handle used by the page and compact window for attachment
/// and voice input that originates outside the declarative chat tree. Owns the one
/// <see cref="ChatComposerSession"/> created for this mount and disposes it exactly
/// once, alongside the Reactor host.
/// </summary>
public sealed class MountedReactorChat(
    Border target,
    ReactorHostControl host,
    ReactorChatHostCallbacks callbacks,
    ChatComposerSession session) : IDisposable
{
    private int _disposed;

    public void AttachFile(ChatAttachment attachment) => AttachFiles(new[] { attachment });

    public void AttachFiles(IReadOnlyList<ChatAttachment> attachments) =>
        callbacks.AttachFiles?.Invoke(attachments);

    public void SetVoiceTranscript(string? text) =>
        callbacks.SetVoiceTranscript?.Invoke(text);

    public void SetVoiceAudioLevel(float level) =>
        callbacks.SetVoiceAudioLevel?.Invoke(level);

    public void TriggerVoiceRecording() =>
        callbacks.TriggerVoiceRecording?.Invoke();

    public bool HasVoiceTrigger => callbacks.TriggerVoiceRecording is not null;

    public void SetSpeakerMuted(bool muted) =>
        callbacks.SetSpeakerMuted?.Invoke(muted);

    /// <summary>First-wins/idempotent: only the first call performs teardown
    /// (session disposal, callback clearing, host disposal, target detach); every
    /// later call — concurrent or sequential — is a no-op. This matters because
    /// <see cref="ReactorHostControl.Dispose"/> is not itself guaranteed idempotent,
    /// so a repeated external <c>Dispose()</c> call must never reach it twice.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        session.Dispose();
        callbacks.Clear();
        host.Dispose();
        if (ReferenceEquals(target.Child, host))
            target.Child = null;
    }
}

public sealed class ReactorChatHostCallbacks
{
    public Action<IReadOnlyList<ChatAttachment>>? AttachFiles { get; set; }
    public Action<string?>? SetVoiceTranscript { get; set; }
    public Action<float>? SetVoiceAudioLevel { get; set; }
    public Action? TriggerVoiceRecording { get; set; }
    public Action<bool>? SetSpeakerMuted { get; set; }

    public void Clear()
    {
        AttachFiles = null;
        SetVoiceTranscript = null;
        SetVoiceAudioLevel = null;
        TriggerVoiceRecording = null;
        SetSpeakerMuted = null;
    }
}
