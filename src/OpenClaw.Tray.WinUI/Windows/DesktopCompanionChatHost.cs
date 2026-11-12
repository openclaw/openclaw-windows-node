// Hosts the shared Reactor chat inside the lobster bubble. Call OpenAsync for
// the dedicated conversation or a notification session, and Hide when dismissed.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClawTray.Chat;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClawTray.Windows;

/// <summary>Owns bubble chat mounts, not the app's shared Gateway provider.</summary>
internal sealed class DesktopCompanionChatHost : IDisposable
{
    private readonly Window _window;
    private readonly DesktopCompanionPresentation _presentation;
    private readonly DesktopCompanionChatSessions _dedicatedSessions = new();
    private readonly Border _dedicatedTarget = new();
    private readonly Border _replyTarget = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressRing _loading = new() { IsActive = false, Width = 24, Height = 24, Visibility = Visibility.Collapsed };
    private readonly HyperlinkButton _connection;
    private readonly StackPanel _recovery;
    private readonly App? _app;
    private MountedReactorChat? _dedicatedChat, _replyChat;
    private OpenClawChatDataProvider? _provider;
    private CancellationTokenSource? _opening;
    private DesktopCompanionInputPrompt? _inputPrompt;
    private int _inputGeneration;
    private bool _pickingAttachment;
    private string? _replyKey, _requestedReply, _gatewayId;
    private bool _visible, _disposed;

    public Grid Content { get; } = new();

    public DesktopCompanionChatHost(Window window, DesktopCompanionPresentation presentation)
    {
        _window = window;
        _presentation = presentation;
        _app = Application.Current as App;
        var retry = new Button { Content = LocalizationHelper.GetString("Chat_Placement_Retry") };
        retry.Click += (_, _) => _ = OpenAsync(_requestedReply);
        _connection = new HyperlinkButton
        {
            Content = LocalizationHelper.GetString("WorkspaceShell_OpenConnection.Content"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new(0),
            Visibility = Visibility.Collapsed,
        };
        _connection.Click += (_, _) => _app?.ShowHub("connection");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(retry);
        actions.Children.Add(_connection);
        _recovery = new StackPanel { Spacing = 8, Margin = new(8) };
        _recovery.Children.Add(_loading);
        _recovery.Children.Add(_status);
        _recovery.Children.Add(actions);
        Content.Children.Add(_dedicatedTarget);
        Content.Children.Add(_replyTarget);
        Content.Children.Add(_recovery);
        if (_app is not null) _app.ChatProviderChanged += OnProviderChanged;
    }

    /// <summary>Opens a retained dedicated chat, or replies to an exact notification session.</summary>
    public async Task OpenAsync(string? replyKey = null)
    {
        if (_disposed) return;
        Hide();
        _visible = true;
        _requestedReply = replyKey;
        _opening?.Cancel();
        _opening?.Dispose();
        var opening = _opening = new CancellationTokenSource();
        var ct = opening.Token;
        _dedicatedChat?.SetHostVisible(false);
        _replyChat?.SetHostVisible(false);
        _dedicatedTarget.Visibility = _replyTarget.Visibility = Visibility.Collapsed;
        _recovery.Visibility = Visibility.Visible;
        _loading.IsActive = true;
        _loading.Visibility = Visibility.Visible;
        _connection.Visibility = Visibility.Collapsed;
        _status.Text = LocalizationHelper.GetString("Chat_Root_ConnectingToGateway");
        try
        {
            var provider = _app?.ChatProvider;
            var gatewayId = _app?.Registry?.GetActive()?.Id;
            if (provider is null || gatewayId is null)
            {
                _status.Text = LocalizationHelper.GetString("Chat_Composer_Placeholder_NotConnected");
                _connection.Visibility = Visibility.Visible;
                return;
            }
            if (!ReferenceEquals(_provider, provider) || _gatewayId != gatewayId)
            {
                DisposeMounts();
                _provider = provider;
                _gatewayId = gatewayId;
            }
            var snapshot = await provider.LoadAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (!snapshot.ComposeTarget.IsReady)
            {
                _status.Text = LocalizationHelper.GetString("Chat_Composer_Placeholder_NotConnected");
                _connection.Visibility = Visibility.Visible;
                return;
            }

            var dedicated = replyKey is null;
            var sessionKey = replyKey;
            if (dedicated)
            {
                sessionKey = await _dedicatedSessions.GetOrCreateAsync(gatewayId, async () =>
                {
                    var result = await provider.ExecuteLifecycleCommandAsync(
                        snapshot.ComposeTarget.SessionKey!, ChatLifecycleCommandKind.New);
                    if (!result.Succeeded || result.NewSessionKey is null)
                        throw new InvalidOperationException("The dedicated conversation could not be created.");
                    return result.NewSessionKey;
                }, ct);
            }
            ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(provider, _app?.ChatProvider) || gatewayId != _app?.Registry?.GetActive()?.Id)
                return;

            var target = dedicated ? _dedicatedTarget : _replyTarget;
            if (!dedicated && _replyKey != sessionKey)
            {
                _replyChat?.Dispose();
                _replyChat = null;
                _replyKey = sessionKey;
            }
            var mounted = dedicated ? _dedicatedChat : _replyChat;
            if (mounted is null)
            {
                var factory = _app!.Services!.GetRequiredService<IChatComposerFactory>();
                var composer = ReactorChatHostExtensions.CreateComposerSession(
                    target, factory, provider,
                    onVoiceRequest: (ct, started) => VoiceTranscribeAsync(mounted, ct, started),
                    onAttachClick: () => _ = PickAndAttachFilesAsync(mounted),
                    onSpeakerMuteChanged: null, initialMuted: true,
                    onSandboxSettingsClick: () => _app.ShowHub("sandbox"),
                    onSessionSelected: (key, _) =>
                    {
                        if (dedicated) _dedicatedSessions.Select(gatewayId, key);
                        else _replyKey = key;
                    });
                mounted = _window.MountReactorChat(target, provider, composer,
                    initialThreadId: sessionKey, agentRosterSource: _app.AppState, compact: true);
                if (dedicated) _dedicatedChat = mounted;
                else _replyChat = mounted;
            }
            mounted.SetHostVisible(true);
            target.Visibility = Visibility.Visible;
            _recovery.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.Warn($"Desktop lobster chat could not open ({ex.GetType().Name}).");
            if (ReferenceEquals(_opening, opening) && !_disposed)
                _status.Text = LocalizationHelper.GetString("DesktopCompanion_ChatFailed");
        }
        finally
        {
            if (ReferenceEquals(_opening, opening))
            {
                _loading.IsActive = false;
                _loading.Visibility = Visibility.Collapsed;
            }
        }
    }

    public void Hide()
    {
        _visible = false;
        _inputGeneration++;
        _inputPrompt?.Complete(false);
        _inputPrompt = null;
        _presentation.ShowInputPrompt(null);
        _opening?.Cancel();
        _dedicatedChat?.SetHostVisible(false);
        _replyChat?.SetHostVisible(false);
    }

    private bool IsCurrentInput(MountedReactorChat? mounted, int generation) =>
        !_disposed && _visible && generation == _inputGeneration && mounted is not null &&
        (ReferenceEquals(mounted, _dedicatedChat) || ReferenceEquals(mounted, _replyChat)) &&
        ReferenceEquals(_provider, _app?.ChatProvider) && _gatewayId == _app?.Registry?.GetActive()?.Id;

    private async Task PickAndAttachFilesAsync(MountedReactorChat? mounted)
    {
        var generation = _inputGeneration;
        if (_pickingAttachment || !IsCurrentInput(mounted, generation)) return;
        _pickingAttachment = true;
        try
        {
            var paths = await Win32FilePickerHelper.PickMultipleFilesAsync(
                WinRT.Interop.WindowNative.GetWindowHandle(_window),
                LocalizationHelper.GetString("ChatPage_AttachFile"));
            if (!IsCurrentInput(mounted, generation) || paths.Count == 0) return;
            var attachments = new List<ChatAttachment>(paths.Count);
            foreach (var path in paths)
            {
                attachments.Add(await ChatAttachment.FromFileAsync(path));
                if (!IsCurrentInput(mounted, generation)) return;
            }
            mounted!.AttachFiles(attachments);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Desktop lobster attachment failed ({ex.GetType().Name}).");
            if (IsCurrentInput(mounted, generation))
                await ShowInputDialogAsync(LocalizationHelper.GetString("ChatPage_CannotAttachFile"), ex.Message);
        }
        finally { _pickingAttachment = false; }
    }

    private async Task<string?> VoiceTranscribeAsync(
        MountedReactorChat? mounted, CancellationToken ct, Action? onRecordingStarted)
    {
        ct.ThrowIfCancellationRequested();
        var generation = _inputGeneration;
        if (!IsCurrentInput(mounted, generation)) return null;
        var voice = _app?.Settings?.NodeSttEnabled == true ? _app.VoiceServiceInstance : null;
        if (voice is null || !voice.IsModelDownloaded)
        {
            var inputOff = voice is null;
            await ShowInputDialogAsync(
                LocalizationHelper.GetString(inputOff ? "ChatVoiceDialog_InputOffTitle" : "ChatVoiceDialog_ModelRequiredTitle"),
                LocalizationHelper.GetString(inputOff ? "ChatVoiceDialog_InputOffMessage" : "ChatVoiceDialog_ModelRequiredMessage"),
                LocalizationHelper.GetString(inputOff ? "ChatVoiceDialog_OpenPermissionsSettings" : "ChatVoiceDialog_OpenVoiceSettings"),
                inputOff ? "permissions" : "voice", ct);
            return null;
        }

        bool IsCurrentCapture() => !ct.IsCancellationRequested && IsCurrentInput(mounted, generation);
        void PostProgress(Action update)
        {
            if (!Content.DispatcherQueue.TryEnqueue(() => { if (IsCurrentCapture()) update(); }))
                Logger.Warn("Desktop lobster voice progress could not reach the UI dispatcher.");
        }
        onRecordingStarted?.Invoke();
        try
        {
            var result = await voice.ListenOnceAsync(new SttListenArgs { TimeoutMs = SttCapability.MaxListenTimeoutMs }, ct,
                text => PostProgress(() => mounted!.SetVoiceTranscript(text)),
                level => PostProgress(() => mounted!.SetVoiceAudioLevel(level)));
            return IsCurrentInput(mounted, generation) ? result.Text : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warn($"Desktop lobster voice capture failed ({ex.GetType().Name}).");
            if (IsCurrentInput(mounted, generation))
                await ShowInputDialogAsync(LocalizationHelper.GetString("Chat_Composer_Tooltip_Voice"), ex.Message, ct: ct);
            return null;
        }
        finally
        {
            if (IsCurrentInput(mounted, generation))
            {
                mounted!.SetVoiceTranscript(null);
                mounted.SetVoiceAudioLevel(0);
            }
        }
    }

    private async Task ShowInputDialogAsync(
        string title, string message, string? actionLabel = null, string? route = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_visible || _disposed || Content.XamlRoot is null || _inputPrompt is not null) return;
        var generation = _inputGeneration;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = _inputPrompt = new DesktopCompanionInputPrompt(
            title, message, actionLabel, accepted => completion.TrySetResult(accepted));
        _presentation.ShowInputPrompt(prompt);
        using var cancellation = ct.Register(() =>
        {
            completion.TrySetResult(false);
        });
        try
        {
            if (await completion.Task &&
                _visible && !_disposed && generation == _inputGeneration && !ct.IsCancellationRequested &&
                route is not null)
                _app?.ShowHub(route);
        }
        finally
        {
            if (ReferenceEquals(_inputPrompt, prompt))
            {
                _inputPrompt = null;
                _presentation.ShowInputPrompt(null);
            }
        }
    }

    private void OnProviderChanged(object? sender, EventArgs args)
    {
        var wasVisible = _visible;
        Hide();
        _visible = wasVisible;
        _opening?.Cancel();
        DisposeMounts();
        _provider = null;
        _gatewayId = null;
        if (_visible)
        {
            _dedicatedTarget.Visibility = _replyTarget.Visibility = Visibility.Collapsed;
            _recovery.Visibility = Visibility.Visible;
            _loading.IsActive = false;
            _loading.Visibility = Visibility.Collapsed;
            _connection.Visibility = Visibility.Visible;
            _status.Text = LocalizationHelper.GetString("Chat_Composer_Placeholder_NotConnected");
            // A new connection can be a different Gateway. Require an explicit
            // retry before opening its dedicated chat, never reuse a reply key.
            _requestedReply = null;
        }
    }

    private void DisposeMounts()
    {
        _dedicatedChat?.Dispose();
        _replyChat?.Dispose();
        _dedicatedChat = _replyChat = null;
        _replyKey = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Hide();
        if (_app is not null) _app.ChatProviderChanged -= OnProviderChanged;
        DisposeMounts();
        _opening?.Dispose();
        _opening = null;
    }
}
