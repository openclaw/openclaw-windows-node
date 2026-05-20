using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using OpenClawTray.Chat.Explorations;
using System;
using System.Threading.Tasks;
using static OpenClawTray.FunctionalUI.Factories;
using static OpenClawTray.FunctionalUI.Core.Theme;

namespace OpenClawTray.Chat;

/// <summary>
    /// FunctionalUI root component used to render the OpenClaw chat surface (Header
/// + Timeline + InputBar + StatusBar). The surrounding XAML window/page owns
/// session navigation (via the existing NavigationView/SessionsPage) so
/// no Sidebar is rendered here.
/// </summary>
public sealed class OpenClawChatRoot : Component
{
    private readonly IChatDataProvider _provider;
    private readonly string? _initialThreadId;
    private readonly Func<string, Task>? _onReadAloud;
    private readonly Action? _onStopSpeaking;
    private readonly Func<CancellationToken, Action?, Task<string?>>? _onVoiceRequest;
    private readonly Action? _onAttachClick;
    private readonly Action? _onSettingsClick;
    private readonly Action<bool>? _onSpeakerMuteChanged;
    private readonly bool _initialMuted;
    private readonly bool _isCompact;
    private Action<ChatAttachment>? _onFileAttached;
    private Action<string?>? _setVoiceTranscript;
    private Action<float>? _setVoiceAudioLevel;
    private Action? _scrollToBottomToken;
    /// <summary>
    /// Programmatically start voice recording from outside the composer.
    /// Set by the composer during render.
    /// </summary>
    public Action? TriggerVoiceRecording { get; set; }

    /// <summary>
    /// Push mute state from outside (e.g. when another chat view toggles mute).
    /// Set by render.
    /// </summary>
    public Action<bool>? SetSpeakerMuted { get; set; }

    /// <summary>
    /// Callback invoked by the host window/page after a file is selected.
    /// Sets the pending attachment and triggers a re-render.
    /// </summary>
    public Action<ChatAttachment>? OnFileAttached
    {
        get => _onFileAttached;
        set => _onFileAttached = value;
    }

    /// <summary>
    /// Push streaming voice transcript text into the composer UI.
    /// Set to null when recording stops to clear the display.
    /// </summary>
    public Action<string?>? SetVoiceTranscript
    {
        get => _setVoiceTranscript;
        set => _setVoiceTranscript = value;
    }

    /// <summary>
    /// Push the current audio input level (0.0–1.0) into the composer UI.
    /// </summary>
    public Action<float>? SetVoiceAudioLevel
    {
        get => _setVoiceAudioLevel;
        set => _setVoiceAudioLevel = value;
    }

    public OpenClawChatRoot(
        IChatDataProvider provider,
        string? initialThreadId = null,
        Func<string, Task>? onReadAloud = null,
        Action? onStopSpeaking = null,
        Func<CancellationToken, Action?, Task<string?>>? onVoiceRequest = null,
        Action? onAttachClick = null,
        Action? onSettingsClick = null,
        Action<bool>? onSpeakerMuteChanged = null,
        bool initialMuted = false,
        bool isCompact = false)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _initialThreadId = initialThreadId;
        _onReadAloud = onReadAloud;
        _onStopSpeaking = onStopSpeaking;
        _onVoiceRequest = onVoiceRequest;
        _onAttachClick = onAttachClick;
        _onSettingsClick = onSettingsClick;
        _onSpeakerMuteChanged = onSpeakerMuteChanged;
        _initialMuted = initialMuted;
        _isCompact = isCompact;
    }

    public override Element Render()
    {
        // Subscribe to ChatExplorationState — without this, FunctionalUI may skip
        // re-rendering child Components (Composer/Timeline) because the props
        // they receive don't change. Bumping this Root's state invalidates
        // the whole tree so toggles always show in the live preview.
        var explorationRev = UseState(0, threadSafe: true);
        var explorationRevRef = UseRef(0);
        var pendingAttachment = UseState<ChatAttachment?>(null, threadSafe: true);
        var speakerMuted = UseState(_initialMuted, threadSafe: true);
        var voiceTranscript = UseState<string?>(null, threadSafe: true);
        var voiceAudioLevel = UseState(0f, threadSafe: true);
        var scrollToBottomToken = UseState(0, threadSafe: true);
        // Guards a duplicate suggestion-button click before the snapshot
        // reflects the optimistic local user entry (which then ordinarily
        // hides the zero-state buttons via the isEmptyConversation check).
        // Cleared automatically when the next snapshot arrives.
        var firstSendInFlight = UseState(false, threadSafe: true);

        // Wire the OnFileAttached callback so the host window/page can set the
        // pending attachment after the file picker completes.
        _onFileAttached = att => pendingAttachment.Set(att);
        _setVoiceTranscript = voiceTranscript.Set;
        _setVoiceAudioLevel = voiceAudioLevel.Set;
        _scrollToBottomToken = () => scrollToBottomToken.Set(scrollToBottomToken.Value + 1);
        SetSpeakerMuted = muted => speakerMuted.Set(muted);
        UseEffect((Func<Action>)(() =>
        {
            // Defer the re-render via DispatcherQueue. When the user picks an
            // item from a ComboBox in the explorations panel, SelectionChanged
            // fires synchronously; if we re-render this whole tree inline the
            // ComboBox's own post-selection bookkeeping races with our
            // reconciliation and the dropdown can become unresponsive on the
            // next click. Posting back to the dispatcher lets the ComboBox
            // finish its event handling before we reshape the tree.
            var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            EventHandler h = (_, _) =>
            {
                explorationRevRef.Current++;
                if (dq is not null)
                    dq.TryEnqueue(() => explorationRev.Set(explorationRevRef.Current));
                else
                    explorationRev.Set(explorationRevRef.Current);
            };
            ChatExplorationState.Changed += h;
            return () => ChatExplorationState.Changed -= h;
        }));

        var snapshotState = UseState<ChatDataSnapshot?>(null, threadSafe: true);
        var selectedIdState = UseState<string?>(_initialThreadId, threadSafe: true);
        // UseRef tracks the selected ID across renders so that closures captured
        // inside UseEffect always read the latest value (UseState structs go stale).
        var selectedIdRef = UseRef<string?>(_initialThreadId);
        selectedIdRef.Current = selectedIdState.Value;

        UseEffect((Func<Action>)(() =>
        {
            var setSnapshot = snapshotState.Set;
            var setSelected = selectedIdState.Set;

            EventHandler<ChatDataChangedEventArgs> onChanged = (_, e) =>
            {
                setSnapshot(e.Snapshot);
                // The debounce must clear only when the new snapshot is evidence
                // that the send round-trip has progressed for the compose key —
                // either the optimistic user entry landed (Timelines has it) or
                // an error event ended the turn. Clearing on every snapshot
                // (presence, models, status, channel health …) would re-enable
                // the suggestion buttons before the optimistic entry rendered
                // and let a double-click duplicate-send.
                if (e.Snapshot.ComposeTarget.SessionKey is { } ck &&
                    e.Snapshot.Timelines.TryGetValue(ck, out var ctl) &&
                    ctl.Entries.Any(x => x.Kind == ChatTimelineItemKind.User))
                {
                    firstSendInFlight.Set(false);
                }
                if (selectedIdRef.Current is null && e.Snapshot.DefaultThreadId is { } d)
                {
                    setSelected(d);
                    selectedIdRef.Current = d;
                }
            };
            _provider.Changed += onChanged;
            _ = LoadAsync(_provider, setSnapshot, () => selectedIdRef.Current, v => { setSelected(v); selectedIdRef.Current = v; });
            return () => _provider.Changed -= onChanged;
        }));

        var snapshot = snapshotState.Value;
        var selectedIdForMetadata = selectedIdState.Value ?? snapshot?.DefaultThreadId;
        var entryMetaSnapshot = UseMemo<IReadOnlyDictionary<string, ChatEntryMetadata>?>(() =>
        {
            if (selectedIdForMetadata is null || _provider is not OpenClawChatDataProvider nativeForMeta)
                return null;

            return nativeForMeta.GetEntryMetadata(selectedIdForMetadata);
        }, selectedIdForMetadata ?? string.Empty, snapshot);

        // Preview override (G) — only honored when the chat is bound to a
        // fake provider (i.e. the explorations window). Real production
        // chat surfaces ignore PreviewState so a stray Loading/EmptyZero
        // setting in the explorations panel can't black out the real UI.
        var previewState = _provider is FakeChatDataProvider
            ? ChatExplorationState.PreviewState
            : ChatPreviewState.Live;

        Element BuildLoadingElement()
        {
            var loadingBg = ChatExplorationState.BackdropMode == ChatBackdropMode.Solid
                ? (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["LayerFillColorDefaultBrush"]
                : (Microsoft.UI.Xaml.Media.Brush)new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            return Border(
                VStack(8,
                    ProgressRing().Size(28, 28).HAlign(HorizontalAlignment.Center),
                    Caption(LocalizationHelper.GetString("Chat_Root_ConnectingToGateway")).Foreground(SecondaryText).HAlign(HorizontalAlignment.Center)
                ).VAlign(VerticalAlignment.Center).HAlign(HorizontalAlignment.Center)
            ).Background(loadingBg);
        }

        if (snapshot is null)
        {
            return BuildLoadingElement();
        }

        var selectedId = selectedIdState.Value ?? snapshot.DefaultThreadId;
        var selectedThread = selectedId is { } id
            ? Array.Find(snapshot.Threads, t => t.Id == id)
            : null;

        // If no real session is selected yet but the provider exposes a ready
        // compose target (gateway connected + handshake snapshot resolved),
        // synthesize a transient compose-only ChatThread so the composer is
        // visible from the welcome screen. The synthetic thread's Id is the
        // canonical compose key — so when the gateway materializes the session
        // and SessionsUpdated arrives, Threads contains a real entry with the
        // same Id and `selectedThread` resolves to it on the next render
        // without any re-keying or migration.
        ChatThread? composeOnlyThread = null;
        if (selectedThread is null
            && snapshot.ComposeTarget.IsReady
            && snapshot.ComposeTarget.SessionKey is { } composeKey)
        {
            composeOnlyThread = new ChatThread
            {
                Id = composeKey,
                Title = "Main session",
                Status = ChatThreadStatus.Running,
                Activity = ChatActivity.Idle,
            };
        }

        // For everything below, `effectiveThread` is the thread the UI should
        // render against. `selectedThread` stays null when nothing materialized
        // exists yet so the zero-state still shows; `composeOnlyThread` exists
        // so the composer can be wired up.
        var effectiveThread = selectedThread ?? composeOnlyThread;

        // Lazy-load history the first time a real (materialized) thread is
        // selected. Don't fire for the compose-only synthetic thread — it
        // doesn't exist server-side yet, so chat.history would 404.
        if (selectedThread is not null && _provider is OpenClawChatDataProvider native)
        {
            var threadId = selectedThread.Id;
            RunFireAndForget(ct => native.LoadHistoryAsync(threadId, force: false, ct));
        }

        // Pull the timeline from the effective thread (so optimistic entries
        // from a pre-materialization first send are visible immediately).
        var timeline = effectiveThread is not null && snapshot.Timelines.TryGetValue(effectiveThread.Id, out var tl)
            ? tl
            : ChatTimelineState.Initial();

        var entries = (IReadOnlyList<ChatTimelineItem>)timeline.Entries;
        var connectedRaw = snapshot.ConnectionStatus;
        var hostConnected = connectedRaw is not null
            && connectedRaw.StartsWith("Connected", StringComparison.OrdinalIgnoreCase);
        var connState = hostConnected ? "connected"
            : (connectedRaw is not null && connectedRaw.StartsWith("Connecting", StringComparison.OrdinalIgnoreCase))
                ? "connecting"
                : "disconnected";

        // Header & divider intentionally hidden — the surrounding chrome
        // (NavigationView page or tray popup TitleBar) already shows the
        // session title; the in-chat header just duplicates it.
        Element header = Empty();

        // Per-entry metadata for the OpenClaw timeline footer (sender · time · model).
        // Keep the same dictionary instance across composer-only renders so the
        // timeline can skip re-rendering while the user types.
        var entryMeta = effectiveThread is null ? null : entryMetaSnapshot;

        // The gateway's default agent identity is "Field" (matches the web UI footer),
        // but for the WinUI tray we surface a generic "Assistant" label so the
        // thinking indicator and sender chip read naturally to all users.
        // TODO: wire to a real agent-name source (agents.list response or
        // sessionDefaults.defaultAgentId from hello-ok) once available, then
        // restore the per-agent name here.
        const string assistantSenderLabel = "Assistant";

        // Show inline "thinking" indicator when the turn is active but the
        // last visible entry is NOT an assistant block yet — i.e. we're between
        // the user's send and the first assistant delta arriving.
        var showThinking = timeline.TurnActive
            && (timeline.Entries.Count == 0
                || timeline.Entries[timeline.Entries.Count - 1].Kind != ChatTimelineItemKind.Assistant);

        // Apply preview-state overrides for the four data-dependent states.
        // These mutate locals only — real provider data on disk is untouched.
        // Note: Loading is also handled here (rather than via early return)
        // so that the OUTER Grid layout (header / divider / body / composer
        // rows) stays structurally identical across all preview states. A
        // structurally stable tree keeps FunctionalUI's reconciliation cheap and
        // avoids tearing down the timeline + composer subtrees, which was
        // observed to race with subsequent ComboBox SelectionChanged events
        // in the explorations panel and "lock" the dropdown after the first
        // pick. Only the body cell content (and composer visibility) swaps.
        var pendingPermissionOverride = timeline.PendingPermission;
        var turnActiveOverride = timeline.TurnActive;
        Element? bodyOverride = null;
        var suppressComposer = false;
        switch (previewState)
        {
            case ChatPreviewState.Loading:
                bodyOverride = BuildLoadingElement();
                suppressComposer = true;
                break;

            case ChatPreviewState.Empty:
                // Unified empty state: synthesize a thread + clear entries so
                // the new welcome zero-state renders. (Collapses what used to
                // be EmptyZero + EmptyThread — the user sees the same UI for
                // both because the distinction is backend-only.)
                selectedThread ??= SynthesizePreviewThread(snapshot);
                entries = Array.Empty<ChatTimelineItem>();
                showThinking = false;
                pendingPermissionOverride = null;
                turnActiveOverride = false;
                break;

            case ChatPreviewState.Thinking:
                selectedThread ??= SynthesizePreviewThread(snapshot);
                if (entries.Count == 0
                    || entries[entries.Count - 1].Kind == ChatTimelineItemKind.Assistant)
                {
                    entries = new[]
                    {
                        new ChatTimelineItem("preview-u1", ChatTimelineItemKind.User,
                            "What's the weather like in Seattle today?")
                    };
                }
                showThinking = true;
                turnActiveOverride = true;
                pendingPermissionOverride = null;
                break;

            case ChatPreviewState.PendingPermission:
                selectedThread ??= SynthesizePreviewThread(snapshot);
                pendingPermissionOverride = new ChatPermissionRequest(
                    RequestId: "preview-perm",
                    PermissionKind: "execute",
                    ToolName: "shell",
                    Detail: "Run `git status` in the current repo.");
                break;
        }

        // Production zero-state: triggered both when no thread is selected
        // *and* when a thread is selected but has no messages yet. These were
        // previously two distinct screens (PlaceholderEmptyState vs an empty
        // OpenClawChatTimeline) but render identically now — the user only
        // cares "this conversation is empty, what do I do?", not whether a
        // thread record exists in the backend.
        var isEmptyConversation = entries.Count == 0
            && !showThinking
            && pendingPermissionOverride is null;

        Element body = bodyOverride ?? (effectiveThread is null || isEmptyConversation
            ? RenderZeroState(suggestion =>
                {
                    if (firstSendInFlight.Value) return; // debounce double-click
                    if (effectiveThread is { } t)
                    {
                        firstSendInFlight.Set(true);
                        OnSend(t.Id, suggestion, null);
                    }
                }, suggestionsDisabled: firstSendInFlight.Value)
            : Component<OpenClawChatTimeline, OpenClawChatTimelineProps>(new(
                SessionId: effectiveThread.Id,
                Entries: entries,
                HasMoreHistory: false,
                OnLoadMoreHistory: null,
                EntryMetadata: entryMeta,
                UserSenderLabel: "OpenClaw Windows Tray",
                AssistantSenderLabel: assistantSenderLabel,
                DefaultModel: effectiveThread.Model,
                ShowThinkingIndicator: showThinking,
                OnReadAloud: _onReadAloud is not null
                    ? (text => _onReadAloud(text))
                    : null,
                OnStopSpeaking: _onStopSpeaking,
                ScrollToBottomToken: scrollToBottomToken.Value)));

        // Distinct list of channel labels (= thread titles) — feeds the
        // composer's first ComboBox so the user can switch chats from the
        // composer, not just the side rail.  Exclude cron sessions which
        // are automated/background and shouldn't appear in the chat switcher.
        var channelTitles = snapshot.Threads
            .Where(t => !string.IsNullOrEmpty(t.Title)
                     && !t.Id.Contains(":cron:", StringComparison.Ordinal))
            .Select(t => t.Title)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Element composer = (effectiveThread is not null && !suppressComposer)
            ? Component<OpenClawComposer, OpenClawComposerProps>(new(
                ConnectionState: connState,
                TurnActive: turnActiveOverride,
                PendingPermission: pendingPermissionOverride,
                ChannelLabel: effectiveThread.Title ?? "Main session",
                AvailableChannels: channelTitles,
                AvailableModels: snapshot.AvailableModels,
                CurrentModel: effectiveThread.Model,
                CurrentThinkingLevel: effectiveThread.ThinkingLevel,
                OnSend: (msg, att) =>
                {
                    pendingAttachment.Set(null);
                    OnSend(effectiveThread.Id, msg, att);
                },
                OnStop: () => OnStop(effectiveThread.Id),
                OnPermissionResponse: (rid, allow) => OnPermission(effectiveThread.Id, rid, allow),
                OnChannelChanged: title =>
                {
                    var match = Array.Find(snapshot.Threads, t => t.Title == title);
                    if (match is not null)
                    {
                        selectedIdState.Set(match.Id);
                        selectedIdRef.Current = match.Id;
                    }
                },
                OnModelChanged: model => RunFireAndForget(ct => _provider.SetModelAsync(effectiveThread.Id, model, ct)),
                OnThinkingLevelChanged: level => RunFireAndForget(ct => _provider.SetThinkingLevelAsync(effectiveThread.Id, level, ct)),
                OnPermissionsChanged: allowAll => RunFireAndForget(ct => _provider.SetPermissionModeAsync(effectiveThread.Id, allowAll, ct)),
                OnVoiceRequest: _onVoiceRequest,
                OnAttachClick: _onAttachClick,
                PendingAttachment: pendingAttachment.Value,
                OnAttachmentRemoved: () => pendingAttachment.Set(null),
                IsSpeakerMuted: speakerMuted.Value,
                OnSpeakerToggle: () =>
                {
                    var newMuted = !speakerMuted.Value;
                    speakerMuted.Set(newMuted);
                    _onSpeakerMuteChanged?.Invoke(newMuted);
                },
                OnSettingsClick: _onSettingsClick,
                VoiceTranscript: voiceTranscript.Value,
                VoiceAudioLevel: voiceAudioLevel.Value,
                RegisterVoiceStarter: starter => TriggerVoiceRecording = starter,
                IsCompact: _isCompact))
            : Empty();

        var divider = Empty();

        // Three rows now (composer absorbs the old StatusBar).
        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto],
            header.Grid(row: 0, column: 0),
            divider.Grid(row: 1, column: 0),
            body.Grid(row: 2, column: 0),
            composer.Grid(row: 3, column: 0)
        );
    }

    private static ChatThread SynthesizePreviewThread(ChatDataSnapshot snapshot)
    {
        return new ChatThread
        {
            Id = "preview-thread",
            Title = "Preview thread",
            Status = ChatThreadStatus.Running,
            Model = snapshot.AvailableModels is { Length: > 0 } m ? m[0] : null,
            CreatedAt = DateTimeOffset.Now.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.Now,
        };
    }

    /// <summary>
    /// Unified zero-state for the chat surface — shown when there is no
    /// thread selected OR the selected thread has no messages yet. Renders
    /// the app icon, a welcome message, and three prompt suggestions that
    /// invoke <paramref name="onSuggestionPicked"/> when clicked. The caller
    /// is responsible for routing the suggestion text into a send (typically
    /// via the active thread's OnSend handler).
    /// </summary>
    private static Element RenderZeroState(Action<string> onSuggestionPicked, bool suggestionsDisabled = false)
    {
        var welcomeTitle = LocalizedOrDefault("Chat_ZeroState_WelcomeTitle", "Welcome to OpenClaw");
        var welcomeSubtitle = LocalizedOrDefault("Chat_ZeroState_WelcomeSubtitle", "How can I help you today?");

        var suggestions = new[]
        {
            "Say hi 👋",
            "What can you do?",
            "Give me a quick tour of OpenClaw",
        };

        Element SuggestionButton(string text) =>
            Button(text, () => onSuggestionPicked(text))
                .Set(b =>
                {
                    b.HorizontalAlignment = HorizontalAlignment.Stretch;
                    b.HorizontalContentAlignment = HorizontalAlignment.Left;
                    b.Padding = new Thickness(12, 10, 12, 10);
                    b.CornerRadius = new CornerRadius(8);
                    b.IsEnabled = !suggestionsDisabled;
                });

        return Border(
            VStack(12,
                Image("ms-appx:///Assets/Square44x44Logo.targetsize-256_altform-unplated.png")
                    .Set(im =>
                    {
                        im.Width = 64;
                        im.Height = 64;
                        im.Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform;
                        im.HorizontalAlignment = HorizontalAlignment.Center;
                    }),
                TextBlock(welcomeTitle)
                    .Set(t =>
                    {
                        t.FontSize = 20;
                        t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                        t.HorizontalAlignment = HorizontalAlignment.Center;
                    }),
                Caption(welcomeSubtitle).Foreground(SecondaryText).HAlign(HorizontalAlignment.Center),
                VStack(6,
                    SuggestionButton(suggestions[0]),
                    SuggestionButton(suggestions[1]),
                    SuggestionButton(suggestions[2])
                ).Set(s =>
                {
                    s.HorizontalAlignment = HorizontalAlignment.Stretch;
                    s.MaxWidth = 360;
                    s.Margin = new Thickness(0, 8, 0, 0);
                })
            ).VAlign(VerticalAlignment.Center).HAlign(HorizontalAlignment.Center)
        ).Padding(24, 24, 24, 24);
    }

    private static string LocalizedOrDefault(string key, string fallback)
    {
        var value = LocalizationHelper.GetString(key);
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal)
            ? fallback
            : value;
    }

    private static Element PlaceholderEmptyThreadState(string connectionState)
    {
        var isConnected = string.Equals(connectionState, "connected", StringComparison.Ordinal);
        var msg = isConnected
            ? "Start a new OpenClaw chat from the composer below."
            : LocalizationHelper.GetString("Chat_Root_ConnectingToGateway");

        return Border(
            VStack(8,
                TextBlock("💬").FontSize(48).HAlign(HorizontalAlignment.Center),
                Caption(msg).Foreground(SecondaryText).HAlign(HorizontalAlignment.Center)
            ).VAlign(VerticalAlignment.Center).HAlign(HorizontalAlignment.Center)
        );
    }

    private void OnSend(string threadId, string message, ChatAttachment? attachment)
    {
        _scrollToBottomToken?.Invoke();
        IReadOnlyList<ChatAttachment>? attachments = attachment is not null
            ? new[] { attachment }
            : null;
        if (attachments is not null)
            RunFireAndForget(ct => _provider.SendMessageAsync(threadId, message, ct, attachments));
        else
            RunFireAndForget(ct => _provider.SendMessageAsync(threadId, message, ct));
    }

    private void OnStop(string threadId)
    {
        RunFireAndForget(ct => _provider.StopResponseAsync(threadId, ct));
    }

    private void OnPermission(string threadId, string requestId, bool allow)
    {
        RunFireAndForget(ct => _provider.RespondToPermissionAsync(threadId, requestId, allow, ct));
    }

    private static void RunFireAndForget(Func<CancellationToken, Task> op)
    {
        _ = Task.Run(async () =>
        {
            try { await op(CancellationToken.None); }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[chat] op failed: {ex}"); }
        });
    }

    private static async Task LoadAsync(
        IChatDataProvider provider,
        Action<ChatDataSnapshot?> setSnapshot,
        Func<string?> getSelected,
        Action<string?> setSelected)
    {
        try
        {
            var snap = await provider.LoadAsync();
            setSnapshot(snap);
            if (getSelected() is null && snap.DefaultThreadId is { } d)
                setSelected(d);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[chat] LoadAsync failed: {ex}");
        }
    }
}
