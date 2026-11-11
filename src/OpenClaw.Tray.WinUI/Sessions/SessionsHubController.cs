using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClaw.Shared.Sessions;
using OpenClawTray.Dialogs;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace OpenClawTray.Sessions;

/// <summary>Page services the Sessions hub needs from its WinUI host.</summary>
internal interface ISessionsHubHost
{
    XamlRoot? XamlRoot { get; }
    IntPtr WindowHandle { get; }
    IOperatorGatewayClient? Client { get; }
    IReadOnlyList<SessionInfo> TrackedSessions { get; }
    string? GatewayName { get; }
    JsonElement? AgentsList { get; }
    ElementTheme ActualTheme { get; }
    bool ShowCompletedSessions { get; set; }
    void OpenChat(string sessionKey);
    void OpenSessionDashboard(string sessionKey);
    void OpenConnection();
    void OpenDashboard();
}

/// <summary>
/// Owns the Sessions page state and every Gateway request it makes. Runs on the
/// UI thread: public methods are called from Reactor event handlers and awaited
/// continuations resume on the page's dispatcher. Background client events are
/// marshalled through <c>post</c>. Shared session-menu actions are delegated to
/// <see cref="WorkspaceSessionMenuController"/> so both surfaces behave alike.
/// </summary>
internal sealed class SessionsHubController
{
    private const int SearchDebounceMs = 200;
    private const int ChangeDebounceMs = 300;
    private const int GcPollIntervalMs = 1000;
    private const int GcMaxPolls = 60;

    private readonly ISessionsHubHost _host;
    private readonly Action<Action> _post;
    private readonly WorkspaceSessionMenuController _menu;
    private SessionsHubState _state = new();
    private IOperatorGatewayClient? _subscribed;
    private int _loadGeneration;
    private int _searchGeneration;
    private int _changeGeneration;
    private int _transcriptGeneration;
    private int _worktreeGeneration;
    private int _branchGeneration;
    private int _noticeGeneration;
    private int _profileGeneration;
    // The client and authenticated connection the page currently shows; null while disconnected.
    // Actions capture it before any prompt, and every later step runs only while it is still
    // current, so nothing confirmed on one connection runs on another.
    private WorkspaceSessionOperation? _binding;
    private string? _liveSignature;
    private bool _profilesLoaded;
    private bool _snapshotOnly;

    public SessionsHubController(ISessionsHubHost host, Action<Action> post)
    {
        _host = host;
        _post = post;
        _menu = new WorkspaceSessionMenuController(
            () => _host.Client,
            key => _state.Rows.FirstOrDefault(row => row.Key == key) ?? _host.TrackedSessions.FirstOrDefault(row => row.Key == key),
            () => _host.TrackedSessions,
            () => _host.XamlRoot,
            () => _host.WindowHandle,
            ForkAsync,
            key => CloseDetails(key),
            (message, severity) => ShowNotice("", message, severity),
            (_, ex) => ShowNotice(T("ActionFailed"), ex.Message, InfoBarSeverity.Error),
            () => _state.CanMutateRows,
            NotifyRowsUnavailable);
    }

    public SessionsHubState State => _state;
    public bool CanMutateRows => _state.CanMutateRows;
    public ElementTheme ActualTheme => _host.ActualTheme;
    public event Action? Changed;
    /// <summary>Requests view-owned page/group reveal and focus after response-confirmed archive Undo.</summary>
    public event Action<string>? ArchiveRestored;

    internal static string T(string key) => LocalizationHelper.GetString("SessionsHub_" + key);
    internal static string F(string key, params object?[] args) => LocalizationHelper.Format("SessionsHub_" + key, args);

    private void Update(Func<SessionsHubState, SessionsHubState> change)
    {
        _state = change(_state);
        Changed?.Invoke();
    }

    // ── Lifetime and connection ──

    /// <summary>Called on every navigation. An agent route scopes the roster; the plain route keeps the user's choice.</summary>
    public void Activate(string? routeAgentId)
    {
        Update(s => s with
        {
            Agents = ReadAgents(_host.AgentsList),
        });
        SetFilters(_state.Filters with
        {
            AgentId = routeAgentId ?? _state.Filters.AgentId,
            ShowCompleted = _host.ShowCompletedSessions,
        }, reload: false);
        RefreshConnection();
    }

    public void Deactivate()
    {
        CloseDialog(false);
        Unsubscribe();
        _loadGeneration++;
        Update(s => s with { IsReloading = false, IsBusy = false, RowReveal = null });
    }

    public void RefreshAgents() => Update(s => s with { Agents = ReadAgents(_host.AgentsList) });

    /// <summary>
    /// Test-only: renders a fixed read-only roster without a Gateway so the
    /// accessibility suite can scan populated rows. Never used by a normal launch.
    /// </summary>
    public void UseSnapshot(IReadOnlyList<SessionInfo> rows)
    {
        _snapshotOnly = true;
        Update(s => s with
        {
            IsConnected = true,
            Permissions = new SessionsHubPermissions(true, ["operator.read"], ["sessions.list"]),
            Load = SessionsHubLoad.Loaded,
            Rows = rows,
            LastLoadedAtUtc = DateTime.UtcNow,
            TrackedRows = rows,
        });
    }

    /// <summary>Re-evaluates connection and permissions, then reloads what the page shows.</summary>
    public void RefreshConnection()
    {
        if (_snapshotOnly)
            return;
        var client = _host.Client;
        var connected = client is { IsConnectedToGateway: true };
        if (!ReferenceEquals(client, _subscribed))
        {
            Unsubscribe();
            if (client is not null)
            {
                client.SessionsUpdated += OnSessionsUpdated;
                client.SessionListChanged += OnSessionListChanged;
                _subscribed = client;
                _liveSignature = SessionsHubProjection.LiveSignature(_host.TrackedSessions);
            }
        }
        // A new client, a disconnect, or a reconnect of the same client (even while the page
        // was hidden) all end the current binding.
        if (_binding is { IsCurrent: true } ? false : _binding is not null || connected)
            StartConnectionEpoch(client, connected);
        var permissions = connected
            ? new SessionsHubPermissions(true, client!.GrantedOperatorScopes, client.AdvertisedServerMethods)
            : SessionsHubPermissions.Disconnected;
        Update(s => s with
        {
            IsConnected = connected,
            Permissions = permissions,
            GatewayName = _host.GatewayName,
            Load = connected ? s.Load : SessionsHubLoad.Idle,
            Rows = connected ? s.Rows : Array.Empty<SessionInfo>(),
            TrackedRows = connected ? _host.TrackedSessions : Array.Empty<SessionInfo>(),
            Selection = connected ? s.Selection : SessionsHubSelection.Empty,
            Transcript = connected ? s.Transcript : s.Transcript with { IsSearching = false, Result = null, Error = null },
            Worktrees = connected ? s.Worktrees : new WorktreesState(),
            IsBusy = connected && s.IsBusy,
        });
        if (!connected)
            return;
        _ = ReloadAsync();
        if (_state.Tab == SessionsHubTab.Worktrees)
            _ = LoadWorktreesAsync();
    }

    private void StartConnectionEpoch(IOperatorGatewayClient? client, bool connected)
    {
        _binding = connected ? new WorkspaceSessionOperation(client!, () => _host.Client) : null;
        _loadGeneration++;
        _transcriptGeneration++;
        _worktreeGeneration++;
        _profileGeneration++;
        _profilesLoaded = false;
        _liveSignature = null;
        // Pending prompts resolve as cancelled so a confirmed action never runs on the new connection.
        CloseDialog(false);
        Update(s => s with
        {
            Notice = null,
            // Rows from the old connection must not stay actionable on the new one.
            Load = connected ? SessionsHubLoad.Loading : SessionsHubLoad.Idle,
            LoadError = null,
            Rows = Array.Empty<SessionInfo>(),
            LastLoadedAtUtc = connected ? null : s.LastLoadedAtUtc,
            TrackedRows = Array.Empty<SessionInfo>(),
            HasMore = false,
            NextOffset = null,
            TotalCount = null,
            DetailsKey = null,
            ExpandedKeys = new HashSet<string>(StringComparer.Ordinal),
            RowReveal = null,
            LabelDraft = (null, "", null),
            IsBusy = false,
            IsReloading = false,
            GroupCatalog = Array.Empty<string>(),
            GroupsSupported = false,
            Profiles = Array.Empty<GatewayUserProfile>(),
            SelfProfileId = null,
            Selection = SessionsHubSelection.Empty,
            Transcript = s.Transcript with { IsSearching = false, Result = null, Error = null },
            Worktrees = new WorktreesState(),
        });
    }

    private void Unsubscribe()
    {
        if (_subscribed is not null)
        {
            _subscribed.SessionsUpdated -= OnSessionsUpdated;
            _subscribed.SessionListChanged -= OnSessionListChanged;
        }
        _subscribed = null;
    }

    private void OnSessionsUpdated(object? sender, SessionInfo[] _)
    {
        var binding = _binding;
        _post(() =>
        {
            // Tracked updates also fire for activity ticks; reload only when something shown changed.
            if (!ReferenceEquals(sender, _subscribed) || !IsCurrent(binding)) return;
            var sessions = _host.TrackedSessions;
            var signature = SessionsHubProjection.LiveSignature(sessions);
            if (signature == _liveSignature) return;
            _liveSignature = signature;
            Update(s => s with { TrackedRows = sessions.ToArray() });
            ScheduleChangeReload();
        });
    }

    /// <summary>The Gateway reported a real change, which may affect archived rows the tracked list omits.</summary>
    private void OnSessionListChanged(object? sender, SessionListChange change) => _post(() =>
    {
        if (!ReferenceEquals(sender, _subscribed)) return;
        // Run start and end pushes would otherwise reload the Active view on every run. For tracked
        // sessions their status arrives through the tracked-list signature. Archived and All always reload.
        if (_state.Filters.Status == SessionArchiveFilter.Active
            && SessionsHubProjection.IsTrackedRunChange(change, _host.TrackedSessions))
            return;
        ScheduleChangeReload();
    });

    private void ScheduleChangeReload()
    {
        var generation = ++_changeGeneration;
        _ = DebounceAsync(ChangeDebounceMs, () => generation == _changeGeneration, () => ReloadAsync(preserveCount: true));
    }

    /// <summary>True while <paramref name="binding"/> is the page's live, authenticated connection.</summary>
    private bool IsCurrent(WorkspaceSessionOperation? binding) => binding is { IsCurrent: true } && ReferenceEquals(binding, _binding);

    private static async Task DebounceAsync(int delayMs, Func<bool> stillCurrent, Func<Task> work)
    {
        await Task.Delay(delayMs);
        if (stillCurrent())
            await work();
    }

    private static IReadOnlyList<SessionsHubAgent> ReadAgents(JsonElement? agentsList)
    {
        if (agentsList is not { ValueKind: JsonValueKind.Object } root
            || !root.TryGetProperty("agents", out var agents) || agents.ValueKind != JsonValueKind.Array)
            return Array.Empty<SessionsHubAgent>();
        return agents.EnumerateArray()
            .Where(agent => agent.ValueKind == JsonValueKind.Object)
            .Select(agent =>
            {
                var id = Read(agent, "id");
                var hasIdentity = agent.TryGetProperty("identity", out var identity) && identity.ValueKind == JsonValueKind.Object;
                var name = hasIdentity ? Read(identity, "name") : null;
                var emoji = hasIdentity ? Read(identity, "emoji") : null;
                return id is null ? null : new SessionsHubAgent(id, name ?? Read(agent, "name") ?? id, emoji);
            })
            .OfType<SessionsHubAgent>()
            .DistinctBy(agent => agent.Id)
            .ToArray();
        static string? Read(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
    }

    // ── Roster loading ──

    public async Task ReloadAsync(bool preserveCount = false)
    {
        var client = _host.Client;
        if (client is not { IsConnectedToGateway: true })
            return;
        var generation = ++_loadGeneration;
        var configured = _state.Filters.ToQuery().Limit;
        var limit = preserveCount ? Math.Max(configured, _state.Rows.Count) : configured;
        Update(s => s with
        {
            Load = s.Load == SessionsHubLoad.Loaded ? SessionsHubLoad.Loaded : SessionsHubLoad.Loading,
            LoadError = null,
            IsReloading = true,
            TrackedRows = _host.TrackedSessions,
        });
        try
        {
            var page = await client.ListSessionsPageAsync(_state.Filters.ToQuery(limitOverride: limit));
            if (generation != _loadGeneration) return;
            if (!page.IsSupported)
            {
                Update(s => s with { Load = SessionsHubLoad.Unsupported, Rows = Array.Empty<SessionInfo>(), IsReloading = false });
                return;
            }
            ApplyPage(page, append: false);
            Update(s => s with { IsReloading = false });
            await LoadCatalogsAsync(client, generation);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or NotSupportedException or OperationCanceledException)
        {
            if (generation != _loadGeneration) return;
            Update(s => s with { Load = SessionsHubLoad.Failed, LoadError = ex.Message });
        }
        finally
        {
            if (generation == _loadGeneration && _state.IsReloading)
                Update(s => s with { IsReloading = false });
        }
    }

    public async Task LoadMoreAsync()
    {
        var client = _host.Client;
        if (client is not { IsConnectedToGateway: true } || _state.IsReloading || _state.IsBusy
            || !_state.HasMore || _state.NextOffset is not { } offset)
            return;
        // Never bump the generation here: a reload that starts while this page is in
        // flight must win, and this page is dropped if its filters are now stale.
        var generation = _loadGeneration;
        var binding = _binding;
        Update(s => s with { IsBusy = true });
        try
        {
            var page = await client.ListSessionsPageAsync(_state.Filters.ToQuery(offset));
            if (generation == _loadGeneration)
                ApplyPage(page, append: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            if (generation == _loadGeneration)
                ShowNotice(T("LoadFailedTitle"), ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (ReferenceEquals(binding, _binding))
                Update(s => s with { IsBusy = false });
        }
    }

    private void ApplyPage(SessionListPage page, bool append)
    {
        Update(s =>
        {
            var rows = append
                ? s.Rows.Concat(page.Sessions.Where(row => s.Rows.All(existing => existing.Key != row.Key))).ToArray()
                : page.Sessions;
            var draft = s.LabelDraft;
            if (draft.Key is { } key && rows.FirstOrDefault(row => row.Key == key) is { } current
                && (string.IsNullOrWhiteSpace(draft.SessionId) || draft.SessionId != current.SessionId))
                draft = (null, "", null);
            return s with
            {
                Load = SessionsHubLoad.Loaded,
                Rows = rows,
                LastLoadedAtUtc = DateTime.UtcNow,
                HasMore = page.HasMore,
                NextOffset = page.NextOffset,
                TotalCount = page.TotalCount,
                LabelDraft = draft,
                Selection = s.Selection.Reconcile(rows.Select(row => row.Key)),
            };
        });
    }

    private async Task LoadCatalogsAsync(IOperatorGatewayClient client, int generation)
    {
        var permissions = _state.Permissions;
        var profileGeneration = _profileGeneration;
        if (permissions.Supports("sessions.groups.list"))
        {
            try
            {
                var catalog = await client.ListSessionGroupsAsync();
                if (generation == _loadGeneration && IsCurrent(_binding))
                    Update(s => s with { GroupCatalog = catalog.Names, GroupsSupported = catalog.IsSupported });
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                new AppLogger().Warn($"[SessionsHub] Group catalog unavailable ({ex.GetType().Name}).");
            }
        }
        if (generation != _loadGeneration || !ReferenceEquals(_host.Client, client)
            || !IsCurrent(_binding) || profileGeneration != _profileGeneration)
            return;
        if (!_profilesLoaded)
            await LoadOwnerCatalogAsync(client, profileGeneration);
    }

    /// <summary>Reloads assignable owners after the authenticated Gateway identity changes.</summary>
    public Task RefreshOwnerCatalogAsync()
    {
        if (_snapshotOnly || _host.Client is not { IsConnectedToGateway: true } client || !IsCurrent(_binding))
            return Task.CompletedTask;

        var generation = ++_profileGeneration;
        _profilesLoaded = false;
        Update(s => s with { Profiles = Array.Empty<GatewayUserProfile>(), SelfProfileId = null });
        return LoadOwnerCatalogAsync(client, generation);
    }

    private async Task LoadOwnerCatalogAsync(IOperatorGatewayClient client, int generation)
    {
        if (!IsOwnerCatalogRequestCurrent(client, generation))
            return;
        var permissions = _state.Permissions;
        if (!permissions.CanWrite || !permissions.Supports("users.list"))
            return;

        _profilesLoaded = true;
        try
        {
            var profiles = await client.ListUserProfilesAsync();
            var self = permissions.Supports("users.self") ? await client.GetSelfProfileIdAsync() : null;
            if (!IsOwnerCatalogRequestCurrent(client, generation))
                return;
            Update(s => s with { Profiles = profiles, SelfProfileId = self });
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or NotSupportedException)
        {
            if (generation == _profileGeneration)
                _profilesLoaded = false;
            new AppLogger().Warn($"[SessionsHub] Owner roster unavailable ({ex.GetType().Name}).");
        }
    }

    private bool IsOwnerCatalogRequestCurrent(IOperatorGatewayClient client, int generation) =>
        generation == _profileGeneration && ReferenceEquals(_host.Client, client) && IsCurrent(_binding);

    public int OwnerCatalogGeneration => _profileGeneration;

    // ── View state ──

    public void SetTab(SessionsHubTab tab)
    {
        if (tab == _state.Tab) return;
        Update(s => s with { Tab = tab, RowReveal = null });
        if (tab == SessionsHubTab.Worktrees && _state.Worktrees.Load is SessionsHubLoad.Idle or SessionsHubLoad.Failed)
            _ = LoadWorktreesAsync();
    }

    public void OpenWorktree(string id)
    {
        var needsReload = _state.Worktrees.Load != SessionsHubLoad.Loaded
            || _state.Worktrees.Rows is null
            || _state.Worktrees.Rows.All(record => record.Id != id);
        Update(s => s with
        {
            Tab = SessionsHubTab.Worktrees,
            Worktrees = s.Worktrees with { DetailsId = id },
        });
        if (needsReload)
            _ = LoadWorktreesAsync();
    }

    public void SetWorktreeDetails(string? id) =>
        Update(s => s with { Worktrees = s.Worktrees with { DetailsId = id } });

    /// <param name="persist">False for a temporary reveal (transcript Show details) that must not change the saved preference.</param>
    public void SetFilters(SessionsHubFilters filters, bool reload = true, bool persist = true)
    {
        var previous = _state.Filters;
        Update(s => s with { Filters = filters, PageIndex = 0, RowReveal = null });
        if (persist && filters.ShowCompleted != previous.ShowCompleted)
            _host.ShowCompletedSessions = filters.ShowCompleted;
        if (!previous.ToQuery().ToSearchScope().SequenceEqual(filters.ToQuery().ToSearchScope()))
            InvalidateTranscriptSearch();
        var queryChanged = !SameServerQuery(previous, filters);
        if (queryChanged)
            ResetRosterForQueryChange();
        // Client-only filters hide rows without a reload; never keep hidden rows selected.
        Update(s => s with { Selection = s.Selection.Reconcile(filters.ApplyClientFilters(s.Rows).Select(row => row.Key)) });
        if (!reload || !queryChanged)
            return;
        _ = ReloadAsync();
    }

    public void SetSearch(string text)
    {
        if (text == _state.Filters.Search) return;
        if (text.Trim() == _state.Filters.Search.Trim())
        {
            Update(s => s with { Filters = s.Filters with { Search = text } });
            return;
        }
        Update(s => s with { Filters = s.Filters with { Search = text }, PageIndex = 0 });
        InvalidateTranscriptSearch();
        ResetRosterForQueryChange();
        var generation = ++_searchGeneration;
        _ = DebounceAsync(SearchDebounceMs, () => generation == _searchGeneration, () => ReloadAsync());
    }

    private void ResetRosterForQueryChange()
    {
        // Invalidate an old response immediately, including during the search debounce.
        _loadGeneration++;
        _searchGeneration++;
        Update(s => s with
        {
            Load = SessionsHubLoad.Idle,
            LoadError = null,
            IsReloading = false,
            Rows = Array.Empty<SessionInfo>(),
            RowReveal = null,
            HasMore = false,
            NextOffset = null,
            TotalCount = null,
            // Keep the draft while its row is filtered out. ApplyPage checks identity before reuse.
            Selection = SessionsHubSelection.Empty,
        });
    }

    private static bool SameServerQuery(SessionsHubFilters a, SessionsHubFilters b) => a.ToQuery() == b.ToQuery();

    public void ClearFilters() => SetFilters(_state.Filters.Cleared());
    public void SetGroupBy(SessionsGroupBy groupBy) => Update(s => s with { GroupBy = groupBy, PageIndex = 0, RowReveal = null });
    public void SetSort(SessionsSortColumn column, bool descending) => Update(s => s with { SortColumn = column, SortDescending = descending, RowReveal = null });
    public void SetPageSize(int size) => Update(s => s with { PageSize = size, PageIndex = 0, RowReveal = null });
    public void SetPage(int index) => Update(s => s with { PageIndex = Math.Max(0, index), RowReveal = null });
    /// <summary>Expands <paramref name="key"/> in the roster without toggling an already open row.</summary>
    public void OpenDetails(string key) => Update(s => s with
    {
        DetailsKey = key,
        ExpandedKeys = new HashSet<string>(s.ExpandedKeys, StringComparer.Ordinal) { key },
    });

    public void RequestRowReveal(string key, bool focus) =>
        Update(s => s with { RowReveal = new SessionsHubRowReveal(key, focus) });

    public void CompleteRowReveal(SessionsHubRowReveal request)
    {
        if (ReferenceEquals(_state.RowReveal, request))
            Update(s => s with { RowReveal = null });
    }

    /// <summary>Loads an attention card's session when the inventory currently filters it out.</summary>
    public async Task<bool> LoadAttentionRowAsync(string key)
    {
        if (_state.Rows.Any(row => row.Key == key))
            return true;
        if (PromptBinding() is not { } binding)
            return false;

        SetFilters(_state.Filters.Cleared() with
        {
            AgentId = null,
            Search = key,
            Status = SessionArchiveFilter.All,
            IncludeUnknown = true,
        }, reload: false, persist: false);
        SetTranscriptOpen(false);
        var generation = _loadGeneration + 1;
        await ReloadAsync();
        if (!IsCurrent(binding) || generation != _loadGeneration || _state.Load != SessionsHubLoad.Loaded)
            return false;

        // Search matches keys and titles by substring, so a small limit can put the exact key on a later page.
        while (_state.Rows.All(row => row.Key != key) && _state.HasMore && _state.NextOffset is { } offset)
        {
            var count = _state.Rows.Count;
            var notice = _state.Notice;
            await LoadMoreAsync();
            if (!IsCurrent(binding) || generation != _loadGeneration)
                return false;
            if (_state.Rows.Count == count || _state.NextOffset is not { } nextOffset || nextOffset <= offset)
            {
                if (_state.Notice is not null && !ReferenceEquals(_state.Notice, notice))
                    return false;
                break;
            }
        }
        if (_state.Rows.Any(row => row.Key == key))
            return true;

        ShowNotice(T("LoadFailedTitle"), T("SessionUnavailable"), InfoBarSeverity.Warning);
        return false;
    }

    public void CloseDetails(string? key = null) => Update(s =>
    {
        var expanded = new HashSet<string>(s.ExpandedKeys, StringComparer.Ordinal);
        if (key is null) expanded.Clear();
        else expanded.Remove(key);
        return s with
        {
            ExpandedKeys = expanded,
            DetailsKey = key is null || s.DetailsKey == key ? null : s.DetailsKey,
            LabelDraft = key is null || s.LabelDraft.Key == key ? (null, "", null) : s.LabelDraft,
        };
    });
    public void SetLabelDraft(string key, string text)
    {
        if (_state.ExpandedKeys.Contains(key))
            Update(s => s with { LabelDraft = (key, text, s.Rows.FirstOrDefault(row => row.Key == key)?.SessionId) });
    }
    public void DismissNotice(SessionsHubNotice? expected = null)
    {
        if (expected is null || ReferenceEquals(_state.Notice, expected))
            Update(s => s with { Notice = null });
    }

    public void SetSelectionMode(bool enabled) => Update(s => s with
    {
        SelectionMode = enabled,
        Selection = enabled ? s.Selection : SessionsHubSelection.Empty,
    });

    public void ToggleSelected(string key) => Update(s => s with { Selection = s.Selection.Toggle(key) });
    public void SetSelected(IEnumerable<string> keys, bool selected) => Update(s => s with { Selection = s.Selection.Set(keys, selected) });

    private void ShowNotice(string title, string message, InfoBarSeverity severity, string? actionLabel = null, Action? action = null,
        bool persistent = false)
    {
        var notice = new SessionsHubNotice(title, message, severity, actionLabel, action);
        var generation = ++_noticeGeneration;
        Update(s => s with { Notice = notice });
        if (!persistent)
            _ = DismissNoticeAfterAsync(generation, actionLabel is null ? 4000 : 7000);
    }

    private async Task DismissNoticeAfterAsync(int generation, int delayMs)
    {
        await Task.Delay(delayMs);
        if (generation == _noticeGeneration && _state.Notice is not null)
            Update(s => s with { Notice = null });
    }

    // ── Session actions ──

    public void OpenInChat(string key) => _host.OpenChat(key);

    public void OpenSessionDashboard(string key) => _host.OpenSessionDashboard(key);

    public void OpenConnection() => _host.OpenConnection();

    public void OpenDashboard() => _host.OpenDashboard();

    public void SearchTranscriptsForCurrentQuery()
    {
        var query = _state.Filters.Search.Trim();
        if (query.Length == 0)
            return;
        Update(s => s with
        {
            Transcript = s.Transcript with { Query = query, IsOpen = true, Result = null, Error = null },
        });
        _ = RunTranscriptSearchAsync();
    }

    public SessionMainState MainState(SessionInfo row) => SessionActionPlanner.ResolveMainState(
        row.Key, rowIsMain: row.IsMain, mainSessionKey: _host.Client?.MainSessionKey, sessions: _host.TrackedSessions.ToArray());

    public bool IsMain(SessionInfo row) => MainState(row) != SessionMainState.NotMain;

    public static WorkspaceSession ToWorkspaceSession(SessionInfo row, string title) => new(
        row.Key, title, SessionsHubProjection.AgentId(row), row.Pinned, row.Unread, row.Archived, SessionRunState.IsWorking(row));

    /// <summary>Pin, rename, read state, fork, copy, reset, compact, and export use the shared Workspace menu runner.</summary>
    public async Task RunSharedActionAsync(WorkspaceSessionMenuAction action, SessionInfo row, string title)
    {
        if (IsRowMutation(action) && !RequireCurrentRoster())
            return;
        if (action == WorkspaceSessionMenuAction.Reset)
        {
            await ResetAsync(row, title);
            return;
        }
        if (action == WorkspaceSessionMenuAction.ExportTranscript)
        {
            await ExportTranscriptAsync(row);
            return;
        }
        await _menu.ExecuteAsync(action, ToWorkspaceSession(row, title), row);
        if (action is not (WorkspaceSessionMenuAction.CopyKey or WorkspaceSessionMenuAction.CopySessionId
            or WorkspaceSessionMenuAction.CopyMarkdown or WorkspaceSessionMenuAction.ExportTranscript))
            await ReloadAsync(preserveCount: true);
    }

    private async Task ForkAsync(SessionCreateRequest request)
    {
        if (!RequireCurrentRoster() || PromptBinding() is not { } binding)
            return;
        var parent = _state.Rows.FirstOrDefault(row => row.Key == request.ParentSessionKey);
        var parentTitle = parent is null
            ? request.ParentSessionKey ?? T("UntitledSession")
            : SessionsHubPresentation.Titles([parent])[parent.Key];
        var name = await PromptAsync(T("ForkDialogTitle"), T("ForkDialogBody"), WorkspaceWindow.Text("SessionMenu_Fork"),
            F("ForkDefaultName", parentTitle), T("LabelPlaceholder"), FluentIconCatalog.Fork,
            T("Label"),
            parent is null ? null :
            [
                new SessionsHubDialogFact(T("FactAgent"),
                    SessionsHubProjection.AgentId(parent) is { } agentId
                        ? _state.Agents.FirstOrDefault(agent => agent.Id == agentId)?.Name ?? agentId
                        : T("NotAvailable")),
            ]);
        if (name is null)
            return;
        if (!RequireCurrentRoster())
            return;
        RequireBound(binding);
        var result = await binding.Client.CreateSessionAsync(request);
        RequireBound(binding);
        if (!result.IsSupported)
            ShowNotice("", WorkspaceWindow.Text("ForkUnsupported"), InfoBarSeverity.Informational);
        else if (!result.Ok || string.IsNullOrWhiteSpace(result.Key))
            ShowNotice(WorkspaceWindow.Text("ForkFailed"), result.Error ?? "", InfoBarSeverity.Error);
        else
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                try { await binding.PatchAsync(result.Key, WorkspaceSessionMenu.RenamePatch(name)); }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or NotSupportedException)
                {
                    ShowNotice(T("ForkCreatedTitle"), F("ForkRenameFailed", ex.Message), InfoBarSeverity.Warning,
                        T("OpenInChat"), () => _host.OpenChat(result.Key));
                    return;
                }
            }
            ShowNotice("", F("ForkCreated", name), InfoBarSeverity.Success,
                T("OpenInChat"), () => _host.OpenChat(result.Key));
        }
    }

    private async Task ResetAsync(SessionInfo row, string title)
    {
        if (PromptBinding() is not { } binding)
            return;
        if (!await ConfirmAsync(F("ResetSessionTitle", title),
                T("ResetSessionBody"),
                LocalizationHelper.GetString("SessionActionPrompt_Reset_ConfirmLabel"), destructive: true,
                icon: FluentIconCatalog.Reset,
                facts:
                [
                    new SessionsHubDialogFact(T("WorktreeOwnerSession"), title),
                    new SessionsHubDialogFact(T("FactAgent"),
                        SessionsHubProjection.AgentId(row) is { } agentId
                            ? _state.Agents.FirstOrDefault(agent => agent.Id == agentId)?.Name ?? agentId
                            : T("NotAvailable")),
                ]))
            return;
        var reset = await GuardedAsync(T("ActionFailed"), async operation =>
        {
            if (!await operation.Client.ResetSessionAsync(row.Key))
                throw new InvalidOperationException(T("ResetRejected"));
        }, binding, requireCurrentRoster: true);
        if (reset)
        {
            ShowNotice("", F("ResetDone", title), InfoBarSeverity.Success);
            await ReloadAsync(preserveCount: true);
        }
    }

    private async Task ExportTranscriptAsync(SessionInfo row)
    {
        var client = _host.Client;
        if (client is not { IsConnectedToGateway: true } || _host.WindowHandle == IntPtr.Zero)
        {
            ShowNotice("", WorkspaceWindow.Text("ExportEmpty"), InfoBarSeverity.Informational);
            return;
        }
        ChatHistoryInfo history;
        try
        {
            history = await client.RequestChatHistoryAsync(row.Key);
        }
        catch (NotSupportedException)
        {
            ShowNotice("", WorkspaceWindow.Text("HistoryUnsupported"), InfoBarSeverity.Informational);
            return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            ShowNotice(T("ActionFailed"), ex.Message, InfoBarSeverity.Error);
            return;
        }
        if (history.Messages.Count == 0)
        {
            ShowNotice("", WorkspaceWindow.Text("ExportEmpty"), InfoBarSeverity.Informational);
            return;
        }
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.Desktop,
                SuggestedFileName = Path.GetFileNameWithoutExtension(SessionTranscriptFormatter.SuggestFileName(row.Key)),
            };
            picker.FileTypeChoices.Add("Text file", [".txt"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _host.WindowHandle);
            var file = await picker.PickSaveFileAsync();
            if (file is null)
                return;
            await FileIO.WriteTextAsync(file, SessionTranscriptFormatter.Format(history));
            var folder = Path.GetDirectoryName(file.Path);
            ShowNotice("", LocalizationHelper.Format("WorkspaceShell_ExportDone", history.Messages.Count, file.Name),
                InfoBarSeverity.Success,
                string.IsNullOrWhiteSpace(folder) ? null : T("OpenFolder"),
                string.IsNullOrWhiteSpace(folder) ? null : () => _ = OpenFolderAsync(folder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowNotice(T("ActionFailed"), ex.Message, InfoBarSeverity.Error);
        }
    }

    private static async Task OpenFolderAsync(string folderPath)
    {
        try
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
            await Launcher.LaunchFolderAsync(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            new AppLogger().Warn($"[SessionsHub] Couldn't open exported transcript folder ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// Refreshes the app-wide tracked sessions so the Workspace sidebar reflects a change
    /// made here, even when the Gateway does not push <c>sessions.changed</c>.
    /// </summary>
    private static void RefreshTrackedSessions(IOperatorGatewayClient client) =>
        _ = client.RequestSessionsAsync().ContinueWith(
            task => new AppLogger().Warn($"[SessionsHub] Tracked session refresh failed ({task.Exception?.InnerException?.GetType().Name})."),
            TaskContinuationOptions.OnlyOnFaulted);

    /// <summary>The live binding for an action that prompts first. Without one, explains why and returns null so no prompt opens.</summary>
    private WorkspaceSessionOperation? PromptBinding()
    {
        if (IsCurrent(_binding))
            return _binding;
        ShowNotice("", WorkspaceWindow.Text(_binding is null ? "ConnectionRequired" : "SessionConnectionChanged"), InfoBarSeverity.Error);
        return null;
    }

    private void RequireBound(WorkspaceSessionOperation operation)
    {
        if (!IsCurrent(operation))
            throw new WorkspaceSessionConnectionChangedException();
    }

    private void NotifyRowsUnavailable() =>
        ShowNotice("", T("RosterNotCurrent"), InfoBarSeverity.Informational);

    private bool RequireCurrentRoster()
    {
        if (!IsCurrent(_binding))
        {
            PromptBinding();
            return false;
        }
        if (_state.CanMutateRows)
            return true;
        if (!_state.IsBusy)
            NotifyRowsUnavailable();
        return false;
    }

    private static bool IsRowMutation(WorkspaceSessionMenuAction action) => action is
        WorkspaceSessionMenuAction.TogglePin or WorkspaceSessionMenuAction.Rename or WorkspaceSessionMenuAction.ToggleUnread
        or WorkspaceSessionMenuAction.ToggleArchive or WorkspaceSessionMenuAction.Fork or WorkspaceSessionMenuAction.Reset
        or WorkspaceSessionMenuAction.Compact or WorkspaceSessionMenuAction.Delete;

    /// <param name="binding">The connection the action started on, captured before any prompt. Defaults to the live one.</param>
    private async Task<bool> GuardedAsync(string failureTitle, Func<WorkspaceSessionOperation, Task> work,
        WorkspaceSessionOperation? binding = null, bool requireCurrentRoster = false)
    {
        if (_state.IsBusy)
            return false;
        var operation = binding ?? _binding;
        if (operation is null)
        {
            ShowNotice("", WorkspaceWindow.Text("ConnectionRequired"), InfoBarSeverity.Error);
            return false;
        }
        if (!IsCurrent(operation))
        {
            ShowNotice(failureTitle, WorkspaceWindow.Text("SessionConnectionChanged"), InfoBarSeverity.Error);
            return false;
        }
        if (requireCurrentRoster && !_state.CanMutateRows)
        {
            NotifyRowsUnavailable();
            return false;
        }
        Update(s => s with { IsBusy = true });
        try
        {
            await work(operation);
            RequireBound(operation);
            RefreshTrackedSessions(operation.Client);
            return true;
        }
        catch (WorkspaceSessionConnectionChangedException)
        {
            ShowNotice(failureTitle, WorkspaceWindow.Text("SessionConnectionChanged"), InfoBarSeverity.Error);
        }
        catch (TimeoutException)
        {
            ShowNotice(failureTitle, WorkspaceWindow.Text("SessionActionTimedOut"), InfoBarSeverity.Error);
        }
        catch (NotSupportedException)
        {
            ShowNotice(failureTitle, T("Unsupported"), InfoBarSeverity.Informational);
        }
        catch (InvalidOperationException ex)
        {
            ShowNotice(failureTitle, ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            // A new connection already reset the busy flag; never clear one it set.
            if (ReferenceEquals(operation, _binding))
                Update(s => s with { IsBusy = false });
        }
        return false;
    }

    public Task PatchAsync(SessionInfo row, SessionPatch patch, WorkspaceSessionOperation? binding = null) =>
        GuardedAsync(T("UpdateFailed"), async operation =>
        {
            await operation.PatchAsync(row.Key, patch);
            await ReloadAsync(preserveCount: true);
        }, binding, requireCurrentRoster: true);

    public async Task SaveLabelAsync(SessionInfo row, string text)
    {
        var trimmed = text.Trim();
        if (trimmed == (row.Label ?? "").Trim())
            return;
        if (!await GuardedAsync(T("UpdateFailed"), operation =>
                operation.PatchAsync(row.Key, WorkspaceSessionMenu.RenamePatch(trimmed)),
                requireCurrentRoster: true))
            return;
        Update(s => s with
        {
            // The acknowledgement owns only the submitted draft, not text typed afterward.
            LabelDraft = s.LabelDraft.Key == row.Key && s.LabelDraft.Text == text && s.LabelDraft.SessionId == row.SessionId
                ? (null, "", null) : s.LabelDraft,
            Rows = s.Rows.Select(existing =>
            {
                if (existing.Key != row.Key || existing.SessionId != row.SessionId) return existing;
                var accepted = existing.Clone();
                accepted.Label = trimmed.Length == 0 ? null : trimmed;
                return accepted;
            }).ToArray(),
        });
        await ReloadAsync(preserveCount: true);
    }

    public async Task ArchiveAsync(SessionInfo row, string title)
    {
        if (!WorkspaceSessionMenu.CanArchiveOrDelete(MainState(row)))
        {
            ShowNotice("", T("MainCannotArchive"), InfoBarSeverity.Informational);
            return;
        }
        var binding = _binding;
        var archived = await GuardedAsync(T("ArchiveFailed"), operation => operation.PatchAsync(row.Key, new SessionPatch { Archived = true }),
            binding, requireCurrentRoster: true);
        if (!archived) return;
        CloseDetails(row.Key);
        // Undo stays bound to the connection that archived the row.
        var channel = SessionDisplayResolver.Resolve(row).Channel;
        var ambiguous = _state.Rows.Any(other => other.Key != row.Key
            && OpenClawTray.Services.SessionTitleFormatter.Format(other) == OpenClawTray.Services.SessionTitleFormatter.Format(row));
        var message = ambiguous && !string.IsNullOrWhiteSpace(channel)
            ? F("ArchivedNamedChannel", title, channel)
            : F("ArchivedFormat", title);
        ShowNotice("", message, InfoBarSeverity.Success, T("Undo"), () => _ = UndoArchiveAsync(row, binding), persistent: true);
        await ReloadAsync(preserveCount: true);
    }

    private async Task UndoArchiveAsync(SessionInfo row, WorkspaceSessionOperation? binding)
    {
        var navigationGeneration = _loadGeneration;
        var navigation = _state;
        bool NavigationChanged() => navigation.Tab != _state.Tab || navigation.Filters != _state.Filters
            || navigation.PageIndex != _state.PageIndex || navigation.PageSize != _state.PageSize
            || navigation.GroupBy != _state.GroupBy || navigation.SortColumn != _state.SortColumn
            || navigation.SortDescending != _state.SortDescending;
        var restored = await GuardedAsync(T("UndoFailed"), operation =>
            operation.PatchAsync(row.Key, SessionsHubDeletion.UndoArchivePatch(row.Pinned)), binding, requireCurrentRoster: true);
        if (restored)
        {
            if (!IsCurrent(binding)) return;
            DismissNotice();
            if (navigationGeneration != _loadGeneration || NavigationChanged())
                return;
            var generation = _loadGeneration + 1;
            await ReloadAsync(preserveCount: true);
            if (!IsCurrent(binding) || generation != _loadGeneration || NavigationChanged())
                return;
            if (await LoadAttentionRowAsync(row.Key) && IsCurrent(binding))
                ArchiveRestored?.Invoke(row.Key);
        }
    }

    public async Task UnarchiveAsync(SessionInfo row, string title)
    {
        var restored = await GuardedAsync(T("UnarchiveFailed"), operation =>
            operation.PatchAsync(row.Key, new SessionPatch { Archived = false }), requireCurrentRoster: true);
        if (!restored) return;
        ShowNotice("", F("UnarchivedFormat", title), InfoBarSeverity.Success);
        await ReloadAsync(preserveCount: true);
    }

    /// <summary>Single delete uses the shared lifecycle prompt wording, then the archive-aware delete contract.</summary>
    public async Task DeleteAsync(SessionInfo row, string title)
    {
        if (!RequireCurrentRoster())
            return;
        var mainState = MainState(row);
        if (!SessionActionPlanner.IsAllowed(SessionActionKind.Delete, mainState, out var blocked))
        {
            ShowNotice("", blocked ?? T("DeleteBlocked"), InfoBarSeverity.Informational);
            return;
        }
        if (!_state.Permissions.CanDelete(row))
        {
            ShowNotice("", T("DeleteNeedsAdmin"), InfoBarSeverity.Informational);
            return;
        }
        if (PromptBinding() is not { } binding) return;
        var rowsBefore = _state.Rows;
        var prompt = SessionActionPlanner.BuildPrompt(SessionActionKind.Delete, row.Key, title, false);
        if (prompt is not null)
        {
            var localized = SessionActionPromptLocalizer.Localize(prompt);
            if (!await ConfirmAsync(F("DeleteSessionTitle", title), T("DeleteSessionBody"), localized.ConfirmLabel, destructive: true,
                    icon: FluentIconCatalog.Delete, facts: SessionFacts(row),
                    noteTitle: row.Worktree is null ? null : T("DeleteSessionWorktreeNoteTitle"),
                    noteMessage: row.Worktree is null ? null : T("DeleteSessionWorktreeNoteBody")))
                return;
        }
        if (string.IsNullOrWhiteSpace(row.SessionId)
            && (!ReferenceEquals(rowsBefore, _state.Rows) || _state.IsReloading))
        {
            ShowNotice("", T("DeleteSelectionChanged"), InfoBarSeverity.Informational);
            return;
        }
        await DeleteRowsAsync([row], T("DeleteFailed"), binding);
    }

    public async Task DeleteSelectedAsync()
    {
        if (!RequireCurrentRoster())
            return;
        // Only rows the user can currently see; a hidden selection must never be deleted.
        var rows = _state.Filters.ApplyClientFilters(_state.Rows).Where(row => _state.Selection.Contains(row.Key)).ToArray();
        if (rows.Length == 0) return;
        var plan = SessionsHubDeletion.Plan(rows, _state.Permissions, IsMain);
        if (plan.Requests.Count == 0)
        {
            ShowNotice("", T("DeleteSelectedNoneAllowed"), InfoBarSeverity.Informational);
            return;
        }
        var body = plan.Requests.Count == 1 ? T("DeleteSelectedBodyOne") : F("DeleteSelectedBodyMany", plan.Requests.Count);
        if (plan.Blocked.Count > 0)
            body += "\n\n" + F("DeleteSelectedSkipped", plan.Blocked.Count);
        if (PromptBinding() is not { } binding) return;
        var rowsBefore = _state.Rows;
        var kept = plan.Blocked.Select(blocked => new SessionsHubDialogListItem(
            SessionsHubPresentation.Titles([blocked])[blocked.Key],
            IsMain(blocked) ? T("DialogReasonMain") : T("DialogReasonNeedsAdmin"))).ToArray();
        if (!await ConfirmAsync(T("DeleteSelectedTitle"), body, F("DeleteCount", plan.Requests.Count), destructive: true,
                icon: FluentIconCatalog.Delete,
                facts:
                [
                    new SessionsHubDialogFact(T("Selected"), rows.Length.ToString(System.Globalization.CultureInfo.CurrentCulture)),
                    new SessionsHubDialogFact(T("DialogWillDelete"), plan.Requests.Count.ToString(System.Globalization.CultureInfo.CurrentCulture)),
                ],
                itemsLabel: kept.Length > 0 ? T("DialogKept") : null,
                items: kept))
            return;
        // The roster may have reloaded while the dialog was open. Delete only confirmed rows that
        // are still loaded, visible and selected. Requests carry the session id the user confirmed,
        // so the Gateway refuses a replaced session. A row without an id has no such guard, so a
        // reload with one in the plan needs a fresh confirmation.
        // Any change to the loaded rows, including a reload already running when the dialog opened.
        var reloaded = !ReferenceEquals(rowsBefore, _state.Rows) || _state.IsReloading;
        var stillSelected = _state.Filters.ApplyClientFilters(_state.Rows)
            .Where(row => _state.Selection.Contains(row.Key)).Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
        plan = plan with { Requests = plan.Requests.Where(request => stillSelected.Contains(request.Key)).ToArray() };
        if (plan.Requests.Count == 0 || (reloaded && plan.Requests.Any(request => request.ExpectedSessionId is null)))
        {
            ShowNotice("", T("DeleteSelectionChanged"), InfoBarSeverity.Informational);
            return;
        }
        await ExecutePlanAsync(plan, T("DeleteFailed"), binding);
        Update(s => s with { Selection = SessionsHubSelection.Empty, SelectionMode = false });
    }

    /// <summary>Loads every archived page first so the confirmation states the real count.</summary>
    public async Task DeleteAllArchivedAsync()
    {
        if (!RequireCurrentRoster())
            return;
        var binding = _binding;
        if (!IsCurrent(binding) || _state.IsBusy) return;
        var client = binding!.Client;
        var rows = new List<SessionInfo>();
        try
        {
            Update(s => s with { IsBusy = true });
            var archivedFilters = _state.Filters with { Status = SessionArchiveFilter.Archived, Search = "" };
            var offset = 0;
            while (true)
            {
                var page = await client.ListSessionsPageAsync(archivedFilters.ToQuery(offset, 100));
                rows.AddRange(page.Sessions);
                if (!page.HasMore || page.NextOffset is not { } next || next <= offset) break;
                offset = next;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            if (IsCurrent(binding))
                ShowNotice(T("DeleteFailed"), ex.Message, InfoBarSeverity.Error);
            return;
        }
        finally
        {
            if (ReferenceEquals(binding, _binding))
                Update(s => s with { IsBusy = false });
        }
        if (!IsCurrent(binding)) return;
        if (!RequireCurrentRoster()) return;
        var plan = SessionsHubDeletion.PlanDeleteAllArchived(rows, _state.Permissions, IsMain);
        if (plan.Requests.Count == 0)
        {
            ShowNotice("", T("NoArchivedToDelete"), InfoBarSeverity.Informational);
            return;
        }
        var withWorktrees = rows.Count(row => row.Worktree is not null);
        var body = F("DeleteAllArchivedBody", plan.Requests.Count);
        if (withWorktrees > 0)
            body += "\n\n" + (withWorktrees == 1 ? T("DeleteAllArchivedWorktreesOne") : F("DeleteAllArchivedWorktrees", withWorktrees));
        if (!await ConfirmAsync(T("DeleteAllArchivedTitle"), body, F("DeleteCount", plan.Requests.Count), destructive: true,
                icon: FluentIconCatalog.Delete,
                facts:
                [
                    new SessionsHubDialogFact(T("DialogScope"),
                        _state.Filters.AgentId is { } agentId
                            ? _state.Agents.FirstOrDefault(agent => agent.Id == agentId)?.Name ?? agentId
                            : T("AllAgents")),
                    new SessionsHubDialogFact(T("StatArchived"),
                        plan.Requests.Count.ToString(System.Globalization.CultureInfo.CurrentCulture)),
                    new SessionsHubDialogFact(T("DialogNeverDeleted"), T("DialogMainSession")),
                ]))
            return;
        await ExecutePlanAsync(plan, T("DeleteFailed"), binding);
    }

    private Task DeleteRowsAsync(IReadOnlyList<SessionInfo> rows, string failureTitle, WorkspaceSessionOperation? binding) =>
        ExecutePlanAsync(SessionsHubDeletion.Plan(rows, _state.Permissions, IsMain), failureTitle, binding);

    private async Task ExecutePlanAsync(SessionsDeletePlan plan, string failureTitle, WorkspaceSessionOperation? binding)
    {
        var deleted = 0;
        var refused = 0;
        var failures = new List<string>();
        var preserved = new List<PreservedWorktreeInfo>();
        var completed = await GuardedAsync(failureTitle, async operation =>
        {
            foreach (var request in plan.Requests)
            {
                RequireBound(operation);
                try
                {
                    var outcome = await operation.Client.DeleteSessionDetailedAsync(request);
                    if (outcome.Deleted) deleted++;
                    else refused++;
                    if (outcome.PreservedWorktree is { } kept) preserved.Add(kept);
                }
                catch (InvalidOperationException ex) when (ex is not WorkspaceSessionConnectionChangedException)
                {
                    failures.Add(ex.Message);
                }
            }
        }, binding, requireCurrentRoster: true);
        if (!completed && deleted == 0 && refused == 0) return;
        if (!completed && _host.Client is { IsConnectedToGateway: true } current)
            RefreshTrackedSessions(current);
        foreach (var request in plan.Requests)
            CloseDetails(request.Key);
        var message = deleted == 1 ? T("DeletedOne") : F("DeletedMany", deleted);
        if (failures.Count > 0)
            message += " " + F("DeleteFailures", failures.Count, failures[0]);
        // An interrupted run (timeout or connection change) left the rest unattempted or unknown.
        if (refused > 0)
            message += " " + F("DeleteRefused", refused);
        var unconfirmed = completed ? 0 : plan.Requests.Count - deleted - refused - failures.Count;
        if (unconfirmed > 0)
            message += " " + F("DeleteInterrupted", unconfirmed);
        if (preserved.Count > 0)
            message += " " + F("PreservedWorktrees", string.Join(", ", preserved.Select(w => $"{w.Branch} ({PreservationReason(w.Reason)})")));
        var severity = failures.Count > 0 || refused > 0 || unconfirmed > 0 ? InfoBarSeverity.Warning
            : preserved.Count > 0 ? InfoBarSeverity.Informational : InfoBarSeverity.Success;
        ShowNotice("", message, severity);
        await ReloadAsync(preserveCount: true);
    }

    internal static string PreservationReason(string reason) => reason switch
    {
        "owner-mismatch" => T("PreservedOwnerMismatch"),
        "busy" => T("PreservedBusy"),
        "foreign-lock" => T("PreservedLocked"),
        "snapshot-failed" => T("PreservedSnapshotFailed"),
        _ => T("PreservedCleanupFailed"),
    };

    public async Task AssignOwnerAsync(SessionInfo row, string type, string id, int ownerCatalogGeneration)
    {
        if (type == "human" &&
            (ownerCatalogGeneration != _profileGeneration || !_profilesLoaded ||
             (id != _state.SelfProfileId && _state.Profiles.All(profile => profile.Id != id))))
        {
            ShowNotice(T("AssignFailed"), T("OwnerCatalogChanged"), InfoBarSeverity.Informational);
            return;
        }
        var assigned = await GuardedAsync(T("AssignFailed"), async operation =>
        {
            await operation.Client.AssignSessionOwnerAsync(row.Key, SessionsHubProjection.AgentId(row), type, id);
        }, requireCurrentRoster: true);
        if (assigned)
            await ReloadAsync(preserveCount: true);
    }

    public async Task StopCloudWorkerAsync(SessionInfo row, string title)
    {
        if (!RequireCurrentRoster())
            return;
        if (PromptBinding() is not { } binding) return;
        if (!await ConfirmAsync(T("StopWorkerTitle"), F("StopWorkerBody", title), T("StopWorker"), destructive: true,
                icon: FluentIconCatalog.Stop,
                facts:
                [
                    new SessionsHubDialogFact(T("WorktreeOwnerSession"), title),
                    new SessionsHubDialogFact(T("CloudWorker"), T("PlacementActive")),
                ]))
            return;
        var stopped = await GuardedAsync(T("StopWorkerFailed"),
            operation => operation.Client.ReclaimSessionAsync(row.Key, SessionsHubProjection.AgentId(row)),
            binding, requireCurrentRoster: true);
        if (!stopped) return;
        ShowNotice("", F("StopWorkerDone", title), InfoBarSeverity.Success);
        await ReloadAsync(preserveCount: true);
    }

    public Task ShowCheckpointsAsync(SessionInfo row, string title) =>
        SessionCheckpointDialogCoordinator.ShowAsync(
            _host.XamlRoot!,
            row.Key,
            isHostAvailable: () => _host.XamlRoot is not null,
            showStatusAsync: (noticeTitle, message, severity) =>
            {
                ShowNotice(noticeTitle, message, severity);
                return Task.CompletedTask;
            },
            displayName: title,
            rowIsMain: row.IsMain);

    // ── Groups ──

    public IReadOnlyList<string> KnownGroups() =>
        SessionsHubProjection.KnownCategories(_state.GroupCatalog, _state.Rows.Select(row => row.Category));

    public async Task MoveToGroupAsync(SessionInfo row, string? group, WorkspaceSessionOperation? binding = null)
    {
        // The Gateway registers a category when patching membership. Do not replace the catalog.
        await PatchAsync(row, new SessionPatch { Category = group is null ? SessionPatch.Clear : group }, binding);
    }

    public async Task NewGroupAsync(SessionInfo? moveRow = null)
    {
        if (moveRow is not null && !RequireCurrentRoster())
            return;
        if (PromptBinding() is not { } binding) return;
        if (!_state.Permissions.CanCreateGroup)
        {
            ShowNotice("", T(_state.Permissions.SupportsGroupCreation ? "NeedsWrite" : "Unsupported"),
                InfoBarSeverity.Informational);
            return;
        }
        var name = await PromptAsync(T("NewGroupTitle"), T("NewGroupBody"), T("Create"),
            placeholder: T("NewGroupPlaceholder"), icon: FluentIconCatalog.Add, inputLabel: T("Label"));
        if (name is null) return;
        if (moveRow is not null && !RequireCurrentRoster())
            return;
        if (!await CreateGroupAsync(name, binding)) return;
        if (moveRow is not null)
            await MoveToGroupAsync(moveRow, name, binding);
        else
            await ReloadAsync(preserveCount: true);
    }

    private Task<bool> CreateGroupAsync(string name, WorkspaceSessionOperation binding) =>
        GuardedAsync(T("GroupFailed"), async operation =>
        {
            var catalog = await operation.Client.CreateSessionGroupAsync(name);
            RequireBound(operation);
            Update(s => s with { GroupCatalog = catalog.Names });
        }, binding);

    public async Task RenameGroupAsync(string name)
    {
        if (PromptBinding() is not { } binding) return;
        var to = await PromptAsync(T("RenameGroupTitle"), T("RenameGroupBody"), T("Save"), name,
            icon: FluentIconCatalog.Rename, inputLabel: T("Label"));
        if (to is null || to == name) return;
        var renamed = await GuardedAsync(T("GroupFailed"), async operation =>
        {
            var catalog = await operation.Client.RenameSessionGroupAsync(name, to);
            RequireBound(operation);
            Update(s => s with { GroupCatalog = catalog.Names });
        }, binding);
        if (renamed) await ReloadAsync(preserveCount: true);
    }

    public async Task DeleteGroupAsync(string name)
    {
        if (PromptBinding() is not { } binding) return;
        if (!await ConfirmAsync(T("DeleteGroupTitle"), F("DeleteGroupBody", name), T("DeleteGroup"), destructive: true,
                icon: FluentIconCatalog.Delete))
            return;
        var deleted = await GuardedAsync(T("GroupFailed"), async operation =>
        {
            var catalog = await operation.Client.DeleteSessionGroupAsync(name);
            RequireBound(operation);
            Update(s => s with { GroupCatalog = catalog.Names });
        }, binding);
        if (deleted) await ReloadAsync(preserveCount: true);
    }

    // ── Transcript search ──

    public void SetTranscriptOpen(bool open) => Update(s => s with { Transcript = s.Transcript with { IsOpen = open } });
    public void SetTranscriptQuery(string query)
    {
        if (query.Trim() != _state.Transcript.Query.Trim())
            InvalidateTranscriptSearch();
        Update(s => s with { Transcript = s.Transcript with { Query = query } });
    }

    private void InvalidateTranscriptSearch()
    {
        _transcriptGeneration++;
        Update(s => s with { Transcript = s.Transcript with { IsSearching = false, Result = null, Error = null } });
    }

    public async Task RunTranscriptSearchAsync()
    {
        var generation = ++_transcriptGeneration;
        var query = _state.Transcript.Query.Trim();
        if (query.Length == 0)
        {
            Update(s => s with { Transcript = s.Transcript with { Result = null, Error = null, IsSearching = false } });
            return;
        }
        if (_host.Client is not { IsConnectedToGateway: true } client)
        {
            Update(s => s with { Transcript = s.Transcript with { Error = T("TranscriptNeedsConnection") } });
            return;
        }
        Update(s => s with { Transcript = s.Transcript with { IsSearching = true, Result = null, Error = null } });
        try
        {
            var result = await client.SearchSessionTranscriptsAsync(query, _state.Filters.ToQuery());
            if (generation != _transcriptGeneration) return;
            Update(s => s with
            {
                Transcript = s.Transcript with
                {
                    IsSearching = false,
                    Result = result,
                    Error = result.IsSupported ? null : T("TranscriptUnsupported"),
                },
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            if (generation != _transcriptGeneration) return;
            Update(s => s with { Transcript = s.Transcript with { IsSearching = false, Error = ex.Message } });
        }
    }

    // ── Worktrees ──

    public async Task LoadWorktreesAsync()
    {
        if (_host.Client is not { IsConnectedToGateway: true } client) return;
        if (!_state.Permissions.Supports("worktrees.list"))
        {
            Update(s => s with { Worktrees = new WorktreesState(SessionsHubLoad.Unsupported) });
            return;
        }
        var generation = ++_worktreeGeneration;
        Update(s => s with { Worktrees = s.Worktrees with { Load = SessionsHubLoad.Loading, Error = null } });
        try
        {
            var rows = await client.ListWorktreesAsync();
            if (generation != _worktreeGeneration) return;
            Update(s => s with
            {
                Worktrees = rows is null
                    ? new WorktreesState(SessionsHubLoad.Unsupported)
                    : s.Worktrees with { Load = SessionsHubLoad.Loaded, Rows = WorktreesProjection.Sort(rows) },
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            if (generation == _worktreeGeneration)
                Update(s => s with { Worktrees = s.Worktrees with { Load = SessionsHubLoad.Failed, Error = ex.Message } });
        }
    }

    private async Task<bool> WorktreeOperationAsync(string? busyId, string failureTitle, Func<IOperatorGatewayClient, Task> work,
        WorkspaceSessionOperation? binding = null)
    {
        var operation = binding ?? _binding;
        if (operation is null || _state.Worktrees.BusyId is not null || _state.Worktrees.IsCleaning)
            return false;
        if (!IsCurrent(operation))
        {
            ShowNotice(failureTitle, WorkspaceWindow.Text("SessionConnectionChanged"), InfoBarSeverity.Error);
            return false;
        }
        Update(s => s with { Worktrees = s.Worktrees with { BusyId = busyId ?? "" } });
        try
        {
            await work(operation.Client);
            RequireBound(operation);
            return true;
        }
        catch (WorkspaceSessionConnectionChangedException)
        {
            ShowNotice(failureTitle, WorkspaceWindow.Text("SessionConnectionChanged"), InfoBarSeverity.Error);
        }
        catch (NotSupportedException)
        {
            ShowNotice(failureTitle, T("Unsupported"), InfoBarSeverity.Informational);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            ShowNotice(failureTitle, ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (ReferenceEquals(operation, _binding))
                Update(s => s with { Worktrees = s.Worktrees with { BusyId = null } });
        }
        return false;
    }

    public async Task CreateWorktreeAsync()
    {
        var dialog = new SessionsHubDialog(SessionsHubDialogKind.NewWorktree, T("NewWorktreeTitle"), "", T("NewWorktreeCreate"),
            false, new TaskCompletionSource<SessionsHubDialog?>())
        {
            Repo = DefaultWorktreeRepo(),
            Icon = FluentIconCatalog.Add,
        };
        if (PromptBinding() is not { } binding) return;
        var pending = OpenDialog(dialog);
        // Mount the controlled ContentDialog before branch loading can invalidate it again.
        // The queued load rechecks the live dialog, connection binding and generation.
        _post(() => _ = LoadDialogBranchesAsync());
        var request = await pending;
        if (request is null) return;
        WorktreeRecord? createdRecord = null;
        var created = await WorktreeOperationAsync(null, T("WorktreeCreateFailed"),
            async client => createdRecord = await client.CreateWorktreeAsync(
                request.Repo.Trim(), NullIfBlank(request.Name), NullIfBlank(request.BaseRef)), binding);
        if (!created || createdRecord is null) return;
        ShowNotice("", T("WorktreeCreated"), InfoBarSeverity.Success);
        await LoadWorktreesAsync();
    }

    /// <summary>The default agent's configured workspace, else the most recently used repository.</summary>
    private string DefaultWorktreeRepo()
    {
        if (_host.AgentsList is { ValueKind: JsonValueKind.Object } root
            && root.TryGetProperty("agents", out var agents) && agents.ValueKind == JsonValueKind.Array)
        {
            var defaultId = root.TryGetProperty("defaultId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            foreach (var agent in agents.EnumerateArray())
            {
                if (agent.ValueKind == JsonValueKind.Object
                    && (defaultId is null || (agent.TryGetProperty("id", out var agentId) && agentId.GetString() == defaultId))
                    && agent.TryGetProperty("workspace", out var workspace) && workspace.ValueKind == JsonValueKind.String)
                    return workspace.GetString() ?? "";
            }
        }
        return _state.Worktrees.Rows?.FirstOrDefault()?.RepoRoot ?? "";
    }

    public async Task RemoveWorktreeAsync(WorktreeRecord record)
    {
        if (PromptBinding() is not { } binding) return;
        if (!await ConfirmAsync(T("RemoveWorktreeTitle"), F("RemoveWorktreeBody", record.Name), T("Remove"), destructive: true,
                icon: FluentIconCatalog.Delete, facts: WorktreeFacts(record)))
            return;
        WorktreeRemoveResult? result = null;
        if (!await WorktreeOperationAsync(record.Id, T("RemoveWorktreeFailed"),
                async client => result = await client.RemoveWorktreeAsync(record.Id, force: false), binding))
            return;
        if (result is { Removed: false })
        {
            // A refusal from an older connection must not prompt for force on the new one.
            if (!IsCurrent(binding))
                return;
            var reason = result.SnapshotError ?? T("RemoveWorktreeRefused");
            if (!await ConfirmAsync(T("ForceRemoveTitle"), F("ForceRemoveBody", record.Name, reason), T("ForceRemove"), destructive: true,
                    icon: FluentIconCatalog.StatusWarn, facts: WorktreeFacts(record),
                    noteTitle: T("ForceRemoveTitle"), noteMessage: reason))
            {
                await LoadWorktreesAsync();
                return;
            }
            if (!await WorktreeOperationAsync(record.Id, T("RemoveWorktreeFailed"),
                    async client => result = await client.RemoveWorktreeAsync(record.Id, force: true), binding))
                return;
        }
        if (result?.Removed != true)
        {
            ShowNotice(T("RemoveWorktreeFailed"), result?.SnapshotError ?? T("RemoveWorktreeRefused"), InfoBarSeverity.Warning);
            await LoadWorktreesAsync();
            return;
        }
        ShowNotice("", F("WorktreeRemoved", record.Name), InfoBarSeverity.Success,
            T("Restore"), () => _ = RestoreWorktreeAsync(record));
        await LoadWorktreesAsync();
    }

    public async Task RestoreWorktreeAsync(WorktreeRecord record)
    {
        WorktreeRecord? restoredRecord = null;
        if (!await WorktreeOperationAsync(record.Id, T("RestoreWorktreeFailed"),
                async client => restoredRecord = await client.RestoreWorktreeAsync(record.Id))
            || restoredRecord is null)
            return;
        ShowNotice("", F("WorktreeRestored", record.Name), InfoBarSeverity.Success);
        await LoadWorktreesAsync();
    }

    public async Task CleanWorktreesAsync()
    {
        var binding = _binding;
        if (!IsCurrent(binding) || _state.Worktrees.IsCleaning) return;
        var client = binding!.Client;
        Update(s => s with { Worktrees = s.Worktrees with { IsCleaning = true } });
        try
        {
            var result = await client.RunWorktreeGcAsync();
            for (var poll = 0; result.IsPending && result.JobId is { } jobId && poll < GcMaxPolls && IsCurrent(binding); poll++)
            {
                await Task.Delay(GcPollIntervalMs);
                if (!IsCurrent(binding))
                    break;
                result = await client.RunWorktreeGcAsync(jobId);
            }
            if (!IsCurrent(binding))
                return;
            if (result.State == "failed" || result.Outcome == "partial" || result.Error is not null)
                ShowNotice(T("CleanFailed"), result.Error ?? string.Join("; ", result.IssueReasons), InfoBarSeverity.Warning);
            else if (result.IsPending)
                ShowNotice("", T("CleanStillRunning"), InfoBarSeverity.Informational);
            else
            {
                var summary = F("CleanDone", result.Removed.Count, result.OrphansDeleted, result.SnapshotsPruned);
                if (result.IssueReasons.Count > 0)
                    summary += " " + F("CleanIssues", string.Join("; ", result.IssueReasons));
                ShowNotice("", summary, result.IssueReasons.Count > 0 ? InfoBarSeverity.Informational : InfoBarSeverity.Success);
            }
        }
        catch (NotSupportedException)
        {
            if (IsCurrent(binding))
                ShowNotice(T("CleanFailed"), T("Unsupported"), InfoBarSeverity.Informational);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            if (IsCurrent(binding))
                ShowNotice(T("CleanFailed"), ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (ReferenceEquals(binding, _binding))
                Update(s => s with { Worktrees = s.Worktrees with { IsCleaning = false } });
        }
        if (IsCurrent(binding))
            await LoadWorktreesAsync();
    }

    // ── Dialogs (controlled Reactor ContentDialog elements) ──

    private Task<SessionsHubDialog?> OpenDialog(SessionsHubDialog dialog)
    {
        _state.Dialog?.Completion.TrySetResult(null);
        Update(s => s with { Dialog = dialog });
        return dialog.Completion.Task;
    }

    public void UpdateDialog(Func<SessionsHubDialog, SessionsHubDialog> change) =>
        Update(s => s.Dialog is null ? s : s with { Dialog = change(s.Dialog) });

    /// <summary>Resolves the open dialog. Confirmation requires the dialog's inputs to be valid.</summary>
    public void CloseDialog(bool confirmed)
    {
        var dialog = _state.Dialog;
        if (dialog is null) return;
        Update(s => s with { Dialog = null });
        dialog.Completion.TrySetResult(confirmed && dialog.IsValid ? dialog : null);
    }

    private async Task<bool> ConfirmAsync(
        string title,
        string body,
        string confirmLabel,
        bool destructive,
        string? icon = null,
        IReadOnlyList<SessionsHubDialogFact>? facts = null,
        string? itemsLabel = null,
        IReadOnlyList<SessionsHubDialogListItem>? items = null,
        string? noteTitle = null,
        string? noteMessage = null) =>
        await OpenDialog(new SessionsHubDialog(SessionsHubDialogKind.Confirm, title, body, confirmLabel, destructive,
            new TaskCompletionSource<SessionsHubDialog?>())
        {
            Icon = icon ?? (destructive ? FluentIconCatalog.Delete : FluentIconCatalog.StatusOk),
            Facts = facts ?? Array.Empty<SessionsHubDialogFact>(),
            ItemsLabel = itemsLabel,
            Items = items ?? Array.Empty<SessionsHubDialogListItem>(),
            NoteTitle = noteTitle,
            NoteMessage = noteMessage,
        }) is not null;

    private async Task<string?> PromptAsync(
        string title,
        string body,
        string confirmLabel,
        string initial = "",
        string? placeholder = null,
        string? icon = null,
        string? inputLabel = null,
        IReadOnlyList<SessionsHubDialogFact>? facts = null) =>
        (await OpenDialog(new SessionsHubDialog(SessionsHubDialogKind.Prompt, title, body, confirmLabel, false,
            new TaskCompletionSource<SessionsHubDialog?>())
        {
            Text = initial,
            Placeholder = placeholder,
            Icon = icon ?? FluentIconCatalog.Rename,
            InputLabel = inputLabel,
            Facts = facts ?? Array.Empty<SessionsHubDialogFact>(),
        }))?.Text.Trim();

    private IReadOnlyList<SessionsHubDialogFact> SessionFacts(SessionInfo row)
    {
        var display = SessionDisplayResolver.Resolve(row);
        var facts = new List<SessionsHubDialogFact>
        {
            new(T("FactAgent"), display.AgentId is { } agentId
                ? _state.Agents.FirstOrDefault(agent => agent.Id == agentId)?.Name ?? agentId
                : T("NotAvailable")),
            new(T("FactUpdated"), SessionsHubPresentation.AbsoluteTime(row.UpdatedAt)),
        };
        if (row.Worktree is { } worktree)
            facts.Add(new SessionsHubDialogFact(T("FactWorktree"), $"{worktree.Branch} · {WorktreesProjection.RepoName(worktree.RepoRoot ?? "")}"));
        return facts;
    }

    private static IReadOnlyList<SessionsHubDialogFact> WorktreeFacts(WorktreeRecord record) =>
    [
        new(T("NewWorktreeBase"), record.Branch),
        new(T("FactPath"), record.Path ?? record.RepoRoot),
        new(T("WorktreeColOwner"), record.OwnerKind),
    ];

    /// <summary>Loads branches for the dialog's repository; the base field stays editable when they are unavailable.</summary>
    public async Task LoadDialogBranchesAsync()
    {
        var dialog = _state.Dialog;
        var repo = dialog?.Repo.Trim() ?? "";
        var binding = _binding;
        if (dialog is not { Kind: SessionsHubDialogKind.NewWorktree } || repo.Length == 0
            || !IsCurrent(binding))
            return;
        var generation = ++_branchGeneration;
        UpdateDialog(d => d with { Status = T("NewWorktreeBranchesLoading") });
        WorktreeBranchList? result = null;
        try { result = await binding!.Client.ListWorktreeBranchesAsync(repo); }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or NotSupportedException) { }
        if (generation != _branchGeneration || !IsCurrent(binding)) return;
        UpdateDialog(d =>
        {
            if (!ReferenceEquals(d.Completion, dialog.Completion) || d.Repo.Trim() != repo) return d;
            if (result is null || result.BranchesUnavailable || result.Branches.Count == 0)
                return d with
                {
                    Branches = Array.Empty<string>(),
                    Status = result?.RepositoryStatus == "not_git" ? T("NewWorktreeNotGit") : T("NewWorktreeBranchesUnavailable"),
                };
            return d with
            {
                Branches = result.Branches.Select(branch => branch.Name).ToArray(),
                BaseRef = d.BaseRef.Length > 0 ? d.BaseRef : result.DefaultBranch ?? result.HeadBranch ?? result.Branches[0].Name,
                Status = null,
            };
        });
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
