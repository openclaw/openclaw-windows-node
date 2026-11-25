using OpenClaw.Shared;
using OpenClawTray.Presentation;
using System.Text.RegularExpressions;

namespace OpenClawTray.Chat;

internal enum ChatPlacementError
{
    DiscoveryFailed,
    BrowseFailed,
    PreparationFailed,
    PreparationPending,
    NotReady,
    HistoryPending,
    Interrupted,
    NoLongerEmpty,
    DraftOptionsFailed,
    DraftCreationFailed,
    DraftCreationUncertain,
    GatewayDefaultOnly
}

internal sealed record ChatPlacementState(
    ChatPlacementCatalog? Catalog = null,
    ChatPlacementSelection? Selection = null,
    bool Loading = false,
    bool Preparing = false,
    ChatPlacementError? Error = null,
    bool Browsing = false,
    bool BrowsingLoading = false,
    ChatWorkspaceDirectory? Directory = null,
    bool SelectionIsKnown = false,
    bool GatewayDefaultOnly = false,
    string? DraftStatusMessage = null);

/// <summary>Host-local first-turn location intent. Opening or choosing a row never mutates the Gateway.</summary>
internal sealed class ChatPlacementController(
    IChatPlacementGateway gateway, IUiDispatcher dispatcher, TimeProvider? timeProvider = null) : IDisposable
{
    private const int DefaultMaterializationHistoryTimeoutMs = 120_000;
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private CancellationTokenSource _scopeCancellation = new();
    private ChatPlacementState _state = new();
    private ChatDraft? _draft;
    private string? _threadId;
    private string _agentId = "main";
    private long? _epoch;
    private int _generation;
    private int _browseGeneration;
    private bool _canEdit;
    private bool _canChangeSessionOptions;
    private bool _targetEmpty;
    private bool _historyReady;
    private bool _targetNew;
    private TaskCompletionSource? _materializationHistory;
    // Accepted Gateway mutations outlive navigation. Keep their intent and drain state with the session.
    private readonly Dictionary<(string ThreadId, string AgentId), LocationIntent> _intents = new();
    private bool _draftNeedsSelection;
    private string? _activeSendThreadId;
    private long _activeSendId;
    private long _sendOperationId;
    private readonly HashSet<string> _knownCreatedSessionKeys = new(StringComparer.Ordinal);
    private LocationIntent? _intent;
    private bool _disposed;

    private sealed class LocationIntent(ChatPlacementSelection selection, ChatDraft? draft)
    {
        internal ChatPlacementSelection Selection = selection;
        internal bool Dirty = true;
        internal bool Preparing;
        internal bool OutcomeUncertain;
        internal ChatPlacementError? Error;
        internal ChatDraft? Draft = draft;
    }

    internal event Action? Changed;
    internal long Revision { get; private set; }
    internal ChatPlacementState State { get { lock (_gate) return _state; } }
    internal bool CanEdit
    {
        get { lock (_gate) return !_disposed && _canEdit && !_state.Preparing &&
            _activeSendThreadId != _threadId && _draft?.GatewaySessionKey is null; }
    }
    internal bool RequiresPreparation
    {
        get { lock (_gate) return _intent?.Dirty == true || _draftNeedsSelection; }
    }

    internal sealed class SendLease(
        ChatPlacementController owner, long id, string threadId, int generation, long epoch, bool requiresPreparation)
        : IDisposable
    {
        private int _disposed;
        internal bool IsCurrent => Volatile.Read(ref _disposed) == 0 &&
            owner.IsCurrentSend(id, threadId, generation, epoch, requiresPreparation);
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.EndSend(id);
        }
    }

    internal SendLease? BeginSend(string threadId)
    {
        SendLease? lease;
        lock (_gate)
        {
            if (_disposed || _threadId != threadId || _epoch is not { } epoch ||
                _activeSendThreadId is not null)
                return null;
            var id = ++_sendOperationId;
            _activeSendId = id;
            _activeSendThreadId = threadId;
            lease = new(this, id, threadId, _generation, epoch,
                _draft?.GatewaySessionKey is null && (_intent?.Dirty == true || _draftNeedsSelection));
        }
        Notify();
        return lease;
    }

    private bool IsCurrentSend(long id, string threadId, int generation, long epoch, bool requiresPreparation)
    {
        lock (_gate)
            return !_disposed && _activeSendId == id && _activeSendThreadId == threadId &&
                _generation == generation && _threadId == threadId && _epoch == epoch &&
                gateway.ConnectionEpoch == epoch &&
                (!requiresPreparation || _targetEmpty && _historyReady);
    }

    private void EndSend(long id)
    {
        lock (_gate)
        {
            if (_disposed || _activeSendId != id) return;
            _activeSendThreadId = null;
        }
        Notify();
    }

    internal void MarkCreatedSession(string sessionKey)
    {
        if (string.IsNullOrWhiteSpace(sessionKey)) return;
        lock (_gate) _knownCreatedSessionKeys.Add(sessionKey);
    }

    internal bool IsKnownCreatedSession(string sessionKey)
    {
        lock (_gate) return _knownCreatedSessionKeys.Contains(sessionKey);
    }

    internal void ApplyInputs(ChatComposerInputs inputs)
    {
        bool load;
        CancellationTokenSource? previous = null;
        lock (_gate)
        {
            if (_disposed) return;
            var previousDraftKey = _draft?.Key;
            _draft = inputs.PlacementDraft;
            if (previousDraftKey != _draft?.Key && _state.DraftStatusMessage is not null)
                _state = _state with { DraftStatusMessage = null };
            var agentId = inputs.CurrentThread.AgentId ?? "main";
            var epoch = inputs.ConnectionState == "connected" ? gateway.ConnectionEpoch : null;
            if (_threadId != inputs.CurrentThread.Id || _agentId != agentId || _epoch != epoch)
            {
                _generation++;
                _browseGeneration++;
                _threadId = inputs.CurrentThread.Id;
                _agentId = agentId;
                _epoch = epoch;
                _intent = _intents.GetValueOrDefault((_threadId, _agentId));
                _state = new(
                    Selection: _intent?.Selection,
                    Preparing: _intent?.Preparing == true,
                    Error: _intent?.Error,
                    GatewayDefaultOnly: _intent?.Error == ChatPlacementError.GatewayDefaultOnly,
                    SelectionIsKnown: _intent?.Selection is not null);
                _materializationHistory = null;
                previous = _scopeCancellation;
                _scopeCancellation = new();
            }
            if (_intent is not null)
            {
                _intent.Draft = _draft ?? _intent.Draft;
                RestoreIntentFromDraftLocked(_intent);
            }
            _historyReady = inputs.PlacementHistoryReady;
            var targetEmpty = inputs.PlacementTargetIsEmpty;
            if (previous is null && _state.Preparing && _targetNew && !inputs.PlacementTargetIsNew &&
                !_historyReady && targetEmpty && inputs.CanChangeSessionOptions)
            {
                // Publishing the session we are preparing is not a reconnect or loss of emptiness.
                _materializationHistory = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            if (_historyReady)
            {
                _materializationHistory?.TrySetResult();
                _materializationHistory = null;
                if (_state.Error == ChatPlacementError.HistoryPending)
                    _state = _state with { Error = _intent?.Error };
            }
            if (_targetNew && !inputs.PlacementTargetIsNew && _intent is null)
                _state = _state with { SelectionIsKnown = false };
            _targetNew = inputs.PlacementTargetIsNew;
            _canEdit = inputs.ShowPlacementPickers && inputs.CanChangeSessionOptions && _historyReady;
            if (_targetEmpty && _canChangeSessionOptions &&
                (!targetEmpty || !_historyReady && _materializationHistory is null || !inputs.CanChangeSessionOptions) &&
                _state.Preparing && previous is null)
            {
                previous = _scopeCancellation;
                _scopeCancellation = new();
            }
            _targetEmpty = targetEmpty;
            if (_intent is null && targetEmpty && _draft?.PlacementIntent is { } savedIntent)
            {
                _intent = new(savedIntent.Selection, _draft)
                {
                    Dirty = savedIntent.RequiresPreparation,
                    Preparing = savedIntent.Preparing,
                    OutcomeUncertain = savedIntent.OutcomeUncertain,
                    Error = savedIntent.Error
                };
                _intents[(_threadId!, _agentId)] = _intent;
                _state = _state with
                {
                    Selection = savedIntent.Selection,
                    Preparing = savedIntent.Preparing,
                    Error = savedIntent.Error,
                    SelectionIsKnown = true
                };
            }
            _draftNeedsSelection = inputs.PlacementRequiresCreation && _intent is null && targetEmpty;
            if (_historyReady && _draftNeedsSelection && _state.Selection is { } defaults)
                SetSelection(defaults, changed: true);
            _canChangeSessionOptions = inputs.CanChangeSessionOptions;
            if (!_canEdit && _state.Error is ChatPlacementError.DiscoveryFailed or ChatPlacementError.BrowseFailed)
                _state = _state with { Error = _intent?.Error };
            load = _canEdit && _epoch.HasValue && _state.Catalog is null &&
                !_state.Loading && _state.Error is null;
        }
        CancelAndDispose(previous);
        Notify();
        if (load) _ = RefreshAsync();
    }

    internal async Task RefreshAsync()
    {
        int generation;
        string agent;
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed || !_canEdit || _state.Loading || _state.Preparing) return;
            generation = _generation;
            agent = _agentId;
            token = _scopeCancellation.Token;
            _state = _state with
            {
                Loading = true,
                Error = _intent?.Error,
                GatewayDefaultOnly = false
            };
        }
        Notify();
        try
        {
            var catalog = await gateway.LoadAsync(agent, token).ConfigureAwait(false);
            Update(generation, state =>
            {
                if (state.GatewayDefaultOnly)
                    return state with { Loading = false };
                var local = catalog.Environments.FirstOrDefault(environment => environment.IsLocal);
                var selected = state.Selection;
                if (selected is null && local is not null)
                    selected = new(local, catalog.DefaultWorkspace);
                else if (selected is not null)
                {
                    var environment = catalog.Environments.FirstOrDefault(item => item.Id == selected.Environment.Id);
                    selected = selected with
                    {
                        Environment = environment ?? selected.Environment with { Unavailable = ChatEnvironmentUnavailable.Offline }
                    };
                }
                if (_intent is not null && selected is not null)
                    _intent.Selection = selected;
                if (_intent?.Error == ChatPlacementError.GatewayDefaultOnly)
                {
                    _intent.Error = null;
                    PersistIntent(_intent);
                }
                if (_draftNeedsSelection && _historyReady && _targetEmpty && selected is not null)
                    SetSelection(selected, changed: true);
                return state with
                {
                    Catalog = catalog, Selection = selected, Loading = false, Error = _intent?.Error,
                    SelectionIsKnown = state.SelectionIsKnown || _targetNew || _intent?.Dirty == true,
                    GatewayDefaultOnly = false
                };
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Fail(generation, ex, state => state with { Loading = false });
        }
    }

    internal void SelectEnvironment(string id)
    {
        lock (_gate)
        {
            if (!CanEdit || _state.Catalog is not { } catalog || _state.Selection is not { } selection)
                return;
            var environment = catalog.Environments.FirstOrDefault(item => item.Id == id);
            if (environment?.IsSelectable != true) return;
            SetSelection(selection with { Environment = environment }, selection.Environment != environment);
        }
        Notify();
    }

    internal void SelectWorkspace(ChatWorkspace workspace)
    {
        lock (_gate)
        {
            if (!CanEdit || _state.Selection is not { } selection || _state.Catalog is not { } catalog) return;
            var current = workspace.Id == "default"
                ? catalog.DefaultWorkspace
                : catalog.Workspaces.FirstOrDefault(item => item.Id == workspace.Id);
            if (current is not null)
                workspace = current;
            else if (workspace.GitUrl is { } url && IsGitUrl(url))
                workspace = new("repository:" + url, url, GitUrl: url);
            else if (!(_state is { Browsing: true, BrowsingLoading: false, Directory: { } directory } &&
                (workspace.Path == directory.Path || directory.Folders.Any(folder => folder == workspace))))
            {
                System.Diagnostics.Trace.WriteLine("[chat] Workspace activation ignored because its catalog or folder listing is no longer current.");
                return;
            }
            SetSelection(selection with { Workspace = workspace }, selection.Workspace != workspace);
            _browseGeneration++;
            _state = _state with
            {
                Browsing = false,
                BrowsingLoading = false,
                Error = _intent?.Error
            };
        }
        Notify();
    }

    private void SetSelection(ChatPlacementSelection selection, bool changed)
    {
        _draftNeedsSelection = false;
        if (changed || !_state.SelectionIsKnown)
        {
            _intent ??= new(selection, _draft);
            _intent.Draft = _draft ?? _intent.Draft;
            _intent.Selection = selection;
            _intent.Dirty = true;
            _intent.Error = _intent.OutcomeUncertain ? ChatPlacementError.Interrupted : null;
            _intents[(_threadId!, _agentId)] = _intent;
            PersistIntent(_intent);
        }
        _state = _state with
        {
            Selection = selection,
            Error = _intent?.Error,
            SelectionIsKnown = true,
            GatewayDefaultOnly = false
        };
    }

    internal bool TryUseGatewayDefaultLocation(string threadId)
    {
        lock (_gate)
        {
            if (_disposed || _threadId != threadId || !_targetEmpty || !_historyReady ||
                _draft is not { UsesGatewayDefaultAgent: false } ||
                _intent?.OutcomeUncertain == true)
            {
                return false;
            }

            var selection = _state.Selection ?? _intent?.Selection;
            var noLocationChosen = selection is null && _intent is null && _draftNeedsSelection;
            if (!_state.GatewayDefaultOnly && !noLocationChosen)
                return false;

            _state = _state with
            {
                GatewayDefaultOnly = true,
                Error = ChatPlacementError.GatewayDefaultOnly
            };
            if (_intent is not null)
            {
                _intent.Dirty = false;
                _intent.Error = ChatPlacementError.GatewayDefaultOnly;
                PersistIntent(_intent);
            }
        }
        Notify();
        return true;
    }

    internal async Task BrowseAsync(string? path = null)
    {
        int generation;
        int browseGeneration;
        long epoch;
        CancellationToken token;
        lock (_gate)
        {
            if (!CanEdit || _epoch is not { } currentEpoch) return;
            generation = _generation;
            browseGeneration = ++_browseGeneration;
            epoch = currentEpoch;
            token = _scopeCancellation.Token;
            _state = _state with { Browsing = true, BrowsingLoading = true, Directory = null, Error = _intent?.Error };
        }
        Notify();
        try
        {
            var directory = await gateway.BrowseAsync(path, epoch, token).ConfigureAwait(false);
            Update(generation, state => browseGeneration == _browseGeneration
                ? state with { Directory = directory, BrowsingLoading = false }
                : state);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[chat] Workspace browsing failed: {ex}");
            Update(generation, state => browseGeneration == _browseGeneration
                ? state with { BrowsingLoading = false, Error = _intent?.Error ?? (_canEdit ? ChatPlacementError.BrowseFailed : null) }
                : state);
        }
    }

    internal void CloseBrowser()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _browseGeneration++;
            _state = _state with { Browsing = false, BrowsingLoading = false, Error = _intent?.Error };
        }
        Notify();
    }

    internal async Task<bool> PrepareAsync(string threadId, CancellationToken cancellationToken)
    {
        int generation;
        string agent;
        long epoch;
        ChatPlacementSelection selection;
        CancellationTokenSource operation;
        LocationIntent intent;
        ChatDraft? draft;
        lock (_gate)
        {
            if (_draft?.CreationOutcomeUncertain == true)
            {
                _state = _state with { Error = ChatPlacementError.DraftCreationUncertain };
                Notify();
                return false;
            }
            if (_draft is { GatewaySessionKey: { } gatewaySessionKey } &&
                gatewaySessionKey == threadId &&
                _threadId == _draft.Key &&
                _epoch is { } defaultEpoch &&
                gateway.ConnectionEpoch == defaultEpoch)
            {
                return true;
            }
            if (_intent is not null)
                RestoreIntentFromDraftLocked(_intent);
            if ((_targetNew || _materializationHistory is not null) && !_historyReady)
            {
                _state = _state with { Error = ChatPlacementError.HistoryPending };
                Notify();
                return false;
            }
            if (_intent is not { Dirty: true } && !_draftNeedsSelection)
                return true;
            if (_intent?.Preparing == true)
            {
                _state = _state with { Error = ChatPlacementError.PreparationPending };
                Notify();
                return false;
            }
            if (_intent is { OutcomeUncertain: true })
            {
                _state = _state with { Error = ChatPlacementError.Interrupted };
                Notify();
                return false;
            }
            if (_draftNeedsSelection)
            {
                _state = _state with { Error = _state.Error ?? ChatPlacementError.NotReady };
                Notify();
                return false;
            }
            if (_intent is not { Dirty: true } currentIntent) return true;
            if (!IsCurrent(threadId)) return false;
            if (!_historyReady)
            {
                _state = _state with { Error = _intent.Error ?? ChatPlacementError.HistoryPending };
                Notify();
                return false;
            }
            if (!_targetEmpty)
            {
                CancelStaleIntent();
                return false;
            }
            if (_threadId != threadId || !_canChangeSessionOptions || _state.Loading ||
                _epoch is not { } currentEpoch || gateway.ConnectionEpoch != currentEpoch ||
                _state.Selection is not { } selected || !selected.Environment.IsSelectable)
            {
                _state = _state with { Error = _intent.Error ?? ChatPlacementError.NotReady };
                Notify();
                return false;
            }
            generation = _generation;
            agent = _agentId;
            epoch = currentEpoch;
            selection = selected;
            draft = _draft;
            intent = currentIntent;
            intent.Selection = selection;
            intent.Preparing = true;
            intent.Error = null;
            PersistIntent(intent);
            operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _scopeCancellation.Token);
            _state = _state with { Preparing = true, Error = null };
        }
        Notify();
        var preparationConfirmed = false;
        try
        {
            await gateway.PrepareAsync(threadId, agent, selection, epoch, operation.Token,
                token => WaitForCurrentHistoryAsync(generation, epoch, token)).ConfigureAwait(false);
            draft?.RememberGatewaySessionKey(threadId);
            lock (_gate) _knownCreatedSessionKeys.Add(threadId);
            preparationConfirmed = true;
            lock (_gate)
            {
                ConfirmIntent(intent);
            }
            try
            {
                await WaitForCurrentHistoryAsync(generation, epoch, operation.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_intent, intent) && generation == _generation)
                        _state = _state with { Error = ChatPlacementError.HistoryPending };
                }
                return false;
            }
            lock (_gate)
            {
                if (_disposed || generation != _generation || gateway.ConnectionEpoch != epoch)
                    return false;
                if (!_historyReady || !_targetEmpty)
                {
                    _state = _state with
                    {
                        Error = !_targetEmpty ? ChatPlacementError.NoLongerEmpty : ChatPlacementError.HistoryPending
                    };
                    return false;
                }
                _state = _state with { SelectionIsKnown = true };
            }
            return true;
        }
        catch (ChatPlacementNotMutatedException)
        {
            lock (_gate)
                InterruptIntent(intent, outcomeUncertain: false);
            return false;
        }
        catch (OperationCanceledException) when (preparationConfirmed)
        {
            lock (_gate)
            {
                if (!_disposed && generation == _generation && ReferenceEquals(_intent, intent))
                    _state = _state with
                    {
                        Error = !_targetEmpty ? ChatPlacementError.NoLongerEmpty :
                            !_historyReady ? ChatPlacementError.HistoryPending : null
                    };
            }
            return false;
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
                InterruptIntent(intent, outcomeUncertain: true);
            return false;
        }
        catch (ChatPlacementOutcomeUncertainException ex)
        {
            System.Diagnostics.Trace.WriteLine($"[chat] Location preparation outcome is uncertain: {ex}");
            lock (_gate)
                InterruptIntent(intent, outcomeUncertain: true);
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[chat] Location preparation failed: {ex}");
            lock (_gate)
            {
                intent.Error = intent.OutcomeUncertain ? ChatPlacementError.Interrupted : ChatPlacementError.PreparationFailed;
                PersistIntent(intent);
            }
            return false;
        }
        finally
        {
            operation.Dispose();
            lock (_gate)
            {
                intent.Preparing = false;
                if (ReferenceEquals(_intent, intent))
                {
                    if (_historyReady)
                        _materializationHistory = null;
                    _state = _state with { Preparing = false, Error = _state.Error ?? intent.Error };
                }
                PersistIntent(intent);
            }
            Notify();
        }
    }

    private async Task WaitForCurrentHistoryAsync(int generation, long epoch, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task pending;
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_disposed || generation != _generation || _epoch != epoch || gateway.ConnectionEpoch != epoch ||
                    !_targetEmpty || !_canChangeSessionOptions)
                    throw new OperationCanceledException(cancellationToken);
                if (_historyReady)
                    return;
                if (_materializationHistory is null)
                    throw new OperationCanceledException(cancellationToken);
                pending = _materializationHistory.Task;
            }
            await pending.WaitAsync(MaterializationHistoryTimeout(), _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private static TimeSpan MaterializationHistoryTimeout()
    {
        const string variable = "OPENCLAW_CHAT_MATERIALIZATION_HISTORY_TIMEOUT_MS";
        var configured = Environment.GetEnvironmentVariable(variable);
        if (configured is not null)
        {
            if (int.TryParse(configured, out var milliseconds) && milliseconds > 0)
                return TimeSpan.FromMilliseconds(milliseconds);
            System.Diagnostics.Trace.WriteLine($"[chat] {variable} must be a positive integer; using the default history wait timeout.");
        }
        return TimeSpan.FromMilliseconds(DefaultMaterializationHistoryTimeoutMs);
    }

    private void InterruptIntent(LocationIntent intent, bool outcomeUncertain)
    {
        intent.OutcomeUncertain = outcomeUncertain;
        intent.Error = ChatPlacementError.Interrupted;
        if (ReferenceEquals(_intent, intent) && _historyReady && !_targetEmpty)
            intent.Dirty = false;
        else if (outcomeUncertain)
            intent.Dirty = true;
        PersistIntent(intent);
    }

    private void ConfirmIntent(LocationIntent intent)
    {
        intent.Dirty = false;
        intent.OutcomeUncertain = false;
        intent.Error = null;
        PersistIntent(intent);
    }

    private void RestoreIntentFromDraftLocked(LocationIntent intent)
    {
        if (intent.Draft?.PlacementIntent is not { } savedIntent)
            return;

        intent.Selection = savedIntent.Selection;
        intent.Dirty = savedIntent.RequiresPreparation;
        intent.OutcomeUncertain = savedIntent.OutcomeUncertain;
        intent.Preparing = savedIntent.Preparing;
        intent.Error = savedIntent.Error;
        if (ReferenceEquals(_intent, intent))
        {
            _state = _state with
            {
                Selection = savedIntent.Selection,
                Preparing = savedIntent.Preparing,
                Error = savedIntent.Error,
                SelectionIsKnown = true
            };
        }
    }

    private static void PersistIntent(LocationIntent intent) =>
        intent.Draft?.RememberPlacementIntent(new(
            intent.Selection,
            intent.Dirty,
            intent.OutcomeUncertain,
            intent.Preparing,
            intent.Error));

    internal void NavigateTo(string threadId)
    {
        CancellationTokenSource previous;
        lock (_gate)
        {
            if (_disposed || _threadId == threadId) return;
            _generation++;
            _threadId = threadId;
            _epoch = null;
            _canEdit = _canChangeSessionOptions = _targetEmpty = _historyReady = _targetNew = false;
            _draftNeedsSelection = false;
            _materializationHistory = null;
            _intent = null;
            _state = new();
            previous = _scopeCancellation;
            _scopeCancellation = new();
        }
        CancelAndDispose(previous);
        Notify();
    }

    private void CancelStaleIntent()
    {
        if (_intent is not null)
        {
            _intent.Dirty = false;
            if (!_intent.OutcomeUncertain)
                _intent.Error = ChatPlacementError.NoLongerEmpty;
        }

        _state = _state with
        {
            Error = _intent?.Error
        };
        Notify();
    }

    internal void ReportDraftOptionsResult(string threadId, Exception? error)
    {
        if (error is not null)
            System.Diagnostics.Trace.WriteLine($"[chat] Draft options could not be applied: {error}");
        lock (_gate)
        {
            if (_disposed || _threadId != threadId) return;
            _state = _state with
            {
                Error = error is not null
                    ? ChatPlacementError.DraftOptionsFailed
                    : _state.GatewayDefaultOnly ? ChatPlacementError.GatewayDefaultOnly : _intent?.Error
            };
        }
        Notify();
    }

    internal void ReportDraftCreationResult(string threadId, ChatPlacementError? error)
    {
        lock (_gate)
        {
            if (_disposed || _threadId != threadId) return;
            _state = _state with
            {
                Error = error ?? (_state.GatewayDefaultOnly
                    ? ChatPlacementError.GatewayDefaultOnly
                    : _intent?.Error)
            };
        }
        Notify();
    }

    internal void ReportDraftSendStatus(string draftKey, string? message)
    {
        if (message is not null && string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Draft status message is required.", nameof(message));
        lock (_gate)
        {
            if (_disposed || _threadId != draftKey || _draft?.Key != draftKey) return;
            _state = _state with { DraftStatusMessage = message };
        }
        Notify();
    }

    internal bool IsCurrent(string threadId)
    {
        lock (_gate)
            return !_disposed && _threadId == threadId && _epoch.HasValue && gateway.ConnectionEpoch == _epoch;
    }

    internal static bool IsGitUrl(string value)
    {
        var text = value.Trim();
        return text.Length is > 0 and <= 2048 && !text.Any(char.IsWhiteSpace) &&
            (Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
                (uri.Scheme == "https" && uri.UserInfo.Length == 0 ||
                 uri.Scheme == "ssh" && uri.UserInfo == "git")
             || Regex.IsMatch(text, @"^git@[^/:\s]+:[^\s]+$", RegexOptions.CultureInvariant));
    }

    private void Fail(int generation, Exception error, Func<ChatPlacementState, ChatPlacementState> update)
    {
        System.Diagnostics.Trace.WriteLine($"[chat] Location request failed: {error}");
        Update(generation, state =>
        {
            var updated = update(state);
            if (state.GatewayDefaultOnly)
                return updated with { GatewayDefaultOnly = true, Error = ChatPlacementError.GatewayDefaultOnly };

            if (_draft is { UsesGatewayDefaultAgent: false } &&
                _draftNeedsSelection &&
                _intent is null)
            {
                return updated with
                {
                    GatewayDefaultOnly = true,
                    Error = ChatPlacementError.GatewayDefaultOnly
                };
            }

            return updated with
            {
                Error = _intent?.Error ?? (_canEdit ? ChatPlacementError.DiscoveryFailed : null)
            };
        });
    }

    private void Update(int generation, Func<ChatPlacementState, ChatPlacementState> update)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation || _epoch != gateway.ConnectionEpoch) return;
            _state = update(_state);
        }
        Notify();
    }

    private void Notify()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Revision++;
        }
        void Publish()
        {
            if (_disposed) return;
            Changed?.Invoke();
        }
        if (dispatcher.HasThreadAccess) Publish();
        else if (!dispatcher.TryEnqueue(Publish))
            System.Diagnostics.Trace.WriteLine("[chat] Location update could not reach the UI dispatcher.");
    }

    public void Dispose()
    {
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            Changed = null;
            cancellation = _scopeCancellation;
        }
        CancelAndDispose(cancellation);
    }

    private static void CancelAndDispose(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }
}
