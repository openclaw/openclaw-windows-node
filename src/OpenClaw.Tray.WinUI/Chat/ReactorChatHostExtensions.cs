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

    public static MountedReactorChat MountReactorChat(
        this Window window,
        Border target,
        IChatDataProvider provider,
        string? initialThreadId = null,
        Func<string, Task>? onReadAloud = null,
        Action? onStopSpeaking = null,
        Func<CancellationToken, Action?, Task<string?>>? onVoiceRequest = null,
        Action? onAttachClick = null,
        Action? onSettingsClick = null,
        Action<string>? onOpenCheckpoints = null,
        Action<bool>? onSpeakerMuteChanged = null,
        bool initialMuted = false,
        bool isCompact = false)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(target);
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

        var callbacks = new ReactorChatHostCallbacks();
        var props = new OpenClawReactorChatRootProps(
            provider,
            callbacks,
            initialThreadId,
            onReadAloud,
            onStopSpeaking,
            onVoiceRequest,
            onAttachClick,
            onSettingsClick,
            onOpenCheckpoints,
            onSpeakerMuteChanged,
            ConfirmResetAsync,
            initialMuted,
            isCompact);
        var host = new ReactorHostControl();
        host.Mount(_ => Component<OpenClawReactorChatRoot, OpenClawReactorChatRootProps>(props));
        target.Child = host;
        VisualTestCapture.ScheduleSignalCapture(target);
        return new MountedReactorChat(target, host, callbacks);
    }
}

/// <summary>
/// Imperative host handle used by the page and compact window for attachment
/// and voice input that originates outside the declarative chat tree.
/// </summary>
public sealed class MountedReactorChat(
    Border target,
    ReactorHostControl host,
    ReactorChatHostCallbacks callbacks) : IDisposable
{
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

    public void Dispose()
    {
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
