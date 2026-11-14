// Session file list behind the chat Files pill and conversation-panel tab. The chat root
// binds the current session and its connection, reset and history identities on each render:
//   files.Bind(sessionKey, connectionGeneration, timelineGeneration, historyRevision, turnActive, provider.ListSessionFilesAsync);
// It reads the existing sessions.files.list contract once per source and again when a run
// ends or the user refreshes. Results from a replaced source are discarded.
using OpenClaw.Shared;
using OpenClawTray.Pages;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

internal enum ChatSessionFilesStatus { Idle, Loading, Loaded, Unsupported, Failed }

/// <summary>UI-thread-owned, read-only projection of one session's file list.</summary>
internal sealed class ChatSessionFilesState
{
    private static readonly WorkspaceFilesModel.WorkspaceListState Empty = new();
    private static readonly WorkspaceFilesModel.WorkspaceListState UnsupportedList = new() { Supported = false };
    private Func<string, Task<SessionFileList>>? _load;
    private string? _session;
    private long _connectionGeneration;
    private long _timelineGeneration;
    private long _historyRevision;
    private bool _turnActive;
    private long _request;
    private bool _reloadAfterCurrent;
    private bool _refreshAfterCurrent;
    private bool _binding;
    // An unsupported answer holds for the connection; only an explicit refresh asks again.
    private (long Connection, Func<string, Task<SessionFileList>> Load)? _unsupported;

    internal event Action? Changed;
    internal ChatSessionFilesStatus Status { get; private set; }
    internal WorkspaceFilesModel.WorkspaceListState Files { get; private set; } = Empty;

    /// <summary>
    /// Increases with every state change. Consumers pass it as a prop: a read that finishes inside
    /// <see cref="Bind"/> raises no <see cref="Changed"/>, so this is how its render sees new state.
    /// </summary>
    internal long Version { get; private set; }

    /// <summary>
    /// Files the session edited or read. The Gateway also returns a listing of the workspace root,
    /// whose untouched entries are not session files.
    /// </summary>
    internal IReadOnlyList<WorkspaceFilesModel.WorkspaceFileEntry> Rows =>
        Files.Entries.Where(entry => entry.IsSessionFile && !entry.IsDirectory).ToArray();

    internal int FileCount => Files.Entries.Count(entry => entry.IsSessionFile && !entry.IsDirectory);

    /// <summary>
    /// Called during render, so it never raises <see cref="Changed"/>; the caller renders the new state.
    /// A null session or loader clears the list and retires any read in flight.
    /// </summary>
    internal void Bind(string? session, long connectionGeneration, long timelineGeneration, long historyRevision,
        bool turnActive, Func<string, Task<SessionFileList>>? load)
    {
        // A reset starts a new timeline generation and a checkpoint restore a new history revision,
        // with the same key and connection.
        var sourceChanged = _session != session || _connectionGeneration != connectionGeneration ||
            _timelineGeneration != timelineGeneration || _historyRevision != historyRevision || !Equals(_load, load);
        var runEnded = _turnActive && !turnActive;
        _session = session;
        _connectionGeneration = connectionGeneration;
        _timelineGeneration = timelineGeneration;
        _historyRevision = historyRevision;
        _turnActive = turnActive;
        _load = load;
        var knownUnsupported = load is not null && _unsupported is { } unsupported &&
            unsupported.Connection == connectionGeneration && Equals(unsupported.Load, load);
        if (sourceChanged)
        {
            _request++;
            _reloadAfterCurrent = false;
            _refreshAfterCurrent = false;
            Status = knownUnsupported && session is not null ? ChatSessionFilesStatus.Unsupported : ChatSessionFilesStatus.Idle;
            Files = Status == ChatSessionFilesStatus.Unsupported ? UnsupportedList : Empty;
            Version++;
        }
        if ((sourceChanged || runEnded) && !knownUnsupported)
        {
            _binding = true;
            try { _ = LoadAsync(notify: false); }
            finally { _binding = false; }
        }
    }

    internal void Refresh() => _ = LoadAsync(notify: true);

    // A loader that completes synchronously finishes inside Bind, before this render reads the state.
    private void Notify()
    {
        Version++;
        if (!_binding) Changed?.Invoke();
    }

    private async Task LoadAsync(bool notify)
    {
        if (_session is not { } session || _load is not { } load) return;
        if (Status == ChatSessionFilesStatus.Loading)
        {
            // One read at a time; a later trigger gets one trailing snapshot.
            _reloadAfterCurrent = true;
            _refreshAfterCurrent |= notify;
            return;
        }
        var request = ++_request;
        var connection = _connectionGeneration;
        Status = ChatSessionFilesStatus.Loading;
        if (notify) Notify();
        else Version++;
        try
        {
            var result = await load(session);
            if (request != _request) return;
            Files = WorkspaceFilesModel.FromSessionFileList(result);
            Status = Files.Supported ? ChatSessionFilesStatus.Loaded : ChatSessionFilesStatus.Unsupported;
            _unsupported = Files.Supported ? null : (connection, load);
        }
        catch (Exception ex)
        {
            if (request != _request) return;
            // The previous list for this session stays visible beside the failure.
            Logger.Warn($"[ChatFiles] sessions.files.list failed ({ex.GetType().Name}).");
            Status = ChatSessionFilesStatus.Failed;
        }
        Notify();
        // After an unsupported answer only a queued explicit refresh asks again.
        var reload = _reloadAfterCurrent && (_refreshAfterCurrent || Status != ChatSessionFilesStatus.Unsupported);
        _reloadAfterCurrent = false;
        _refreshAfterCurrent = false;
        if (reload)
            await LoadAsync(notify: true);
    }
}
