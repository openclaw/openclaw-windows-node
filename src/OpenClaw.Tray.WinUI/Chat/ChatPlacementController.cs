using OpenClaw.Shared;
using OpenClawTray.Presentation;
using System.Text.RegularExpressions;

namespace OpenClawTray.Chat;

internal sealed record ChatPlacementState(
    ChatPlacementCatalog? Catalog = null,
    ChatPlacementSelection? Selection = null,
    bool Loading = false,
    bool Preparing = false,
    string? Error = null,
    bool Browsing = false,
    bool BrowsingLoading = false,
    ChatWorkspaceDirectory? Directory = null);

/// <summary>Host-local first-turn location intent. Opening or choosing a row never mutates the Gateway.</summary>
internal sealed class ChatPlacementController(IChatPlacementGateway gateway, IUiDispatcher dispatcher) : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource _scopeCancellation = new();
    private ChatPlacementState _state = new();
    private string? _threadId;
    private string _agentId = "main";
    private long? _epoch;
    private int _generation;
    private int _browseGeneration;
    private bool _canEdit;
    private bool _canChangeSessionOptions;
    private bool _targetEmpty;
    // Accepted Gateway mutations outlive navigation. Keep their intent and drain state with the session.
    private readonly Dictionary<(string ThreadId, string AgentId), LocationIntent> _intents = new();
    private LocationIntent? _intent;
    private bool _disposed;

    private sealed class LocationIntent(ChatPlacementSelection selection)
    {
        internal ChatPlacementSelection Selection = selection;
        internal bool Dirty = true;
        internal bool Preparing;
        internal bool OutcomeUncertain;
        internal string? Error;
    }

    internal event Action? Changed;
    internal long Revision { get; private set; }
    internal ChatPlacementState State { get { lock (_gate) return _state; } }
    internal bool CanEdit { get { lock (_gate) return !_disposed && _canEdit && !_state.Preparing; } }
    internal bool RequiresPreparation { get { lock (_gate) return _intent?.Dirty == true; } }

    internal void ApplyInputs(ChatComposerInputs inputs)
    {
        bool load;
        CancellationTokenSource? previous = null;
        lock (_gate)
        {
            if (_disposed) return;
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
                _state = new(Selection: _intent?.Selection, Preparing: _intent?.Preparing == true,
                    Error: _intent?.Error);
                previous = _scopeCancellation;
                _scopeCancellation = new();
            }
            _canEdit = inputs.ShowPlacementPickers && inputs.CanChangeSessionOptions;
            var targetEmpty = inputs.PlacementTargetIsEmpty;
            if (_targetEmpty && _canChangeSessionOptions &&
                (!targetEmpty || !inputs.CanChangeSessionOptions) && _state.Preparing && previous is null)
            {
                previous = _scopeCancellation;
                _scopeCancellation = new();
            }
            _targetEmpty = targetEmpty;
            _canChangeSessionOptions = inputs.CanChangeSessionOptions;
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
            _state = _state with { Loading = true, Error = null };
        }
        Notify();
        try
        {
            var catalog = await gateway.LoadAsync(agent, token).ConfigureAwait(false);
            Update(generation, state =>
            {
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
                return state with { Catalog = catalog, Selection = selected, Loading = false, Error = _intent?.Error };
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
                Error = null
            };
        }
        Notify();
    }

    private void SetSelection(ChatPlacementSelection selection, bool changed)
    {
        if (changed)
        {
            _intent ??= new(selection);
            _intent.Selection = selection;
            _intent.Dirty = true;
            _intent.Error = null;
            _intents[(_threadId!, _agentId)] = _intent;
        }
        _state = _state with { Selection = selection, Error = _intent?.Error };
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
            _state = _state with { Browsing = true, BrowsingLoading = true, Error = null };
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
                ? state with { BrowsingLoading = false, Error = ex.Message }
                : state);
        }
    }

    internal void CloseBrowser()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _browseGeneration++;
            _state = _state with { Browsing = false, BrowsingLoading = false, Error = null };
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
        lock (_gate)
        {
            if (_intent?.Preparing == true)
            {
                _state = _state with { Error = "Location preparation is still finishing. Wait before sending again." };
                Notify();
                return false;
            }
            if (_intent is not { Dirty: true } currentIntent) return true;
            if (!IsCurrent(threadId)) return false;
            if (!_targetEmpty)
            {
                CancelStaleIntent();
                return false;
            }
            if (_threadId != threadId || !_canChangeSessionOptions || _state.Loading ||
                _epoch is not { } currentEpoch || gateway.ConnectionEpoch != currentEpoch ||
                _state.Selection is not { } selected || !selected.Environment.IsSelectable)
            {
                _state = _state with { Error = "The selected chat location is not ready. Refresh the choices before sending." };
                Notify();
                return false;
            }
            generation = _generation;
            agent = _agentId;
            epoch = currentEpoch;
            selection = selected;
            intent = currentIntent;
            intent.Selection = selection;
            intent.Preparing = true;
            intent.Error = null;
            operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _scopeCancellation.Token);
            _state = _state with { Preparing = true, Error = null };
        }
        Notify();
        try
        {
            await gateway.PrepareAsync(threadId, agent, selection, epoch, operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_disposed || generation != _generation || gateway.ConnectionEpoch != epoch)
                {
                    InterruptIntent(intent);
                    return false;
                }
                if (!_targetEmpty)
                {
                    InterruptIntent(intent);
                    return false;
                }
                intent.Dirty = false;
                intent.OutcomeUncertain = false;
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
                InterruptIntent(intent);
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[chat] Location preparation failed: {ex}");
            lock (_gate) intent.Error = ex.Message;
            return false;
        }
        finally
        {
            operation.Dispose();
            lock (_gate)
            {
                intent.Preparing = false;
                if (ReferenceEquals(_intent, intent))
                    _state = _state with { Preparing = false, Error = intent.Error };
            }
            Notify();
        }
    }

    private void InterruptIntent(LocationIntent intent)
    {
        intent.OutcomeUncertain = true;
        intent.Error = "Location preparation was interrupted and may have changed this session's location. Review it in the Gateway before you send again.";
        if (ReferenceEquals(_intent, intent) && _epoch.HasValue && !_targetEmpty)
            intent.Dirty = false;
    }

    internal void NavigateTo(string threadId)
    {
        CancellationTokenSource previous;
        lock (_gate)
        {
            if (_disposed || _threadId == threadId) return;
            _generation++;
            _threadId = threadId;
            _epoch = null;
            _canEdit = _canChangeSessionOptions = _targetEmpty = false;
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
                _intent.Error = "This session is no longer empty, so the pending location selection was cancelled. Review the session and send again to continue in its current location.";
        }
        _state = _state with
        {
            Error = _intent?.Error
        };
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
        Update(generation, state => update(state) with { Error = error.Message });
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
            if (!_disposed) Changed?.Invoke();
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
